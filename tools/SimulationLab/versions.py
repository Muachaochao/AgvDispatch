"""Read-only, timestamped comparison of actual backend files and effective parameters."""
import copy
import datetime
import hashlib
import json
import os
from pathlib import Path
import threading
import time
from urllib.request import HTTPRedirectHandler, Request, build_opener


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('版本核对接口不接受重定向。')


def _fetch(port, path):
    if type(port) is not int or not 1024 <= port <= 65535 or not path.startswith('/api/'):
        raise ValueError('版本核对地址无效。')
    # Fixed loopback GETs only; no credentials, field commands, or proxy routing.
    from urllib.request import ProxyHandler
    with build_opener(ProxyHandler({}), _NoRedirect()).open(Request(f'http://127.0.0.1:{port}{path}', method='GET'), timeout=3) as response:
        content = response.read(8 * 1024 * 1024 + 1)
        if len(content) > 8 * 1024 * 1024:
            raise ValueError('版本观察响应过大。')
        return json.loads(content)


def _process_image(pid):
    if type(pid) is not int or pid <= 0:
        raise ValueError('后台进程编号无效。')
    if os.name != 'nt':
        raise ValueError('当前平台不能核实后台进程文件路径。')
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        raise OSError('无法读取现场后台进程路径。')
    try:
        size = wintypes.DWORD(32768)
        buffer = ctypes.create_unicode_buffer(size.value)
        if not kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            raise OSError('无法核实现场后台进程路径。')
        return Path(buffer.value)
    finally:
        kernel.CloseHandle(handle)


def _assemblies(directory):
    directory = Path(directory).absolute()
    for candidate in (directory, *directory.parents):
        if candidate.is_symlink() or (hasattr(candidate, 'is_junction') and candidate.is_junction()):
            raise ValueError('版本证据路径不能经过目录联接。')
    rows = []
    for path in sorted(directory.glob('AgvDispatch.*.dll')):
        if path.is_symlink():
            raise ValueError('程序集文件不能为链接。')
        before = path.stat()
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        after = path.stat()
        if (before.st_mtime_ns, before.st_size) != (after.st_mtime_ns, after.st_size):
            raise ValueError('程序集在核对期间变化，请重新核对。')
        rows.append(dict(name=path.name, path=str(path), sha256=digest, bytes=after.st_size))
    if not any(row['name'] == 'AgvDispatch.Service.dll' for row in rows):
        raise ValueError('未找到后台主程序集。')
    return rows


def _fresh(snapshot):
    if not isinstance(snapshot, dict) or snapshot.get('connected') is not True:
        raise ValueError('后台未连接，不能确认当前版本。')
    at = datetime.datetime.fromisoformat(snapshot['receivedAt'].replace('Z', '+00:00')).timestamp()
    if not 0 <= time.time() - at < 20:
        raise ValueError('后台版本观察已过期。')
    if not snapshot.get('status', {}).get('execution', {}).get('buildId'):
        raise ValueError('后台未报告构建哈希。')


def _parameters(snapshot, parameters):
    safety = snapshot.get('status', {}).get('safety', {})
    if parameters.get('mapId') != safety.get('activeMapId') or parameters.get('mapVersionId') != safety.get('activeMapVersionId'):
        raise ValueError('采集期间地图或任务参数已切换。')
    config = parameters['configuration']
    return dict(stations={row['stationId']: row['pickupSite'] for row in config.get('pickups', [])},
                waitingSites=config.get('waitingSites') or [config.get('waitingSite')],
                dumpingSite=config.get('dumpingSite'), dumperStationId=config.get('dumperStationId'),
                actions=config.get('actions', []), stepTimeouts=config.get('stepTimeouts', []),
                vehiclePools={row['stationId']: dict(vehiclePoolId=row.get('vehiclePoolId'), allowedVehicleIds=row.get('allowedVehicleIds'))
                              for row in config.get('pickups', [])},
                taskTemplate=config.get('taskTemplate'), parameterRevision=parameters.get('revision'),
                mapId=parameters.get('mapId'), mapVersionId=parameters.get('mapVersionId'))


