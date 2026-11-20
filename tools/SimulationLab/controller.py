"""Owned loopback simulation runtime. All business actions use production HTTP APIs."""
import argparse
import copy
import hashlib
import hmac
import json
import math
import os
import re
from pathlib import Path
import socket
import shutil
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.request import Request, urlopen
from urllib.parse import urlsplit, parse_qs, urlencode
import uuid
from datetime import datetime, timezone

WORKSPACE = Path(__file__).resolve().parents[2]
STATION_IDS = ('CNC-001', 'CNC-002')
VEHICLE_ID = re.compile(r'^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$')
TASK_ID = re.compile(r'^[A-Za-z0-9_-]{1,128}$')
SOURCE_SHA256 = re.compile(r'^[a-f0-9]{64}$')
POINT_TABLE_SOURCE_FIELDS = ('sourceSha256', 'sourceFile', 'sourceDeviceId', 'sourceDeviceName')
OBSTACLE_RECOVERY_TIMING = {
    'DetourAfterSeconds': 30,
    'CancelAfterSeconds': 120,
    'CancelTerminalWaitWarningSeconds': 30,
    'WarningAfterSeconds': 60,
    'AlarmAfterSeconds': 120,
    'ClosureSuggestionAfterSeconds': 300,
}


class CleanupPending(RuntimeError):
    pass


def _transient_file_lock(error):
    return isinstance(error, PermissionError) or getattr(error, 'winerror', None) in (5, 32, 33)


def read(path):
    source = Path(path)
    for attempt in range(20):
        try:
            return json.loads(source.read_text(encoding='utf-8-sig'))
        except OSError as error:
            if not _transient_file_lock(error) or attempt == 19:
                raise
            time.sleep(.02 * (attempt + 1))


def write(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')
    try:
        for attempt in range(20):
            try:
                temporary.replace(path)
                return
            except OSError as error:
                if not _transient_file_lock(error) or attempt == 19:
                    raise
                time.sleep(.02 * (attempt + 1))
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass


def rename_evidence(source, target):
    """Same-directory rename only: never copy then delete a locked evidence file."""
    source, target = Path(source), Path(target)
    if source.resolve().parent != target.resolve().parent:
        raise ValueError('启动证据只允许在原实例目录内重命名。')
    for attempt in range(20):
        try:
            source.rename(target)
            return
        except OSError as error:
            if not _transient_file_lock(error) or attempt == 19:
                raise
            time.sleep(.02 * (attempt + 1))


def validate_root(root):
    unresolved = Path(root).absolute()
    for path in (unresolved, *unresolved.parents):
        if path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction()):
            raise ValueError('仿真目录不能通过链接重定向。')
    root = unresolved.resolve()
    artifacts = (WORKSPACE / 'artifacts').resolve()
    if root.parent != artifacts or not root.name.startswith('web-single-vehicle-'):
        raise ValueError('必须选择项目 artifacts/web-single-vehicle-* 独立仿真目录。')
    for path in (root, *root.parents):
        if path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction()):
            raise ValueError('仿真目录不能通过链接重定向。')
    lab = read(root / 'lab.json')
    settings = read(root / 'service/appsettings.json')
    web = read(root / 'webhost/appsettings.json')['Web']
    if lab.get('simulationOnly') is not True or settings['HandshakeExecution']['PlcContractVersion'] != 'plc-28-stage-v2':
        raise ValueError('需要明确标记的28阶段仿真实例。')
    devices = [settings['Plc'], *settings['Agv']['Robots']]
    saved = root / 'data/communication-settings.json'
    if saved.exists():
        communication = read(saved)['Configuration']
        devices += [*communication['Plcs'], *communication['Agvs']]
    if any(d['IpAddress'] != '127.0.0.1' for d in devices):
        raise ValueError('仿真只能连接回环设备。')
    if Path(settings['Database']['Path']).resolve() != root / 'data/agv-dispatch.db':
        raise ValueError('数据库必须属于本仿真目录。')
    if not lab['pipe'].startswith('Agv28StageLab-') or settings['Security']['PipeName'] != lab['pipe'] or web['PipeName'] != lab['pipe']:
        raise ValueError('仿真后台管道不匹配。')
    if web['Port'] != lab['webPort'] or web['Port'] in (5198, 5199) or type(settings['TaskCreation'].get('AutomaticCreationEnabled')) is not bool:
        raise ValueError('仿真需要独立临时Web端口和明确的PLC接单模式。')
    for key in ('ProjectStorePath', 'EngineeringStorePath'):
        if key in web and not Path(web[key]).resolve().is_relative_to(root):
            raise ValueError('仿真工程数据必须位于本目录。')
    ports = [web['Port'], settings['Plc']['Port']]
    for robot in settings['Agv']['Robots']:
        ports += [robot['Port'], robot['NavigationPort']]
    if len(set(ports)) != len(ports) or any(type(p) is not int or not 1024 <= p <= 65535 or p in (5198, 5199) for p in ports):
        raise ValueError('仿真端口无效或重复。')
    return root, lab, settings, web, ports


class Runtime:
    def __init__(self, root, template_root=None, scheme_store=None):
        self.root, self.lab, self.settings, self.web, self.ports = validate_root(root)
        from loopback_http import LoopbackJsonClient
        self.api_client = LoopbackJsonClient(self.web['Port'])
        self.template_root = Path(template_root or self.root)
        self.lock = threading.RLock()
        self.children = {}
        self.abort = threading.Event()
        self.phase = 'stopped'
        self.error = None
        self.worker = None
        self.scenario = {'status': 'idle', 'completedRounds': 0, 'totalRounds': 0}
        self.report = read(self.root / 'simulation-report.json') if (self.root / 'simulation-report.json').exists() else None
        self.controls = {'sequence': uuid.uuid4().hex, 'vehicles': {}, 'plc': {}}
        self.protocol_runtime = None
        self.equipment_model = None
        self.scheme_store = scheme_store
        self.history_cache = (0, [])
        self.guidance_cache = (0, [])
        self.recovery_cache = (0, {})
        self.plc_table_cache = None
        self.startup_plan = None
        self.startup_checks = []
        self.startup_thread = None
        if scheme_store is not None:
            from versions import VersionComparison
            self.version_comparison = VersionComparison(self)

    def api(self, path, data=None, allow_rejection=False, *, method=None):
        if self.abort.is_set():
            raise InterruptedError('场景已停止，拒绝继续提交后台操作。')
        # The normal WebHost pipe operation is bounded at 25 seconds. A shorter
        # client timeout can abandon a successful task creation before its receipt.
        try:
            result = (self.api_client.request(path, data) if method is None
                      else self.api_client.request(path, data, method=method))
        except TimeoutError as error:
            raise TimeoutError(f'等待仿真后台接口 {path} 返回超时，请核对原请求结果。') from error
        if not allow_rejection and isinstance(result, dict) and result.get('accepted') is False:
            raise RuntimeError(f'{path}: {result}')
        return result

    def dispatch_diagnostics(self, path, update=None):
        with self.lock:
            if self.phase != 'running':
                raise ValueError('请先启动隔离实例。')
            instance = self.root.name
            if update is None:
                return {**self.api('/api' + path), 'simulationInstance': instance}
            if set(update) != {'simulationInstance', 'expectedRevision', 'options'} or update['simulationInstance'] != instance:
                raise ValueError('仿真实例已变化，请刷新调度策略后再保存。')
            request = {key: update[key] for key in ('expectedRevision', 'options')}
            if not isinstance(request['expectedRevision'], str) or not request['expectedRevision'] or not isinstance(request['options'], dict):
                raise ValueError('调度策略必须包含配置和当前修订。')
            result = self.api('/api/dispatch-optimization/options', request, allow_rejection=True, method='PUT')
            return {**result, 'simulationInstance': instance}

    def device(self):
        path = self.root / ('multi-state.json' if self.lab.get('twoVehicleSimulation') or self.lab.get('multiVehicleSimulation') else 'device-state.json')
        try:
            value = read(path)
            if 'cars' not in value:
                value['cars'] = {self.settings['Agv']['Robots'][0]['VehicleId']: dict(value)}
            value.setdefault('time', path.stat().st_mtime)
            return value
        except (OSError, ValueError):
            return {}

    def task_page(self, query=None):
        query = query or {}
        allowed = {'filter', 'search', 'page', 'pageSize'}
        if not set(query).issubset(allowed):
            raise ValueError('任务查询参数无效。')
        values = {key: query[key][0] for key in query}
        try:
            page = int(values.get('page', '1'))
            page_size = int(values.get('pageSize', '200'))
        except ValueError as error:
            raise ValueError('任务分页参数无效。') from error
        if page < 1 or not 1 <= page_size <= 200 or len(values.get('search', '')) > 128:
            raise ValueError('任务查询参数无效。')
        values['page'] = str(page)
        values['pageSize'] = str(page_size)
        suffix = ('?' + urlencode(values)) if values else ''
        with self.lock:
            try:
                if values.get('filter', 'All') == 'Active':
                    # The production task API exposes disjoint Waiting and Running
                    # filters. "Active" is a presentation-level union; forwarding it
                    # directly returns 400 and hides every active task.
                    rows = []
                    summary = None
                    for filter_name in ('Waiting', 'Running'):
                        source_page = 1
                        while True:
                            live = self.api('/api/tasks?' + urlencode({
                                'filter': filter_name,
                                'page': str(source_page),
                                'pageSize': '200',
                            }))
                            rows.extend(live.get('items') or [])
                            summary = summary or live.get('summary')
                            total = int(live.get('totalCount') or 0)
                            if source_page * 200 >= total:
                                break
                            source_page += 1
                    search = values.get('search', '').strip().casefold()
                    if search:
                        rows = [row for row in rows if search in ' '.join(
                            str(row.get(key) or '') for key in
                            ('taskId', 'stationId', 'vehicleId', 'stage')).casefold()]
                    rows.sort(key=lambda row: row.get('createdAt') or '', reverse=True)
                    start = (page - 1) * page_size
                    return dict(
                        items=rows[start:start + page_size],
                        totalCount=len(rows),
                        page=page,
                        pageSize=page_size,
                        summary=summary or dict(
                            total=len(rows),
                            running=len(rows),
                            waiting=0,
                            completed=0,
                            cancelled=0,
                            failed=0,
                            other=0,
                        ),
                    )
                return self.api('/api/tasks' + suffix)
            except Exception as live_error:
                rows = self._report_task_rows()
                if rows is None:
                    raise live_error
                filter_name = values.get('filter', 'All')
                search = values.get('search', '').strip().casefold()
                failed_stages = {'Faulted', 'Failed', 'CancellationReview'}
                if filter_name == 'Active':
                    rows = [row for row in rows if row['stage'] not in {'Completed', 'Cancelled'} | failed_stages]
                elif filter_name == 'Completed':
                    rows = [row for row in rows if row['stage'] == 'Completed']
                elif filter_name == 'Failed':
                    rows = [row for row in rows if row['stage'] in failed_stages]
                if search:
                    rows = [row for row in rows if search in ' '.join(
                        str(row.get(key) or '') for key in ('taskId', 'stationId', 'vehicleId', 'stage')).casefold()]
                start = (page - 1) * page_size
                all_rows = self._report_task_rows() or []
                return dict(items=rows[start:start + page_size], totalCount=len(rows), page=page,
                            pageSize=page_size, summary=dict(
                                total=len(all_rows),
                                running=sum(row['stage'] in {'Running', 'Dispatching', 'InProgress'} for row in all_rows),
                                waiting=sum(row['stage'] in {'Created', 'WaitingForVehicle', 'WaitingForDumper',
                                                             'WaitingForPlcConfirmation'} for row in all_rows),
                                completed=sum(row['stage'] == 'Completed' for row in all_rows),
                                cancelled=sum(row['stage'] == 'Cancelled' for row in all_rows),
                                failed=sum(row['stage'] in failed_stages for row in all_rows),
                                other=sum(row['stage'] not in {
                                    'Running', 'Dispatching', 'InProgress', 'Created', 'WaitingForVehicle',
                                    'WaitingForDumper', 'WaitingForPlcConfirmation', 'Completed', 'Cancelled',
                                    *failed_stages} for row in all_rows)),
                            evidenceSource='saved-report')

    def task_detail(self, task_id):
        if not isinstance(task_id, str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,128}', task_id):
            raise ValueError('任务编号无效。')
        with self.lock:
            try:
                result = self.api('/api/tasks/' + task_id)
                from scenarios import query
                import sqlite3
                try:
                    result['trafficWaiting'] = query(self.root,
                        "SELECT Reason AS reason, BlockingKey AS blockingKey, WaitingSinceUtc AS waitingSinceUtc "
                        "FROM traffic_request WHERE TaskId=? AND State='Waiting' ORDER BY UpdatedAt DESC", (task_id,))
                except sqlite3.Error:
                    result['trafficWaiting'] = None
                return result
            except Exception as live_error:
                saved = next((item for item in (self._report_task_summary() or [])
                              if item.get('taskId') == task_id), None)
                if saved is None:
                    raise live_error
                row = next(row for row in (self._report_task_rows() or []) if row['taskId'] == task_id)
                steps = []
                for step in saved.get('steps') or []:
                    sequence = step.get('number')
                    steps.append(dict(
                        stepId='REPORT-STEP-' + str(sequence),
                        sequence=sequence,
                        stepType=step.get('stepType'),
                        status=step.get('status') or 'Pending',
                        isCurrent=sequence == saved.get('currentStepNumber'),
                        waitingExplanation=step.get('waitingExplanation') or '',
                        elapsedText='报告未保存步骤耗时'))
                return dict(
                    task=row,
                    waitingExplanation=saved.get('waitingExplanation') or '',
                    steps=steps,
                    executionTimeline=[],
                    handshakes=[],
                    commands=[],
                    resources=[],
                    plcConditions=[],
                    evidenceSource='saved-report',
                    evidenceMessage='仿真环境当前未在线，显示最近报告保存的只读九步证据；实时命令、PLC握手和路权明细需要在实例运行时查看。')

    def task_resources(self):
        with self.lock:
            # Resource leases are part of the coherent service snapshot. The
            # production WebHost intentionally has no standalone
            # /api/resources route, so projecting from the same snapshot keeps
            # the simulation proxy read-only and avoids a false 404.
            try:
                return self.api('/api/snapshot').get('resources') or []
            except Exception:
                return []

    def _report_task_summary(self):
        value = getattr(self, 'report', None)
        summary = value.get('taskSummary') if isinstance(value, dict) else None
        return summary if isinstance(summary, list) else None

    def _report_task_rows(self):
        summary = self._report_task_summary()
        if summary is None:
            return None
        finished_at = (getattr(self, 'report', {}) or {}).get('finishedAt')
        try:
            timestamp = datetime.fromtimestamp(float(finished_at), timezone.utc).isoformat()
        except (TypeError, ValueError, OSError, OverflowError):
            timestamp = ''
        return [dict(
            taskId=item.get('taskId'),
            stationId=item.get('stationId') or '',
            vehicleId=item.get('vehicleId') or None,
            stage=item.get('stage') or ('Completed' if item.get('completed') else
                                        item.get('currentStepStatus') or 'WaitingForVehicle'),
            priority=0,
            createdAt=timestamp,
            updatedAt=timestamp) for item in summary if item.get('taskId')]

    def status(self):
        from simulation_reports import history, guidance
        device = self.device()
        with self.lock:
            now = time.monotonic()
            recovery_cache = getattr(self, 'recovery_cache', (0, {}))
            if self.phase == 'running' and now - recovery_cache[0] > 1:
                try:
                    snapshot = self.api('/api/snapshot')
                    recoveries = {row.get('vehicleId'): row.get('recovery')
                                  for row in snapshot.get('status', {}).get('agvs', [])
                                  if row.get('vehicleId') and row.get('recovery')}
                except Exception:
                    recoveries = {}
                recovery_cache = (now, recoveries)
                self.recovery_cache = recovery_cache
            if self.phase == 'running':
                for vehicle_id, car in (device.get('cars') or {}).items():
                    recovery = recovery_cache[1].get(vehicle_id)
                    if recovery:
                        car['recovery'] = recovery
                        car['blocked'] = True
            recovery = None
            if self.phase == 'running':
                try: recovery = self.api('/api/recovery')
                except Exception as error: recovery = dict(error=str(error), items=[], released=False)
            if now - self.history_cache[0] > 15:
                self.history_cache = (now, history(self))
            if self.phase == 'running' and now-self.guidance_cache[0] > 4:
                details = {}
                for item in (recovery or {}).get('items', []):
                    tid = item.get('taskId')
                    if tid and tid != '__SITE__':
                        try: details[tid] = self.api('/api/tasks/' + tid)
                        except (OSError, ValueError, RuntimeError, AttributeError): pass
                # A timeout task can be Faulted after the startup recovery
                # barrier has already been released. Build the manual-recovery
                # page from every unfinished task, not only startup tickets.
                try:
                    page = self.task_page(dict(filter=['All'], page=['1'], pageSize=['200']))
                    unfinished = [row for row in page.get('items', [])
                                  if row.get('taskId') and row.get('stage') not in ('Completed', 'Cancelled')][:40]
                    for row in unfinished:
                        tid = row['taskId']
                        if tid in details: continue
                        try: details[tid] = self.api('/api/tasks/' + tid)
                        except (OSError, ValueError, RuntimeError, AttributeError): pass
                except (OSError, ValueError, RuntimeError, AttributeError):
                    pass
                self.guidance_cache = (now, guidance(details, device))
            plc_table = self._bound_plc_table()
            return dict(enabled=True, rootName=self.root.name, running=self.phase == 'running', phase=self.phase,
                        fresh=self.phase == 'running' and 0 <= time.time() - device.get('time', 0) < 5 and bool(self.children) and all(p.poll() is None for p in self.children.values()),
                        device=device, map=read(self.root / 'navigation.agvmap.json'), scenario=dict(self.scenario), report=self.report,
                        businessPlan=read(self.root / 'business-call-plan.json') if (self.root / 'business-call-plan.json').is_file() else None,
                        runtimeRelease=read(self.root / 'runtime-release.json') if (self.root / 'runtime-release.json').is_file() else None,
                        controls=self.controls, error=self.error, configuration=getattr(self, 'lab', {}).get('simulationConfiguration'), recovery=recovery,
                        protocol=self.protocol_status(),
                        equipment=self.equipment_status(),
                        **(self.scheme_store.catalog() if getattr(self, 'scheme_store', None) else {}),
                        stationCatalog=plc_table.get('stations', []), history=self.history_cache[1],
                        plcSignalTable=self._plc_table_metadata(plc_table),
                        recoveryGuidance=self.guidance_cache[1], startupPlan=self.startup_plan, startupChecks=getattr(self, 'startup_checks', []),
                        versionComparison=self.version_comparison.snapshot() if hasattr(self, 'version_comparison') else None,
                        message='回环设备仿真；合成模型不代表真实固件、机械及安全联锁验收。')

    def live_status(self):
        """Small read path for high-frequency map animation bridges.

        The full status payload also contains the map, PLC catalog, history,
        recovery guidance and scheme catalog. Rebuilding that payload every
        500 ms competes with the production task coordinator during large
        station-wave tests. This route returns only fresh device telemetry and
        a compact scenario summary; it never changes scheduling state.
        """
        device = self.device()
        scenario = self.scenario or {}
        with self.lock:
            children_running = bool(self.children) and all(p.poll() is None for p in self.children.values())
            return dict(
                enabled=True,
                rootName=self.root.name,
                running=self.phase == 'running',
                phase=self.phase,
                fresh=(
                    self.phase == 'running'
                    and 0 <= time.time() - device.get('time', 0) < 5
                    and children_running
                ),
                device=device,
                scenario=dict(
                    id=scenario.get('id'),
                    name=scenario.get('name'),
                    status=scenario.get('status'),
                    completedRounds=scenario.get('completedRounds', 0),
                    totalRounds=scenario.get('totalRounds', 0),
                    message=scenario.get('message'),
                    callRows=scenario.get('callRows') or (
                        (self.report or {}).get('callRows', []) if scenario.get('status') == 'idle' else []),
                ),
                error=self.error,
                startupChecks=getattr(self, 'startup_checks', []),
                message='轻量实时状态；正式派车、PLC和路权仍由生产仿真后台负责。',
            )

    def launch(self, name, arguments, cwd):
        env = os.environ.copy()
        env['AGV_DISPATCH_DATABASE_PATH'] = str(self.root / 'data/agv-dispatch.db')
        with (self.root / f'simulation-{name}-stdout.log').open('ab') as out, (self.root / f'simulation-{name}-stderr.log').open('ab') as err:
            child = subprocess.Popen(arguments, cwd=cwd, env=env, stdout=out, stderr=err,
                                     creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        self.children[name] = child
        write(self.root / 'processes.json', [dict(Id=p.pid, Path=str(self.root / (n + '/AgvDispatch.' + ('Service' if n == 'service' else 'WebHost') + '.exe')) if n != 'twin' else sys.executable) for n, p in self.children.items()])
        return child

    def wait(self, label, predicate, timeout=90):
        deadline = time.monotonic() + timeout
        last_error = ''
        while time.monotonic() < deadline:
            if self.abort.is_set():
                raise InterruptedError('场景已停止，任务与设备状态保留供恢复核对。')
            try:
                value = predicate()
                if value:
                    return value
            except (OSError, ValueError) as error:
                last_error = str(error)
            if any(p.poll() is not None for p in self.children.values()):
                raise RuntimeError('仿真子进程退出，请查看本实例日志。')
            self.abort.wait(.3)
        raise TimeoutError(label + ('；' + last_error if last_error else ''))

    def connected(self):
        snap = self.api('/api/snapshot')
        return snap if snap.get('connected') and snap.get('status', {}).get('service', {}).get('processId') == self.children['service'].pid else None

    def devices_ready(self):
        snap = self.connected()
        vehicles = snap.get('status', {}).get('agvs', []) if snap else []
        return len(vehicles) == len(self.settings['Agv']['Robots']) and all(v['connection'] == 'Online' and v['quality'] == 'Good' for v in vehicles)

    def idle_review(self):
        state = self.device()
        memory = bytes.fromhex(state['memory_hex'])
        station_offsets = [station.get('offset', 0) for station in getattr(self, 'lab', {}).get('simulationTopology', {}).get('stations', [])] or [0, 2]
        if any(c['loaded'] or c['current_task'] or any(t in (1, 2, 3) for t in c['tasks'].values()) for c in state['cars'].values()) or memory[610] != 0 or any(memory[o] & 254 != 10 for o in station_offsets):
            raise RuntimeError('设备不是空载静止的闭合初态，拒绝自动恢复。')
        page = self.api('/api/tasks')
        summary = page.get('summary')
        # The first page is not an inventory once repeated runs exceed 50 rows.
        # Current Service provides an aggregate over all matches before pagination.
        if not isinstance(summary, dict) or any(type(summary.get(key)) is not int for key in ('total', 'completed')) or summary['total'] != page.get('totalCount'):
            raise RuntimeError('缺少完整任务汇总，不能自动批准初态恢复。')
        if summary['total'] != summary['completed']:
            raise RuntimeError('存在未闭合任务，保留现场并人工核对。')
        recovery = self.api('/api/recovery')
        if not recovery['released']:
            if any(t['taskId'] != '__SITE__' for t in recovery['items']):
                raise RuntimeError('活动任务恢复不自动批准。')
            for ticket in recovery['items']:
                self.api('/api/recovery/decisions', dict(sessionId=recovery['sessionId'], ticketId=ticket['ticketId'], revision=ticket['revision'],
                    decision=10, materialReference='SIM-ONLY 空载静止初态，PLC闭合，任务无未完成项。', reason='isolated synthetic idle baseline',
                    vehicleAndLocationConfirmed=True, loadAndOwnershipConfirmed=True, plcCycleConfirmed=True))
            self.api('/api/recovery/release', dict(sessionId=recovery['sessionId'], reason='SIM-ONLY verified idle baseline'))

    def bootstrap(self):
        if not (self.root / 'map-operation-results.json').exists():
            process = self.launch('map-import', [sys.executable, str(WORKSPACE / 'tools/WebSingleVehicleLab/configure_map.py'), '--root', str(self.root)], WORKSPACE)
            self.wait('导入地图超时', lambda: process.poll() is not None)
            self.children.pop('map-import')
            if process.returncode:
                raise RuntimeError('地图导入失败，请查看 simulation-map-import-stderr.log。')
        metadata = read(self.root / 'map-operation-results.json')
        target = {k: metadata[k] for k in ('mapId', 'versionId')}
        switch = self.api('/api/maps/switch')
        if not switch.get('current') and not any(m['isActive'] and m['mapId'] == target['mapId'] and m['activeVersionId'] == target['versionId'] for m in self.api('/api/maps')):
            self.api('/api/maps/switch/begin', target)
            switch = self.api('/api/maps/switch')
        if switch.get('current'):
            if any(switch['current'][k] != v for k, v in target.items()):
                raise RuntimeError('已有其他切图操作，拒绝覆盖。')
            if switch['current']['phase'] == 'Draining':
                self.api('/api/maps/activate', dict(**target, confirmationText='ACTIVATE ' + metadata['published']['workspace']['project']['name'],
                    siteSafetyConfirmed=True, vehicleMapCompatibilityConfirmed=True, evidence='SIM-ONLY same authored map', expectedReleaseId=switch['current']['releaseId']))
            self.wait('地图恢复门禁等待新观测超时', lambda: self.api('/api/maps/switch/resume', dict(releaseId=switch['current']['releaseId'],
                siteSafetyConfirmed=True, vehicleMapCompatibilityConfirmed=True, evidence='SIM-ONLY idle virtual map verified'), allow_rejection=True).get('accepted'))
        self.wait('车辆新鲜观测未就绪', self.devices_ready)
        self.review_idle_with_retry()
        import hashlib
        binding = self.api('/api/map-bindings')
        self.api('/api/map-bindings', dict(expectedRevision=binding['revision'], bindings=[dict(vehicleId=r['VehicleId'], localMapId=target['mapId'],
            localMapVersion=target['versionId'], robotMap='single_vehicle_lab', robotMapMd5=hashlib.md5((self.root / 'navigation.agvmap.json').read_bytes()).hexdigest(),
            coordinatesVerified=True, confirmationNote='SIM-ONLY identical synthetic coordinates; not field calibration') for r in self.settings['Agv']['Robots']]))
        child = self.children['service']
        child.terminate(); child.wait(timeout=10)
        self.children.pop('service')
        self.launch('service', [str(self.root / 'service/AgvDispatch.Service.exe')], self.root / 'service')
        self.wait('配置后后台重启超时', self.connected)
        self.wait('重启后车辆新鲜观测未就绪', self.devices_ready)
        self.review_idle_with_retry()

    def review_idle_with_retry(self):
        # A polling observation can revise the site ticket between reading and
        # deciding. Retry only the verified idle baseline; never force a ticket.
        last = None
        for attempt in range(8):
            try:
                self.idle_review(); return
            except RuntimeError as error:
                last = error
                if '/api/recovery/decisions' not in str(error): raise
                if self.abort.wait(.5): raise InterruptedError('启动已中止。')
        raise last

    @staticmethod
    def _bootstrap_sites(navigation, configuration):
        """Choose unique safe-stop sites for the map activation gate only.

        Field vehicles are often parked on ordinary Waypoints (V13 currently uses
        LM24/LM25), while a first map activation requires Station/WaitingPoint/
        ChargingPoint stops. Bootstrap the empty devices at reserved safe sites,
        activate the authored map, then restart the twins at the exact configured
        field-snapshot sites. This does not weaken or bypass the production gate.
        """
        vehicles = configuration['vehicles']
        if len(vehicles) == 1:
            return None
        safe_types = {'Station', 'WaitingPoint', 'ChargingPoint'}
        reserved = {
            *(station['pickupSite'] for station in configuration['stations']),
            configuration['dumper']['dumpingSite'],
            *configuration['dumper']['waitingSites'],
        }
        typed = any(isinstance(node.get('type'), str) for node in navigation['nodes'])
        if not typed:
            return None
        enabled = [node for node in navigation['nodes'] if node.get('enabled', True) and node.get('type') in safe_types]
        candidates = [node for node in enabled if node['id'] not in reserved]
        minimum_separation = 2 * max(vehicle['radiusMeters'] for vehicle in vehicles) + .1
        selected = []
        for node in sorted(candidates, key=lambda row: row['id']):
            if all(math.hypot(node['x'] - other['x'], node['y'] - other['y']) >= minimum_separation
                   for other in selected):
                selected.append(node)
                if len(selected) == len(vehicles):
                    break
        if len(selected) < len(vehicles):
            # Some custom maps have few free waiting areas. Prefer another typed
            # stop over changing or disabling the activation safety checks.
            for node in sorted(enabled, key=lambda row: row['id']):
                if node in selected:
                    continue
                if all(math.hypot(node['x'] - other['x'], node['y'] - other['y']) >= minimum_separation
                       for other in selected):
                    selected.append(node)
                    if len(selected) == len(vehicles):
                        break
        if len(selected) < len(vehicles):
            raise ValueError('地图没有足够的独立安全站点供首次地图激活；请增加Station或等待点。')
        return {vehicle['id']: node['id'] for vehicle, node in zip(vehicles, selected)}

    def _restart_twin_at_field_sites(self):
        """Restart only the synthetic devices after the authored map is active."""
        sites = (self.lab.get('initialSites') or {}).copy()
        if not sites or sites == self.lab.get('runtimeInitialSites'):
            return
        twin = self.children.get('twin')
        if twin is None or twin.poll() is not None:
            raise RuntimeError('地图激活后虚拟设备已退出，无法切换到现场快照起点。')
        twin.terminate(); twin.wait(timeout=10)
        self.children.pop('twin')
        # Keep bootstrap observations and journals instead of overwriting them.
        replacements = []
        for name in ('robots', 'multi-state.json', 'device-state.json', 'device-ready'):
            source = self.root / name
            if source.exists():
                target = self.root / ('bootstrap-' + name)
                if target.exists():
                    raise RuntimeError('启动现场已出现同名引导证据：' + target.name)
                rename_evidence(source, target)
                replacements.append((source, target))
        for name in ('multi-state.json', 'device-state.json', 'device-ready'):
            stale = self.root / name
            if stale.exists() and stale.is_file():
                stale.unlink()
        self.lab['runtimeInitialSites'] = sites
        write(self.root / 'lab.json', self.lab)
        self.launch('twin', [sys.executable, str(WORKSPACE / 'tools/WebTwoVehicleLab/multi_twin.py'), str(self.root)], self.root)
        self.wait('现场快照起点虚拟设备启动超时', lambda: self.device(), 15)
        self.wait('现场快照起点车辆观测未就绪', self.devices_ready)

    @staticmethod
    def _finite_number(value):
        return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)

    @staticmethod
    def _attach_plc_table_source(table, payload):
        """Keep the exact source-file identity separate from the normalized copy hash."""
        result = dict(table)
        source_sha256 = payload.get('sourceSha256') if isinstance(payload, dict) else None
        if source_sha256 is not None:
            if not isinstance(source_sha256, str) or not SOURCE_SHA256.fullmatch(source_sha256.lower()):
                raise ValueError('PLC点表原文件SHA256格式无效。')
            result['sourceSha256'] = source_sha256.lower()
        for key, maximum in (('sourceFile', 512), ('sourceDeviceId', 128), ('sourceDeviceName', 256)):
            value = payload.get(key) if isinstance(payload, dict) else None
            if value is not None:
                if not isinstance(value, str) or not value.strip() or len(value) > maximum:
                    raise ValueError('PLC点表来源字段无效。')
                result[key] = value.strip()
        return result

    def _bound_plc_table(self):
        from station_catalog import normalize_plc_table_from_path
        candidates = [self.root / 'plc-signals.source.csv']
        configured = getattr(self, 'settings', {}).get('Plc', {}).get('SignalCsvPath')
        if configured:
            path = Path(configured)
            candidates.append(path if path.is_absolute() else self.root / 'service' / path)
        for path in candidates:
            if not path.is_file() or path.is_symlink():
                continue
            stat = path.stat()
            key = (str(path.resolve()), stat.st_mtime_ns, stat.st_size)
            if self.plc_table_cache and self.plc_table_cache[0] == key:
                return self._attach_plc_table_source(
                    self.plc_table_cache[1], getattr(self, 'lab', {}).get('plcSignalTable') or {})
            try:
                table = normalize_plc_table_from_path(path, 'plc-signals.source.csv' if path.name == 'plc-signals.source.csv' else path.name)
            except (OSError, ValueError):
                continue
            table = self._attach_plc_table_source(
                table, getattr(self, 'lab', {}).get('plcSignalTable') or {})
            self.plc_table_cache = (key, table)
            return table
        return {}

    @staticmethod
    def _plc_table_metadata(table):
        if not table:
            return None
        return {key: table.get(key) for key in
                ('fileName', 'sha256', 'bytes', 'rowCount', 'signalCount', 'stationCount', 'dumperSignalCount',
                 'stations', 'headers', 'mapDefinitionHash', *POINT_TABLE_SOURCE_FIELDS)}

    def validate_point_table(self, payload):
        from station_catalog import normalize_plc_table
        required = {'fileName', 'csvText'}
        allowed = required | set(POINT_TABLE_SOURCE_FIELDS)
        if not isinstance(payload, dict) or not required <= set(payload) or set(payload) - allowed:
            raise ValueError('PLC点表校验字段无效。')
        table = normalize_plc_table(payload.get('fileName'), payload.get('csvText'))
        return self._plc_table_metadata(self._attach_plc_table_source(table, payload))

    def current_point_table(self):
        table = self._bound_plc_table()
        if not table:
            raise ValueError('当前实例尚未绑定PLC点表。')
        return table

    def _validate_configuration(self, payload):
        if not isinstance(payload, dict) or set(payload) - {'name', 'mapDefinition', 'stations', 'vehicles', 'dumper', 'inspectionSite', 'plcSignalTable', 'mapIdentity', 'downstreamAdmissionCapacity'}:
            raise ValueError('新实例配置包含未知字段。')
        navigation = copy.deepcopy(payload.get('mapDefinition'))
        if not isinstance(navigation, dict) or not all(isinstance(navigation.get(key), list) for key in ('nodes', 'edges', 'resources')):
            raise ValueError('地图必须是包含nodes、edges和resources的对象。')
        if not 3 <= len(navigation['nodes']) <= 10000 or not 1 <= len(navigation['edges']) <= 50000 or len(navigation['resources']) > 5000:
            raise ValueError('地图节点、道路或资源数量超出仿真范围。')
        # Legacy CAD maps represented machine approach junctions without the
        # explicit stop veto. The transport window planner treats null as the
        # commissioned legacy default (allowed), splitting nearly every factory
        # route at APPROACH-* nodes. These nodes are graph junctions, never
        # physical waiting bays. Normalize a private copy so saved schemes are
        # repaired without mutating the browser payload.
        for node in navigation['nodes']:
            if not isinstance(node, dict):
                raise ValueError('地图节点必须是对象。')
            if str(node.get('id', '')).startswith('APPROACH-') and node.get('allowTrafficStop') is None:
                node['allowTrafficStop'] = False
        # A map copied from the running engineering runtime already carries the
        # authoritative SHA256 computed over the complete C# MapDefinition.
        # Re-hashing only the browser projection (nodes/edges/resources) produces
        # a different value for the same V13 map and causes false mismatch reports.
        # Imported maps without that identity still receive a deterministic local
        # projection hash, but never claim to be the active field map.
        navigation_hash = hashlib.sha256(json.dumps(navigation, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()).hexdigest().upper()
        identity_payload = payload.get('mapIdentity') or {}
        if not isinstance(identity_payload, dict) or set(identity_payload) - {'mapId', 'mapVersionId', 'sourceName', 'definitionHash'}:
            raise ValueError('地图身份字段无效。')
        for key in ('mapId', 'mapVersionId', 'sourceName'):
            if identity_payload.get(key) is not None and (not isinstance(identity_payload[key], str) or len(identity_payload[key]) > 256):
                raise ValueError('地图身份字段长度或类型无效。')
        identity_hash = identity_payload.get('definitionHash')
        if identity_hash is not None:
            if not isinstance(identity_hash, str) or not re.fullmatch(r'[A-Fa-f0-9]{64}', identity_hash):
                raise ValueError('地图内容摘要格式无效。')
            identity_hash = identity_hash.upper()
        map_hash = identity_hash or navigation_hash
        map_identity = dict(mapId=identity_payload.get('mapId') or '', mapVersionId=identity_payload.get('mapVersionId') or '',
                            sourceName=identity_payload.get('sourceName') or navigation.get('name') or '自定义仿真地图',
                            definitionHash=map_hash)
        nodes = {}
        vendor_ids = set()
        for node in navigation['nodes']:
            if node.get('allowTrafficStop') is not None and type(node['allowTrafficStop']) is not bool:
                raise ValueError('路口是否允许交通停车必须为布尔值，不能用文字或数字替代。')
            if not isinstance(node, dict) or any(not isinstance(node.get(key), str) or not node[key].strip() for key in ('id', 'agvSiteId')):
                raise ValueError('地图节点缺少id或agvSiteId。')
            if node['id'] in nodes or node['agvSiteId'] in vendor_ids:
                raise ValueError('地图节点id或agvSiteId重复。')
            if not self._finite_number(node.get('x')) or not self._finite_number(node.get('y')):
                raise ValueError('地图节点坐标无效。')
            nodes[node['id']] = node
            vendor_ids.add(node['agvSiteId'])
        edges = []
        for edge in navigation['edges']:
            if not isinstance(edge, dict) or not isinstance(edge.get('id'), str) or edge.get('from') not in nodes or edge.get('to') not in nodes:
                raise ValueError('地图道路id、起点或终点无效。')
            if edge.get('enabled', True) is True:
                edges.append((edge['from'], edge['to'], bool(edge.get('bidirectional', True))))
        resources = {}
        for resource in navigation['resources']:
            if not isinstance(resource, dict) or not isinstance(resource.get('id'), str) or resource['id'] in resources:
                raise ValueError('地图资源id缺失或重复。')
            resources[resource['id']] = resource
        selected = {}
        def node(field, label):
            value = field
            if not isinstance(value, str) or value not in nodes or nodes[value].get('enabled', True) is not True:
                raise ValueError(f'{label}不存在或已停用。')
            selected[label] = value
            return value
        from station_catalog import normalize_plc_table, normalize_stations
        table_payload = payload.get('plcSignalTable')
        table_fields = {'fileName', 'csvText', 'sha256', 'bytes', 'rowCount', 'signalCount', 'stationCount',
                        'dumperSignalCount', 'stations', 'headers', 'mapDefinitionHash', *POINT_TABLE_SOURCE_FIELDS}
        if not isinstance(table_payload, dict) or set(table_payload) - table_fields:
            raise ValueError('新实例必须明确绑定PLC点表；未再使用仓库全局点表作为回退。')
        table = normalize_plc_table(table_payload.get('fileName'), table_payload.get('csvText'))
        if table_payload.get('sha256') and table_payload['sha256'].lower() != table['sha256']:
            raise ValueError('PLC点表内容与浏览器提供的SHA256不一致，请重新选择文件。')
        table = self._attach_plc_table_source(table, table_payload)
        table['mapDefinitionHash'] = map_hash
        stations = normalize_stations(payload.get('stations'), catalog=table['stations'])
        normalized_stations = []
        typed_topology = any(isinstance(node.get('type'), str) for node in nodes.values())
        for index, station in enumerate(stations):
            pickup = node(station['pickupSite'], '取货点' + str(index + 1))
            if typed_topology and nodes[pickup].get('type') != 'Station':
                raise ValueError('呼叫工位取货点必须是Station类型节点。')
            normalized_stations.append(dict(station, pickupSite=pickup))
        if len({s['pickupSite'] for s in normalized_stations}) != len(normalized_stations):
            raise ValueError('各工位必须使用不同取货点。')
        dumper = payload.get('dumper')
        if not isinstance(dumper, dict) or set(dumper) - {'dumpingSite', 'waitingSite', 'waitingSites', 'resourceId'} or not {'dumpingSite', 'waitingSite', 'resourceId'} <= set(dumper):
            raise ValueError('倒料设备配置字段无效。')
        dumping = node(dumper['dumpingSite'], '倒料点')
        waiting = node(dumper['waitingSite'], '等待点')
        waiting_sites = dumper.get('waitingSites', [waiting])
        if not isinstance(waiting_sites, list) or not 1 <= len(waiting_sites) <= 32 or any(not isinstance(site, str) for site in waiting_sites):
            raise ValueError('等待位列表须包含1至32个节点。')
        waiting_sites = [node(site, '等待位') for site in waiting_sites]
        if waiting_sites[0] != waiting or len(set(waiting_sites)) != len(waiting_sites) or dumping in waiting_sites:
            raise ValueError('等待位不能重复或与倒料点相同，首项须为主等待位。')
        if typed_topology and nodes[dumping].get('type') != 'Station':
            raise ValueError('倒料点必须是Station类型节点。')
        resource_id = dumper.get('resourceId')
        resource = resources.get(resource_id)
        if resource is None or not isinstance(resource.get('boundary'), list) or len(resource['boundary']) < 3 or resource.get('capacity', 1) < 1:
            raise ValueError('倒料资源必须有有效边界和正容量。')
        if any(not isinstance(point, dict) or not self._finite_number(point.get('x')) or not self._finite_number(point.get('y'))
               for point in resource['boundary']):
            raise ValueError('倒料资源边界坐标无效。')
        if resource_id not in (nodes[dumping].get('resourceIds') or []):
            nodes[dumping]['resourceIds'] = list(dict.fromkeys([*(nodes[dumping].get('resourceIds') or []), resource_id]))
        inspection = node(payload.get('inspectionSite') or next(iter(nodes)), '检修点')
        if typed_topology and nodes[inspection].get('type') not in ('Station','WaitingPoint'):
            raise ValueError('检修停放引用必须是Station或WaitingPoint类型节点，不能使用道路中间点。')
        vehicles = payload.get('vehicles')
        if not isinstance(vehicles, list) or not 1 <= len(vehicles) <= 8:
            raise ValueError('车辆数量须为1至8辆。')
        normalized_vehicles = []
        initial_sites = set()
        vehicle_ids = set()
        for vehicle in vehicles:
            if not isinstance(vehicle, dict) or set(vehicle) - {
                    'id', 'initialSite', 'radiusMeters', 'lengthMeters', 'widthMeters',
                    'initialLoaded', 'batteryPercent', 'speedScale',
                    'accelerationMetersPerSecondSquared', 'operationSeconds', 'travelCostPercentPerMeter',
                    'idleCostPercentPerHour', 'voltageCurve', 'chargingGainPercentPerHour'}:
                raise ValueError('车辆配置字段无效。')
            vehicle_id = vehicle.get('id')
            if not isinstance(vehicle_id, str) or not VEHICLE_ID.fullmatch(vehicle_id) or vehicle_id in vehicle_ids:
                raise ValueError('车辆ID须唯一且只包含字母、数字、下划线或连字符。')
            vehicle_ids.add(vehicle_id)
            initial = node(vehicle.get('initialSite'), '车辆起点' + vehicle_id)
            # Current field positions are allowed to be waypoints. The isolated
            # device must reproduce the reported vendor site, not relocate the
            # vehicle to a nearby station just to satisfy a template assumption.
            if initial in initial_sites:
                raise ValueError('每辆虚拟车必须使用不同的初始节点。')
            initial_sites.add(initial)
            supplied_length = vehicle.get('lengthMeters')
            supplied_width = vehicle.get('widthMeters')
            if (supplied_length is None) != (supplied_width is None):
                raise ValueError('车辆长宽必须同时提供。')
            if supplied_length is not None:
                if not self._finite_number(supplied_length) or not self._finite_number(supplied_width) or \
                        not 0.1 <= supplied_width <= supplied_length <= 20:
                    raise ValueError('车辆长宽须满足0.1≤宽≤长≤20米。')
                length, width = float(supplied_length), float(supplied_width)
            else:
                radius = vehicle.get('radiusMeters', 0.6)
                if not self._finite_number(radius) or not 0.1 <= radius <= 5:
                    raise ValueError('车辆包络半径须为0.1至5米。')
                length = width = 2 * float(radius)
            radius = max(float(vehicle.get('radiusMeters') or 0), math.hypot(length, width) / 2)
            if not 0.1 <= radius <= 20:
                raise ValueError('车辆外接半径须为0.1至20米。')
            loaded = vehicle.get('initialLoaded', False)
            battery = vehicle.get('batteryPercent', 86)
            speed = vehicle.get('speedScale', 0.75)
            acceleration = vehicle.get('accelerationMetersPerSecondSquared', 0.6)
            operation = vehicle.get('operationSeconds', 2.0)
            travel_cost = vehicle.get('travelCostPercentPerMeter', 0.01)
            idle_cost = vehicle.get('idleCostPercentPerHour', 0.1)
            charging_gain = vehicle.get('chargingGainPercentPerHour', 20)
            if not isinstance(loaded, bool) or not self._finite_number(battery) or not 0 <= battery <= 100 or \
                    not self._finite_number(speed) or not 0.1 <= speed <= 10 or \
                    not self._finite_number(acceleration) or not 0.05 <= acceleration <= 10 or \
                    not self._finite_number(operation) or not 0.1 <= operation <= 120 or \
                    not self._finite_number(travel_cost) or not 0 <= travel_cost <= 10 or \
                    not self._finite_number(idle_cost) or not 0 <= idle_cost <= 100 or \
                    not self._finite_number(charging_gain) or not 0 <= charging_gain <= 100:
                raise ValueError('车辆载荷、电量、速度、加速度、动作时长或耗电参数无效。')
            curve = vehicle.get('voltageCurve')
            if curve is None: curve = [dict(percent=0, voltage=21), dict(percent=100, voltage=26)]
            if not isinstance(curve, list) or not 2 <= len(curve) <= 20:
                raise ValueError('电池电压曲线须包含2至20个点。')
            normalized_curve = []
            seen_percent = set()
            for point in curve:
                if not isinstance(point, dict) or set(point) != {'percent', 'voltage'} or \
                        not self._finite_number(point['percent']) or not 0 <= point['percent'] <= 100 or \
                        not self._finite_number(point['voltage']) or not 0 < point['voltage'] <= 1000 or point['percent'] in seen_percent:
                    raise ValueError('电池电压曲线点无效。')
                seen_percent.add(point['percent']); normalized_curve.append(dict(percent=float(point['percent']), voltage=float(point['voltage'])))
            normalized_vehicles.append(dict(id=vehicle_id, initialSite=initial, radiusMeters=float(radius),
                                            lengthMeters=length, widthMeters=width,
                                            initialLoaded=loaded, batteryPercent=float(battery), speedScale=float(speed),
                                            accelerationMetersPerSecondSquared=float(acceleration), operationSeconds=float(operation),
                                            travelCostPercentPerMeter=float(travel_cost), idleCostPercentPerHour=float(idle_cost),
                                            voltageCurve=sorted(normalized_curve, key=lambda point: point['percent']),
                                            chargingGainPercentPerHour=float(charging_gain)))
        # Reachability is undirected here; the formal route planner performs the
        # directed, resource-aware validation after the isolated instance starts.
        graph = {node_id: set() for node_id in nodes}
        for source, target, bidirectional in edges:
            graph[source].add(target)
            if bidirectional: graph[target].add(source)
        def reachable(source):
            pending, seen = [source], set()
            while pending:
                current = pending.pop()
                if current in seen: continue
                seen.add(current); pending.extend(graph[current] - seen)
            return seen
        targets = [s['pickupSite'] for s in normalized_stations] + waiting_sites + [dumping, inspection]
        for vehicle in normalized_vehicles:
            if not set(targets) <= reachable(vehicle['initialSite']):
                raise ValueError(f"{vehicle['id']}与地图工位、等待点或倒料点不连通。")
        admission = payload.get('downstreamAdmissionCapacity', 0)
        if type(admission) is not int or not 0 <= admission <= 8:
            raise ValueError('下游准入数量须为0至8的整数，0表示不另行限流。')
        return dict(name=payload.get('name') or navigation.get('name') or 'custom-simulation',
                    mapDefinition=navigation, stations=normalized_stations,
                    vehicles=normalized_vehicles, dumper=dict(dumpingSite=dumping, waitingSite=waiting, waitingSites=waiting_sites, resourceId=resource_id),
                    inspectionSite=inspection, plcSignalTable=table, mapIdentity=map_identity,
                    downstreamAdmissionCapacity=admission)

    def _default_configuration(self, count):
        table = self._bound_plc_table()
        if not table:
            raise ValueError('当前实例没有可复用的PLC点表；请先上传并绑定对应地图的CSV。')
        navigation = read(self.root / 'navigation.agvmap.json')
        prior_stations = {station['stationId']: station for station in self.lab.get('simulationTopology', {}).get('stations', [])}
        stations = [dict(stationId=station_id, pickupSite=site,
                        signalSource=prior_stations.get(station_id, {}).get('signalSource', 'verified'),
                        priority=prior_stations.get(station_id, {}).get('priority', 0))
                    for station_id, site in self.settings['Dispatching']['StationPickupSites'].items()
                    ]
        dumper = dict(dumpingSite=self.settings['Dispatching']['DumperDumpingSite'],
                      waitingSite=self.settings['Dispatching']['DumperWaitingSite'],
                      waitingSites=self.settings['Dispatching'].get('DumperWaitingSites') or [self.settings['Dispatching']['DumperWaitingSite']],
                      resourceId=self.settings['HandshakeExecution']['DumperExit']['ResourceIds'][0])
        excluded = {s['pickupSite'] for s in stations} | {dumper['dumpingSite'], *dumper['waitingSites']}
        available_origins = [
            n['id'] for n in navigation['nodes']
            if n.get('enabled', True) and n['id'] not in excluded
        ]
        station_origins = [
            n['id'] for n in navigation['nodes']
            if n.get('enabled', True) and n['id'] not in excluded and
            n.get('type') == 'Station'
        ]
        preferred = [
            self.lab.get('initialSites', {}).get(robot['VehicleId'])
            for robot in self.settings['Agv']['Robots']
        ]
        origins = []
        for origin in [*preferred, *station_origins, *available_origins]:
            if origin in available_origins and origin not in origins:
                origins.append(origin)
        if count > len(origins): raise ValueError('地图没有足够的独立起点。')
        vehicles = [dict(id=f'AGV-LOCAL-{index+1:02}', initialSite=origins[index],
                         radiusMeters=.49, lengthMeters=.8, widthMeters=.52)
                    for index in range(count)]
        inspection = self.lab.get('simulationTopology', {}).get('inspectionSite', '检修')
        if inspection not in {n['id'] for n in navigation['nodes']}:
            inspection = origins[0]
        return self._validate_configuration(dict(mapDefinition=navigation, stations=stations, vehicles=vehicles,
            dumper=dumper, inspectionSite=inspection, name=self.root.name,
            plcSignalTable=dict(fileName=table['fileName'], csvText=table['csvText'], sha256=table['sha256'])))

    def configure(self, payload):
        configuration = self._validate_configuration(payload)
        return self._create_instance(configuration)

    def save_scheme(self, payload):
        from scenarios import validate_scenario
        if self.scheme_store is None: raise ValueError('仿真方案存储未初始化。')
        if set(payload) - {'id','name','configuration','scenario'}: raise ValueError('方案字段无效。')
        configuration = payload.get('configuration')
        if configuration is None:
            configuration = dict(self.lab.get('simulationConfiguration') or {})
            configuration.pop('mapName', None)
            configuration['mapDefinition'] = read(self.root / 'navigation.agvmap.json')
            table = self._bound_plc_table()
            if not table:
                raise ValueError('当前实例没有已绑定的PLC点表，不能保存为可复现方案。')
            configuration['plcSignalTable'] = dict(fileName=table['fileName'], csvText=table['csvText'], sha256=table['sha256'])
        validated = self._validate_configuration(configuration)
        scenario = validate_scenario(payload['scenario']) if payload.get('scenario') is not None else None
        if scenario and any(call['stationId'] not in {s['stationId'] for s in validated['stations']} for call in scenario.get('calls', [])):
            raise ValueError('方案场景包含未配置工位。')
        if scenario and any(call.get('vehicleId') for call in scenario.get('calls', [])):
            raise ValueError('保存的PLC自动呼叫场景不能预先指定车辆；车辆由正式调度选择。')
        value = self.scheme_store.save(payload.get('name'), validated, scenario, payload.get('id'))
        self.scheme_store.remember(self.root, value['id'])
        return self.status()

    def select_scheme(self, payload, start=False):
        if self.scheme_store is None: raise ValueError('仿真方案存储未初始化。')
        if 'id' not in payload or set(payload) - {'id', 'business', 'plan', 'vehicleStarts', 'checkOnly'}: raise ValueError('请选择一个保存的方案。')
        check_only = payload.get('checkOnly', False)
        if type(check_only) is not bool or (start and check_only):
            raise ValueError('检查模式仅用于方案预览。')
        value = self.scheme_store.get(payload['id'])
        configuration = copy.deepcopy(value['configuration'])
        if 'vehicleStarts' in payload:
            starts = payload['vehicleStarts']
            ids = {v['id'] for v in configuration['vehicles']}
            if (not isinstance(starts, list) or len(starts) != len(ids) or
                    any(not isinstance(v, dict) or set(v) != {'id', 'initialSite'} or
                        not isinstance(v['id'], str) or not isinstance(v['initialSite'], str) for v in starts) or
                    {v['id'] for v in starts} != ids or len({v['initialSite'] for v in starts}) != len(ids)):
                raise ValueError('车辆初始位置须完整覆盖方案车辆且各不相同。')
            positions = {v['id']: v['initialSite'] for v in starts}
            for vehicle in configuration['vehicles']:
                vehicle['initialSite'] = positions[vehicle['id']]
        if 'business' in payload and 'plan' in payload: raise ValueError('单次业务与随机计划不能同时提交。')
        business_plan = None
        if 'plan' in payload:
            from business_plan import generate_plan
            business_plan = generate_plan(payload['plan'], configuration['stations'])
            for vehicle in configuration['vehicles']:
                vehicle.update(initialLoaded=False, speedScale=1.0)
            value['scenario'] = dict(name='business-calls', rounds=1, intervalSeconds=0, timeoutSeconds=7200,
                                    calls=business_plan['calls'])
        business = payload.get('business')
        if 'business' in payload:
            if not isinstance(business, dict) or set(business) != {'vehicleId', 'initialSite', 'stationId'}:
                raise ValueError('单次业务必须指定车辆、初始节点和CNC工位。')
            vehicle = next((v for v in configuration['vehicles'] if v['id'] == business['vehicleId']), None)
            if vehicle is None: raise ValueError('车辆不属于所选方案。')
            if not any(s['stationId'] == business['stationId'] for s in configuration['stations']):
                raise ValueError('CNC工位未绑定所选方案。')
            if not any(n['id'] == business['initialSite'] and n.get('enabled') is not False for n in configuration['mapDefinition']['nodes']):
                raise ValueError('初始节点不存在或未启用。')
            vehicle.update(initialSite=business['initialSite'], initialLoaded=False, speedScale=1.0)
            configuration['vehicles'] = [vehicle]
            value['scenario'] = dict(name='dispatch-backlog', rounds=1, intervalSeconds=0, timeoutSeconds=7200,
                calls=[dict(stationId=business['stationId'], atSeconds=0, vehicleId=None)])
        configuration = self._validate_configuration(configuration)
        from scenarios import validate_scenario
        scenario = validate_scenario(value['scenario']) if value.get('scenario') else None
        if scenario and (any(call['stationId'] not in {s['stationId'] for s in configuration['stations']} for call in scenario.get('calls', [])) or
                         any(call.get('vehicleId') for call in scenario.get('calls', []))):
            raise ValueError('保存场景与当前工位或自动选车规则不匹配，请更新方案。')
        if check_only:
            from runtime_release import current_release
            release = current_release(WORKSPACE)
            return dict(configuration=configuration, map=configuration['mapDefinition'], businessPlan=business_plan,
                runtimeRelease=release['metadata'] if release else None,
                preflight=dict(passed=True, checks=[
                    dict(name='地图、工位与PLC点表绑定', status='passed'),
                    dict(name='车辆初态与业务路线配置', status='passed'),
                    dict(name='当前发布程序与算法选项', status='passed' if release else 'unbound'),
                    dict(name='业务呼叫计划', status='passed')]))
        plan_record = business_plan
        self._create_instance(configuration)
        self.business_plan = plan_record
        if plan_record is not None: write(self.root / 'business-call-plan.json', plan_record)
        self.scheme_store.remember(self.root, value['id'])
        if start:
            self.startup_plan = dict(status='starting', schemeId=value['id'], scenarioName=scenario.get('name') if scenario else None,
                                     message='正在启动保存的隔离方案。')
            self.start()
            def after_start():
                try:
                    worker = self.worker
                    if worker: worker.join()
                    if self.abort.is_set():
                        self.startup_plan.update(status='aborted', message='启动计划已停止。'); return
                    if self.phase != 'running':
                        raise RuntimeError(self.error or '仿真未就绪，未提交测试呼叫。')
                    if scenario: self.run_scenario(scenario, clear_abort=False)
                    self.startup_plan.update(status='submitted' if scenario else 'ready',
                        message='保存场景已开始，结果见运行进度。' if scenario else '方案已启动，可以选择场景。')
                except Exception as error:
                    self.startup_plan.update(status='failed', message=str(error))
            self.startup_thread = threading.Thread(target=after_start, daemon=True)
            self.startup_thread.start()
        return self.status()

    def delete_scheme(self, payload):
        if self.scheme_store is None: raise ValueError('仿真方案存储未初始化。')
        if set(payload) != {'id'}: raise ValueError('请选择要删除的保存方案。')
        if self.phase == 'running' or self.worker and self.worker.is_alive():
            raise ValueError('请先停止当前仿真，再删除保存方案。')
        self.scheme_store.delete(payload['id'])
        return self.status()

    def new_instance(self, payload=None):
        if self.phase in ('running', 'starting') or self.children or self.worker and self.worker.is_alive():
            raise ValueError('请先停止当前仿真环境，再创建干净实例。')
        if self.protocol_runtime is not None or self.equipment_model is not None:
            raise ValueError('请先停止协议服务和设备模型，再创建干净实例。')
        payload = payload or {}
        if not isinstance(payload, dict) or set(payload) - {'vehicles'}:
            raise ValueError('新实例仅接受vehicles参数。')
        count = payload.get('vehicles', len(self.settings['Agv']['Robots']))
        if type(count) is not int or not 1 <= count <= 8:
            raise ValueError('车队数量须为1至8。')
        return self._create_instance(self._default_configuration(count))

    def _create_instance(self, configuration):
        with self.lock:
            if self.startup_thread and self.startup_thread.is_alive():
                raise ValueError('一键开测仍在准备，请先停止并等待收尾。')
            if self.phase in ('running', 'starting') or self.children or self.worker and self.worker.is_alive():
                raise ValueError('请先停止当前仿真环境，再创建干净实例。')
            if self.protocol_runtime is not None or self.equipment_model is not None:
                raise ValueError('请先停止协议服务和设备模型，再创建干净实例。')
            validate_root(self.root)
            source, lab, settings, web, _ = validate_root(self.template_root)
            from runtime_release import current_release, verify_release
            release = current_release(WORKSPACE)
            target = WORKSPACE / 'artifacts' / ('web-single-vehicle-sim-' + time.strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6])
            target.mkdir()
            for directory in ('service', 'webhost'):
                binary_source = release['directories'][directory] if release else source / directory
                shutil.copytree(binary_source, target / directory,
                    ignore=shutil.ignore_patterns('data', 'logs', '*.log', '*.db', '*.db-*', 'appsettings*.json'))
            navigation = copy.deepcopy(configuration['mapDefinition'])
            write(target / 'navigation.agvmap.json', navigation)
            table = configuration['plcSignalTable']
            signal_source = target / 'plc-signals.source.csv'
            signal_source.write_bytes(table['csvText'].encode('utf-8-sig'))
            write(target / 'source-manifest.json', dict(source='Web simulation configuration', sourceSha256='', editedFile='navigation.agvmap.json',
                editedSha256=hashlib.sha256((target / 'navigation.agvmap.json').read_bytes()).hexdigest(),
                plcSignalTable=dict(fileName=table['fileName'], sha256=table['sha256'], bytes=table['bytes'],
                    **{key: table[key] for key in POINT_TABLE_SOURCE_FIELDS if table.get(key)}),
                changes=['configured through isolated Simulation Center', 'explicit user-bound PLC signal table'],
                boundary='User-provided or copied simulation map and PLC mapping; not field provenance, calibration or live PLC proof.'))
            used = set()
            def port():
                while True:
                    with socket.socket() as sock:
                        sock.bind(('127.0.0.1', 0)); value = sock.getsockname()[1]
                    if value not in used and value not in (5198, 5199): used.add(value); return value
            field_sites = {v['id']: v['initialSite'] for v in configuration['vehicles']}
            bootstrap_sites = self._bootstrap_sites(navigation, configuration)
            lab.update(root=str(target), pipe='Agv28StageLab-' + target.name, webPort=port(), plcPort=port(),
                multiVehicleSimulation=True, twoVehicleSimulation=len(configuration['vehicles']) == 2,
                waitingBayProtection=True, mapName=navigation.get('name') or 'custom_simulation',
                initialSites=field_sites, runtimeInitialSites=bootstrap_sites or field_sites, bootstrapSites=bootstrap_sites,
                vehicleProfiles={v['id']: dict(initialLoaded=v['initialLoaded'], batteryPercent=v['batteryPercent'],
                    lengthMeters=v['lengthMeters'], widthMeters=v['widthMeters'],
                    speedScale=v['speedScale'], accelerationMetersPerSecondSquared=v['accelerationMetersPerSecondSquared'],
                    operationSeconds=v['operationSeconds'], travelCostPercentPerMeter=v['travelCostPercentPerMeter'],
                    idleCostPercentPerHour=v['idleCostPercentPerHour'], voltageCurve=v['voltageCurve'],
                    chargingGainPercentPerHour=v['chargingGainPercentPerHour']) for v in configuration['vehicles']},
                simulationTopology=dict(stations=configuration['stations'], dumperSite=configuration['dumper']['dumpingSite'],
                    waitingSite=configuration['dumper']['waitingSite'], waitingSites=configuration['dumper']['waitingSites'], inspectionSite=configuration['inspectionSite']),
                simulationConfiguration=dict(stations=configuration['stations'], dumper=configuration['dumper'],
                    inspectionSite=configuration['inspectionSite'], vehicles=configuration['vehicles'],
                    mapName=navigation.get('name') or 'custom_simulation',
                    plcSignalTable=self._plc_table_metadata(table), mapIdentity=configuration['mapIdentity']))
            settings['Security']['PipeName'] = lab['pipe']
            settings['Database']['Path'] = str(target / 'data/agv-dispatch.db')
            settings['Map']['ImportPath'] = str(target / 'navigation.agvmap.json')
            settings['Plc']['Port'] = lab['plcPort']
            original = settings['Agv']['Robots'][0]
            template_profile = settings['HandshakeExecution']['DumperExit']['BodyProfiles'][0]
            robots, profiles = [], []
            for index, vehicle in enumerate(configuration['vehicles']):
                robot = copy.deepcopy(original)
                robot.update(VehicleId=vehicle['id'], DisplayName=f"仿真车{index+1:02}", Port=port(), NavigationPort=port())
                robots.append(robot)
                profile = copy.deepcopy(template_profile)
                profile.update(VehicleId=vehicle['id'], ProfileId='SIM-ONLY-' + vehicle['id'],
                               RadiusFromReportedReferenceMeters=vehicle['radiusMeters'],
                               LengthMeters=vehicle['lengthMeters'], WidthMeters=vehicle['widthMeters'])
                profiles.append(profile)
            settings['Agv']['Robots'] = robots
            # The loopback vehicle is loaded from exactly the authored map file.
            # Keep the full identity checks, but avoid hundreds of serial 1303
            # requests on large CAD maps. Production communication settings
            # remain on the default Full mode and are not changed by this branch.
            settings['Agv']['TaskMapPreflightMode'] = 'SimulationLocal'
            settings['HandshakeExecution']['DumperExit']['BodyProfiles'] = profiles
            settings['HandshakeExecution']['DumperExit']['ResourceIds'] = [configuration['dumper']['resourceId']]
            # Isolated synthetic devices provide exact segment identity and pose.
            # Use the production two-frame/body-envelope verifier, never remove
            # traffic leases merely because the visual car reached a station.
            settings['TrafficSegmentRelease'] = dict(Enabled=True, CommitEnabled=True,
                PollIntervalMs=250, MaximumStepsPerCycle=32, FrameWaitSeconds=1,
                ReceiptValiditySeconds=15)
            settings['Dispatching']['MaxConcurrentTasks'] = len(robots)
            if len(configuration['stations']) > 32 and len(robots) > 1:
                settings['TrafficSegmentRelease']['MaximumAgeSeconds'] = 15
            if release:
                settings['DispatchOptimization'] = copy.deepcopy(release['options'])
            # Only loopback PLC calls enter the production creation coordinator.
            # Scenarios never choose a car or fabricate a queued database row.
            settings['TaskCreation']['AutomaticCreationEnabled'] = True
            settings['TaskCreation']['StationPriorities'] = {station['stationId']: station.get('priority', 0) for station in configuration['stations']}
            settings['Dispatching']['StationPickupSites'] = {s['stationId']: s['pickupSite'] for s in configuration['stations']}
            settings['Dispatching']['DumperWaitingSite'] = configuration['dumper']['waitingSite']
            settings['Dispatching']['DumperWaitingSites'] = configuration['dumper']['waitingSites']
            settings['Dispatching']['DumperDumpingSite'] = configuration['dumper']['dumpingSite']
            capacity = configuration.get('downstreamAdmissionCapacity', 0)
            settings['DownstreamAdmission'] = dict(Enabled=capacity > 0, Capacity=max(1, capacity))
            settings['Dispatching']['DumperTransitClearanceMeters'] = 2 * max(v['radiusMeters'] for v in configuration['vehicles']) + 0.15
            settings['ObstacleRecovery'] = dict(Enabled=True, AutomaticDetourEnabled=True,
                AutomaticSafetyPointRetreatEnabled=True,
                PollIntervalMs=500, **OBSTACLE_RECOVERY_TIMING,
                StableStopObservationMs=500, StoppedVelocityToleranceMetersPerSecond=0.02,
                MinimumLocalizationConfidence=0.5, MaximumStationDeviationMeters=0.3,
                SafetyPointMaximumRetreatDistanceMeters=10,
                SafetyPointRetreatMaximumSpeedMetersPerSecond=0.1,
                SafetyPointRetreatMaximumAccelerationMetersPerSecondSquared=0.15)
            lab.update(robotPort=robots[0]['Port'], navigationPort=robots[0]['NavigationPort'])
            from station_catalog import write_private_signal_table
            signal_target = target / 'service/config/simulation-signals.csv'
            signal_manifest = write_private_signal_table(signal_source, signal_target, configuration['stations'])
            lab['stationSignalManifest'] = signal_manifest
            table_metadata = self._plc_table_metadata(table)
            lab['plcSignalTable'] = dict(
                table_metadata,
                sourceFile=table_metadata.get('sourceFile') or 'plc-signals.source.csv',
                privateFile='plc-signals.source.csv',
                privateSha256=signal_manifest['privateSha256'])
            settings['Plc']['SignalCsvPath'] = 'config/simulation-signals.csv'
            web.update(Port=lab['webPort'], PipeName=lab['pipe'], ProjectStorePath=str(target / 'projects'), EngineeringStorePath=str(target / 'engineering'))
            for filename, value in [('lab.json', lab), ('service/appsettings.json', settings), ('service-settings.json', settings), ('webhost/appsettings.json', {'Web': web})]:
                write(target / filename, value)
            if release:
                verify_release(target, release)
                write(target / 'runtime-release.json', release['metadata'])
            store = self.scheme_store
            self.__init__(target, template_root=self.template_root, scheme_store=store)
            if store is not None: store.remember(target)
        return self.status()

    def start(self):
        with self.lock:
            if self.phase in ('starting', 'running'):
                return self.status()
            if self.worker and self.worker.is_alive():
                raise ValueError('上个操作仍在收尾。')
            validate_root(self.root)
            from runtime_release import current_release, verify_release
            verify_release(self.root, current_release(WORKSPACE))
            if any((self.root / p).exists() for p in ('multi-state.json', 'device-state.json', 'STOP')):
                raise ValueError('该实例保留了设备历史；请新建仿真实例，不会自动重置故障状态。')
            for port in self.ports:
                with socket.socket() as sock:
                    sock.bind(('127.0.0.1', port))
            self.abort.clear(); self.phase = 'starting'; self.error = None
            self.worker = threading.Thread(target=self._start, daemon=True); self.worker.start()
        return self.status()

    def _start(self):
        self.startup_checks = [dict(name=name, status='pending', detail='') for name in
                               ['虚拟设备', '正式后台', '地图恢复', '现场初态']]
        current = self.startup_checks[0]
        try:
            current['status'] = 'running'
            multi = self.lab.get('twoVehicleSimulation') or self.lab.get('multiVehicleSimulation')
            self.launch('twin', [sys.executable, str(WORKSPACE / ('tools/WebTwoVehicleLab/multi_twin.py' if multi else 'tools/WebSingleVehicleLab/device_twin.py')), str(self.root)], self.root)
            self.wait('虚拟设备启动超时', lambda: self.device(), 15)
            current['status'] = 'passed'
            current = self.startup_checks[1]; current['status'] = 'running'
            for name, exe in [('service', 'AgvDispatch.Service'), ('webhost', 'AgvDispatch.WebHost')]:
                self.launch(name, [str(self.root / name / (exe + '.exe'))], self.root / name)
            self.wait('正式后台连接超时', self.connected)
            current['status'] = 'passed'
            current = self.startup_checks[2]; current['status'] = 'running'
            self.bootstrap()
            current['status'] = 'passed'
            current = self.startup_checks[3]; current['status'] = 'running'
            self._restart_twin_at_field_sites()
            current['status'] = 'passed'
            self.phase = 'running'
        except Exception as error:
            current.update(status='failed', detail=str(error))
            self.phase = 'failed'; self.error = str(error)
            try:
                self._stop_children()
            except (OSError, subprocess.TimeoutExpired) as cleanup_error:
                # Preserve the owned process handles so Stop can finish cleanup.
                self.error += '；子进程尚未退出，请点击停止并清理：' + str(cleanup_error)

    def _stop_children(self):
        (self.root / 'STOP').touch()
        for name, child in tuple(self.children.items()):
            if child.poll() is None:
                try:
                    if name == 'twin': child.wait(timeout=3)
                    else: child.terminate(); child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill(); child.wait(timeout=5)
            # Only forget a process after wait/poll confirmed its exit.
            self.children.pop(name)

    def stop(self):
        self.abort.set()
        deadline = time.monotonic() + 18
        startup = getattr(self, 'startup_thread', None)
        if startup and startup.is_alive() and startup is not threading.current_thread():
            startup.join(timeout=max(0, deadline-time.monotonic()))
            if startup.is_alive():
                raise CleanupPending('一键开测仍在收尾，稍后重试停止。')
        worker = self.worker
        if worker and worker.is_alive() and worker is not threading.current_thread():
            worker.join(timeout=max(0, deadline-time.monotonic()))
            if worker.is_alive():
                raise CleanupPending('操作仍在收尾，稍后重试停止。')
        if self.protocol_runtime is not None:
            from protocol_twin import stop_protocol
            stop_protocol(self.protocol_runtime)
            self.protocol_runtime = None
        if self.equipment_model is not None:
            self.equipment_model.stop(); self.equipment_model = None
        self._stop_children()
        self.phase = 'stopped'
        return self.status()

    def protocol_status(self):
        path = self.root / 'protocol-state.json'
        try: value = read(path)
        except (OSError, ValueError): value = dict(running=False, name='未配置协议脚本', transport='', port=0, rules=0)
        if getattr(self, 'protocol_runtime', None) is None and value.get('running'):
            value = dict(value, running=False, error='控制器重启后协议服务器已停止，请重新启动。')
        return value

    def start_protocol(self, payload):
        from protocol_twin import start_protocol, stop_protocol
        if self.protocol_runtime is not None:
            stop_protocol(self.protocol_runtime); self.protocol_runtime = None
        self.protocol_runtime = start_protocol(self.root, payload)
        return self.status()

    def stop_protocol(self):
        from protocol_twin import stop_protocol
        if self.protocol_runtime is not None:
            stop_protocol(self.protocol_runtime); self.protocol_runtime = None
        return self.status()

    def equipment_status(self):
        if getattr(self, 'equipment_model', None) is not None: return self.equipment_model.status()
        try: value = read(self.root / 'equipment-state.json')
        except (OSError, ValueError): value = dict(running=False, floors=['1','2'], currentFloor='1', door='Closed', signals={}, schedules=[])
        if value.get('running'): value = dict(value, running=False, error='控制器重启后设备模型已停止，请重新启动。')
        return value

    def start_equipment(self, payload):
        from equipment_twin import EquipmentModel
        if self.equipment_model is not None: self.equipment_model.stop()
        self.equipment_model = EquipmentModel(self.root)
        if payload and set(payload) - {'sequence'}:
            self.equipment_model.control(payload)
        return self.status()

    def control_equipment(self, payload):
        if self.equipment_model is None: raise ValueError('电梯/信号设备模型尚未启动。')
        self.equipment_model.control(payload)
        return self.status()

    def stop_equipment(self):
        if self.equipment_model is not None:
            self.equipment_model.stop(); self.equipment_model = None
        return self.status()

    def control(self, payload):
        from device_controls import normalize_controls
        if not self.status()['fresh']:
            raise ValueError('设备状态过期或运行未就绪。')
        with self.lock:
            # Merge explicit patches; device consumes a full replacement snapshot.
            candidate = json.loads(json.dumps(self.controls))
            # A new patch must not re-arm a previously consumed one-shot response loss.
            for fields in candidate['vehicles'].values():
                fields['dropNextResponse'] = False
            candidate['sequence'] = payload.get('sequence') or uuid.uuid4().hex
            if set(payload) - {'sequence', 'vehicles', 'plc'}:
                raise ValueError('未知控制字段。')
            for vehicle, changes in payload.get('vehicles', {}).items():
                if vehicle not in {r['VehicleId'] for r in self.settings['Agv']['Robots']}:
                    raise ValueError('车辆不存在。')
                candidate['vehicles'].setdefault(vehicle, {}).update(changes)
            candidate['plc'].update(payload.get('plc', {}))
            navigation = read(self.root / 'navigation.agvmap.json')
            normalize_controls(candidate, {r['VehicleId'] for r in self.settings['Agv']['Robots']}, {e['id'] for e in navigation['edges']})
            write(self.root / 'simulation-control.json', candidate)
            self.controls = candidate
        return self.status()

    def run_scenario(self, payload, clear_abort=True):
        from scenarios import validate_scenario, execute
        request = validate_scenario(payload)
        with self.lock:
            if not clear_abort and self.abort.is_set():
                raise InterruptedError('一键开测已停止，未提交测试呼叫。')
            if not self.status()['fresh'] or self.worker and self.worker.is_alive():
                raise ValueError('仿真未就绪或已有场景运行。')
            if not clear_abort and self.abort.is_set():
                raise InterruptedError('一键开测已停止，未提交测试呼叫。')
            if clear_abort: self.abort.clear()
            stations = set(self.settings['Dispatching']['StationPickupSites'])
            if any(call['stationId'] not in stations for call in request.get('calls', [])):
                raise ValueError('呼叫计划包含本实例未配置的工位。')
            if request['name'] in ('dispatch-backlog', 'business-calls') and not self.settings['TaskCreation']['AutomaticCreationEnabled']:
                raise ValueError('此旧实例未启用正式PLC自动接单，请按当前方案新建实例。')
            if self.settings['TaskCreation']['AutomaticCreationEnabled'] and any(call.get('vehicleId') for call in request.get('calls', [])):
                raise ValueError('PLC自动接单由正式调度选择车辆，呼叫计划不能指定车辆。')
            total_units = len(request['calls']) if request['name'] in ('custom-calls', 'dispatch-backlog', 'business-calls') else request['rounds']
            self.scenario = dict(id=uuid.uuid4().hex, status='running', completedRounds=0, totalRounds=total_units, startedAt=time.time(), message='核对初态', **request)
            self.worker = threading.Thread(target=execute, args=(self, request), daemon=True); self.worker.start()
        return self.status()

    def decide_recovery(self, payload):
        if not isinstance(payload, dict) or set(payload) - {
                'sessionId', 'ticketId', 'revision', 'decision', 'materialReference', 'reason',
                'vehicleAndLocationConfirmed', 'loadAndOwnershipConfirmed', 'plcCycleConfirmed'}:
            raise ValueError('恢复决策字段无效。')
        if not self.status().get('fresh'):
            raise ValueError('设备观测已过期，禁止提交恢复决策。')
        if payload.get('decision') not in (10, 20, 30):
            raise ValueError('恢复决策必须是继续原任务、补记物理完成或关闭取消。')
        if type(payload.get('revision')) is not int:
            raise ValueError('恢复决策必须携带当前修订号。')
        if any(payload.get(key) is not True for key in ('vehicleAndLocationConfirmed', 'loadAndOwnershipConfirmed', 'plcCycleConfirmed')):
            raise ValueError('车辆位置、载荷归属和PLC周期确认必须显式勾选。')
        if not all(isinstance(payload.get(key), str) and len(payload[key].strip()) >= 10 for key in ('materialReference', 'reason')):
            raise ValueError('物料核对引用和恢复原因至少10个字符。')
        recovery = self.api('/api/recovery')
        if recovery.get('released'):
            raise ValueError('恢复屏障已经放行，不能重复决策。')
        if payload.get('sessionId') != recovery.get('sessionId'):
            raise ValueError('恢复会话已变化，请刷新原记录。')
        item = next((row for row in recovery.get('items', []) if row.get('ticketId') == payload.get('ticketId')), None)
        if item is None:
            raise ValueError('恢复条目不存在或会话已刷新。')
        if item.get('revision') != payload.get('revision'):
            raise ValueError('恢复条目修订号已变化，请刷新后重新核对。')
        result = self.api('/api/recovery/decisions', dict(payload, reason='SIM-ONLY ' + payload['reason']))
        return dict(result=result, recovery=self.api('/api/recovery'))

    def release_recovery(self, payload):
        if not isinstance(payload, dict) or set(payload) != {'sessionId', 'reason'}:
            raise ValueError('恢复放行字段无效。')
        if not self.status().get('fresh') or not isinstance(payload.get('reason'), str) or len(payload['reason'].strip()) < 10:
            raise ValueError('需要新鲜设备观测和至少10个字符的放行原因。')
        recovery = self.api('/api/recovery')
        if recovery.get('released'):
            return dict(result=dict(Accepted=True, Message='already released'), recovery=recovery)
        if payload.get('sessionId') != recovery.get('sessionId'):
            raise ValueError('恢复会话已变化，请刷新。')
        if any(not item.get('verified') for item in recovery.get('items', [])):
            raise ValueError('仍有未核对条目，禁止放行。')
        result = self.api('/api/recovery/release', dict(sessionId=payload['sessionId'], reason='SIM-ONLY ' + payload['reason']))
        return dict(result=result, recovery=self.api('/api/recovery'))