def _snapshot_parameters(runtime):
    settings = runtime.settings
    dispatch = settings['Dispatching']
    return dict(stations=dispatch.get('StationPickupSites', {}),
                waitingSites=dispatch.get('DumperWaitingSites') or [dispatch.get('DumperWaitingSite')],
                dumpingSite=dispatch.get('DumperDumpingSite'), dumperStationId=settings.get('TaskCreation', {}).get('DumperStationId'),
                actions=None, stepTimeouts=None, vehiclePools=None, taskTemplate=None,
                parameterRevision=None, mapId=None, mapVersionId=None)


LABELS = dict(stations='工位与取箱位置', waitingSites='等待位置及顺序', dumpingSite='倒料位置',
              dumperStationId='倒料设备编号', actions='车辆动作参数', stepTimeouts='步骤超时参数',
              vehiclePools='工位可选车辆范围', taskTemplate='任务模板')


class VersionComparison:
    def __init__(self, runtime, field_port=5199, fetch=None, process_image=None):
        self.runtime = runtime
        self.field_port = field_port
        self.fetch = fetch or _fetch
        self.process_image = process_image or _process_image
        self.lock = threading.Lock()
        self.refresh_lock = threading.Lock()
        self.last_attempt = 0
        self.working = False
        self.value = dict(status='unknown', observedAt=None, field=None, simulation=None, differences=[],
                          message='正在读取现场后台构建和有效参数；尚未形成版本对照。',
                          scope='逐项对比后台 AgvDispatch 程序集 SHA256 与已读取的业务参数；不证明现场设备固件、机械或安全验收一致。')

    def snapshot(self):
        with self.lock:
            if not self.working and time.time() - self.last_attempt >= 15:
                self.working = True
                self.last_attempt = time.time()
                threading.Thread(target=self.refresh, daemon=True).start()
            result = copy.deepcopy(self.value)
        observed = result.get('observedAt')
        if observed is not None and time.time() - observed >= 40:
            result.update(status='unknown', message='版本对照观察已过期，正在重新读取；历史对照仅供追溯。')
        return result

    def refresh(self):
        with self.refresh_lock:
            return self._refresh()

    def _refresh(self):
        runtime = self.runtime
        result = dict(status='unknown', observedAt=time.time(), field=None, simulation=None, differences=[], scope=self.value['scope'])
        try:
            private_rows = _assemblies(runtime.root / 'service')
            result['simulation'] = dict(rootName=runtime.root.name, assemblies=private_rows, parameters=_snapshot_parameters(runtime),
                                        informationalVersion=None, buildId=next(row['sha256'] for row in private_rows if row['name'] == 'AgvDispatch.Service.dll'),
                                        parametersSource='已保存的实例设置；启动后才能核对完整有效参数')
            field_snapshot = self.fetch(self.field_port, '/api/snapshot')
            _fresh(field_snapshot)
            field_status = field_snapshot['status']
            image = self.process_image(field_status['service']['processId'])
            if image.name.casefold() != 'agvdispatch.service.exe':
                raise ValueError('现场后台进程文件身份不匹配，不能使用目录程序集代替。')
            field_rows = _assemblies(image.parent)
            field_execution = field_status['execution']
            service_hash = next(row['sha256'] for row in field_rows if row['name'] == 'AgvDispatch.Service.dll')
            if service_hash != field_execution['buildId'].lower():
                raise ValueError('现场实际进程目录与后台报告的程序集哈希不同，不能判定版本一致。')
            field_parameters = _parameters(field_snapshot, self.fetch(self.field_port, '/api/task-parameters'))
            result['field'] = dict(available=True, observedAt=time.time(), processId=field_status['service']['processId'],
                                   processPath=str(image), startedAt=field_status['service'].get('startedAt'),
                                   informationalVersion=field_execution.get('informationalVersion'), buildId=field_execution['buildId'],
                                   assemblies=field_rows, parameters=field_parameters,
                                   mapDefinitionHash=(field_snapshot.get('map') or {}).get('definitionHash'))
            simulation = result['simulation']
            private_effective = False
            if runtime.phase == 'running':
                private_snapshot = self.fetch(runtime.web['Port'], '/api/snapshot')
                _fresh(private_snapshot)
                private_status = private_snapshot['status']
                child = runtime.children.get('service')
                if child is None or private_status['service']['processId'] != child.pid or private_status['execution']['buildId'].lower() != simulation['buildId']:
                    raise ValueError('仿真后台进程或程序集身份变化，请等待重新核对。')
                simulation.update(informationalVersion=private_status['execution'].get('informationalVersion'),
                                  processId=child.pid, parameters=_parameters(private_snapshot, self.fetch(runtime.web['Port'], '/api/task-parameters')),
                                  parametersSource='当前仿真后台有效参数',
                                  mapDefinitionHash=(private_snapshot.get('map') or {}).get('definitionHash'))
                private_effective = True
            differences = []
            left = {row['name']: row['sha256'] for row in field_rows}
            right = {row['name']: row['sha256'] for row in private_rows}
            for name in sorted(left.keys() | right.keys()):
                if left.get(name) != right.get(name):
                    differences.append(dict(key='assembly:' + name, label='后台程序集 ' + name, field=left.get(name), simulation=right.get(name), kind='different'))
            for key, label in LABELS.items():
                actual = simulation['parameters'].get(key)
                if actual is None and not private_effective:
                    differences.append(dict(key=key, label=label, field=field_parameters.get(key), simulation=None, kind='unverified'))
                elif actual != field_parameters.get(key):
                    differences.append(dict(key=key, label=label, field=field_parameters.get(key), simulation=actual, kind='different'))
            if private_effective:
                if not result['field']['mapDefinitionHash'] or not simulation.get('mapDefinitionHash'):
                    differences.append(dict(key='mapDefinitionHash', label='当前运行地图内容',
                                            field=result['field']['mapDefinitionHash'], simulation=simulation.get('mapDefinitionHash'), kind='unverified'))
                elif result['field']['mapDefinitionHash'] != simulation['mapDefinitionHash']:
                    differences.append(dict(key='mapDefinitionHash', label='当前运行地图内容',
                                            field=result['field']['mapDefinitionHash'], simulation=simulation['mapDefinitionHash'], kind='different'))
                for key, label in dict(mode='执行方式', handshakeEnabled='业务握手推进开关', configuredContract='业务握手契约',
                                       taskCreationEnabled='任务创建开关', stepExecutionEnabled='步骤执行开关', outboxEnabled='车辆命令发送开关').items():
                    left_value, right_value = field_execution.get(key), private_status['execution'].get(key)
                    if left_value != right_value:
                        differences.append(dict(key='execution:' + key, label=label, field=left_value, simulation=right_value, kind='different'))
            result['differences'] = differences
            result['status'] = 'different' if any(row['kind'] == 'different' for row in differences) else 'unknown' if differences else 'matched'
            result['message'] = {'different': '仿真与现场存在构建或参数差异；本次通过仅适用于报告绑定的仿真版本。',
                                 'unknown': '已读取构建，仍有有效参数未核对；请启动仿真后复核。',
                                 'matched': '后台程序集与所列业务参数一致；仍需独立完成现场设备验收。'}[result['status']]
        except Exception as error:
            result.update(status='unknown', message='版本对照未完成：' + str(error))
        finally:
            with self.lock:
                self.value = result
                self.working = False
                self.last_attempt = time.time()
        return copy.deepcopy(result)