def cleanup_owned_runtime(runtime, retry_delay=.2):
    """A still-finishing worker is not permission to orphan its owned children."""
    while True:
        try:
            return runtime.stop()
        except CleanupPending:
            time.sleep(retry_delay)
        except subprocess.TimeoutExpired:
            # stop() retains the Popen handle when a killed child has not yet exited.
            time.sleep(retry_delay)


def serve(root, port, token):
    from schemes import SchemeStore
    store = SchemeStore(WORKSPACE, root, validate_root)
    selected, notice = store.restore_root()
    runtime = Runtime(selected, template_root=root, scheme_store=store)
    if notice: runtime.error = notice
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args): pass
        def dispatch(self):
            if not hmac.compare_digest(self.headers.get('X-Simulation-Token', ''), token):
                self.send_error(403); return
            try:
                if self.command == 'GET':
                    from simulation_reports import report, replay
                    parsed = urlsplit(self.path)
                    query = parse_qs(parsed.query, strict_parsing=True)
                    if parsed.path == '/archive':
                        if query and (set(query) != {'id'} or len(query['id']) != 1): raise ValueError('报告查询参数无效。')
                        identity = query.get('id', [None])[0]
                        from simulation_archive import archive_evidence
                        if identity is None: raise ValueError('导出证据包需要报告编号。')
                        filename, payload = archive_evidence(runtime, identity)
                        self.send_response(200)
                        self.send_header('Content-Type', 'application/zip')
                        self.send_header('Content-Disposition', f'attachment; filename="{filename}"')
                        self.send_header('Content-Length', str(len(payload)))
                        self.send_header('Cache-Control', 'no-store')
                        self.end_headers()
                        self.wfile.write(payload)
                        return
                    if parsed.path == '/tasks': result = runtime.task_page(query)
                    elif parsed.path == '/task':
                        if set(query) != {'id'} or len(query['id']) != 1: raise ValueError('任务查询参数无效。')
                        result = runtime.task_detail(query['id'][0])
                    elif parsed.path == '/timeout-recovery':
                        if set(query) != {'id'} or len(query['id']) != 1 or not TASK_ID.fullmatch(query['id'][0]):
                            raise ValueError('超时恢复任务编号无效。')
                        result = runtime.api('/api/timeout-recovery/' + query['id'][0])
                    elif parsed.path == '/resources': result = runtime.task_resources()
                    elif parsed.path == '/snapshot': result = runtime.api('/api/snapshot')
                    elif parsed.path in ('/dispatch-pipeline', '/dispatch-optimization'):
                        if query: raise ValueError('只读查询参数无效。')
                        result = runtime.dispatch_diagnostics(parsed.path)
                    elif parsed.path in ('/report', '/replay'):
                        if query and (set(query) != {'id'} or len(query['id']) != 1): raise ValueError('报告查询参数无效。')
                        identity = query.get('id', [None])[0]
                        result = report(runtime, identity) if parsed.path == '/report' else replay(runtime, identity)
                    elif query:
                        raise ValueError('只读查询参数无效。')
                    elif parsed.path == '/status': result = runtime.status()
                    elif parsed.path == '/live': result = runtime.live_status()
                    elif parsed.path == '/point-table': result = runtime.current_point_table()
                    else: result = None
                else:
                    length = int(self.headers.get('Content-Length', 0))
                    if not 0 <= length <= 8 * 1024 * 1024: raise ValueError('请求过大。')
                    payload = json.loads(self.rfile.read(length) or b'{}')
                    if not isinstance(payload, dict): raise ValueError('请求应为JSON对象。')
                    if self.path == '/start': result = runtime.start()
                    elif self.path == '/stop': result = runtime.stop()
                    elif self.path == '/run': result = runtime.run_scenario(payload)
                    elif self.path == '/control': result = runtime.control(payload)
                    elif self.path == '/new-instance': result = runtime.new_instance(payload)
                    elif self.path == '/configure': result = runtime.configure(payload)
                    elif self.path == '/scheme-save': result = runtime.save_scheme(payload)
                    elif self.path == '/scheme-select': result = runtime.select_scheme(payload)
                    elif self.path == '/scheme-start': result = runtime.select_scheme(payload, start=True)
                    elif self.path == '/scheme-delete': result = runtime.delete_scheme(payload)
                    elif self.path == '/point-table-validate': result = runtime.validate_point_table(payload)
                    elif self.path == '/dispatch-optimization/options':
                        result = runtime.dispatch_diagnostics('/dispatch-optimization', payload)
                    elif self.path == '/timeout-recovery-execute':
                        allowed = {'taskId', 'expectedRevision', 'executeConfirmed',
                                   'vehicleAndLocationConfirmed', 'loadAndOwnershipConfirmed',
                                   'plcConditionsConfirmed', 'reason'}
                        if set(payload) != allowed or not isinstance(payload.get('taskId'), str) or not TASK_ID.fullmatch(payload['taskId']):
                            raise ValueError('人工恢复确认参数无效。')
                        result = runtime.api('/api/timeout-recovery/' + payload['taskId'] + '/execute',
                                             payload, allow_rejection=True)
                    elif self.path == '/recover': result = runtime.decide_recovery(payload)
                    elif self.path == '/recover-release': result = runtime.release_recovery(payload)
                    elif self.path == '/protocol-start': result = runtime.start_protocol(payload)
                    elif self.path == '/protocol-stop': result = runtime.stop_protocol()
                    elif self.path == '/equipment-start': result = runtime.start_equipment(payload)
                    elif self.path == '/equipment-control': result = runtime.control_equipment(payload)
                    elif self.path == '/equipment-stop': result = runtime.stop_equipment()
                    elif self.path == '/abort': runtime.abort.set(); result = runtime.status()
                    elif self.path == '/shutdown':
                        result = cleanup_owned_runtime(runtime); threading.Thread(target=server.shutdown, daemon=True).start()
                    else: result = None
                if result is None and self.path != '/report': self.send_error(404); return
                encoded = json.dumps(result, ensure_ascii=False, allow_nan=False).encode()
                self.send_response(200); self.send_header('Content-Type', 'application/json; charset=utf-8'); self.end_headers(); self.wfile.write(encoded)
            except Exception as error:
                encoded = json.dumps({'error': str(error)}, ensure_ascii=False).encode()
                self.send_response(400); self.send_header('Content-Type', 'application/json; charset=utf-8'); self.end_headers(); self.wfile.write(encoded)
        do_GET = dispatch
        do_POST = dispatch
    server = ThreadingHTTPServer(('127.0.0.1', port), Handler)
    stopping = threading.Event()
    parent = os.getppid()
    def watch_parent():
        # The daily launcher can terminate WebHost without a graceful .NET
        # shutdown. Hold the Windows parent handle so PID reuse cannot orphan
        # or accidentally attach the virtual devices to a different WebHost.
        handle = None
        kernel = None
        if os.name == 'nt':
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel.OpenProcess.restype = wintypes.HANDLE
            kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            handle = kernel.OpenProcess(0x100000, False, parent)
        try:
            while not stopping.wait(.5):
                if kernel:
                    alive = bool(handle) and kernel.WaitForSingleObject(handle, 0) == 258
                else:
                    try: os.kill(parent, 0); alive = True
                    except ProcessLookupError: alive = False
                if not alive:
                    try: cleanup_owned_runtime(runtime)
                    finally: server.shutdown()
                    return
        finally:
            if handle: kernel.CloseHandle(handle)
    threading.Thread(target=watch_parent, daemon=True).start()
    try: server.serve_forever(poll_interval=.2)
    finally:
        stopping.set()
        if runtime.protocol_runtime is not None:
            from protocol_twin import stop_protocol
            stop_protocol(runtime.protocol_runtime); runtime.protocol_runtime = None
        cleanup_owned_runtime(runtime); server.server_close()


if __name__ == '__main__':
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True); parser.add_argument('--port', type=int, required=True)
    args = parser.parse_args()
    token = os.environ.get('AGV_SIMULATION_TOKEN', '')
    if len(token) < 32: raise SystemExit('Controller requires a private bridge token.')
    serve(args.root, args.port, token)
