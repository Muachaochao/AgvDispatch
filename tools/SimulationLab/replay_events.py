"""Project run-scoped evidence onto the saved simulation clock."""
from collections import deque
from datetime import datetime, timezone
import heapq
import json
import math

MAX_DEVICE_LOG_BYTES = 32 * 1024 * 1024
MAX_EVENTS = 8000

DEVICE_EVENTS = {
    'movement-start': ('motion', '开始移动'),
    'movement-complete': ('motion', '设备到站'),
    'plc-bit': ('plc', 'PLC 信号变化'),
    's7-write': ('plc', 'S7 写入'),
    'vehicle-obstacle-triggered': ('anomaly', '前方障碍'),
    'vehicle-navigation-timeout-triggered': ('anomaly', '设备导航超时'),
    'robokit-command-response-written': ('command', '仿真设备已写出响应'),
    'simulation-response-dropped': ('anomaly', '仿真丢弃命令回执'),
    'vendor-segment-cancelled': ('anomaly', '车端取消路段'),
    'safety-retreat-start': ('anomaly', '开始安全撤退'),
    'safety-retreat-complete': ('anomaly', '完成安全撤退'),
    'movement-rejected': ('anomaly', '设备拒绝移动'),
}


def stamp(value):
    if type(value) in (int, float) and math.isfinite(value):
        return value
    if isinstance(value, str):
        try:
            moment = datetime.fromisoformat(value.replace('Z', '+00:00'))
            return moment.timestamp() if moment.tzinfo else None
        except (ValueError, OverflowError, OSError):
            return None
    return None


def records(value):
    return (row for row in (value if isinstance(value, list) else []) if isinstance(row, dict))


def category(kind):
    folded = str(kind).lower()
    if any(part in folded for part in ('error', 'fail', 'timeout', 'recover', 'detour', 'cancel', 'block')):
        return 'anomaly'
    if any(part in folded for part in ('handshake', 'plc', 'signal')):
        return 'plc'
    if any(part in folded for part in ('agvsend', 'command')):
        return 'command'
    if any(part in folded for part in ('traffic', 'lease', 'resource', 'release')):
        return 'traffic'
    return 'task'


def linked(path):
    return path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction())


def resource_ids(raw):
    if isinstance(raw, str):
        try:
            raw = json.loads(raw)
        except ValueError:
            return []
    return sorted({value for value in raw if isinstance(value, str)}) if isinstance(raw, list) else []


class ReplayEvidence:
    def __init__(self, report, rate):
        self.start, self.end = stamp(report.get('startedAt')), stamp(report.get('finishedAt'))
        origin_ms = stamp(report.get('simulationOriginUnixMs'))
        self.origin = origin_ms / 1000 if origin_ms and origin_ms > 0 else None
        self.rate = rate
        self.events = []
        self.omitted = 0
        self.sequence = 0
        self.warnings = []
        self.commands = {}
        self.sent = {}
        self.received = set()
        self.previous_leases = None
        self.previous_waits = None
        self.previous_tasks = {}
        self.have_task_sample = False
        self.device_receipts = set()
        self.position_targets = deque()
        self.latest_observation = None

    def add(self, time, kind, group, title, source, **extra):
        time = stamp(time)
        if time is None or (self.start is not None and time < self.start) or (self.end is not None and time > self.end):
            return
        row = dict(time=time, kind=kind, category=group, title=title, source=source)
        if self.origin is not None:
            row['wallTime'] = self.origin + (time - self.origin) / self.rate
        row.update({key: value for key, value in extra.items() if value is not None})
        if self.latest_observation and time == self.latest_observation[0]:
            self.attach_position(row)
        relevant_release = kind == 'traffic-release' and (extra.get('detail') or {}).get('waitingRelated')
        if (relevant_release or group == 'anomaly' or kind in {
            'task-created', 'command-sent', 'device-accepted', 'device-rejected',
            'movement-start', 'movement-complete', 'plc-bit', 'plc-call',
            'robokit-command-response-written', 'AgvSendIntent', 'AgvSendAcknowledged',
            'traffic-wait', 'traffic-wait-ended', 'traffic-release-audited',
            'traffic-log-requested', 'traffic-log-waiting', 'traffic-log-blockingcleared',
        }):
            priority = 3
        elif kind in ('traffic-acquired', 'traffic-release', 'StepConditionsObserved', 'StepObservation'):
            priority = 0
        else:
            priority = 1 if source.endswith('采样') else 2
        self.sequence += 1
        entry = (priority, -self.sequence, row)
        if len(self.events) < MAX_EVENTS:
            heapq.heappush(self.events, entry)
        else:
            self.omitted += 1
            heapq.heappushpop(self.events, entry)

    def task_details(self, details):
        for task_id, detail in details.items():
            if not isinstance(detail, dict):
                continue
            task = detail.get('task')
            if not isinstance(task, dict) or task.get('taskId') != task_id:
                continue
            ref = dict(taskId=task_id, vehicleId=task.get('vehicleId'), stationId=task.get('stationId'))
            self.add(task.get('createdAt'), 'task-created', 'task', '任务建单', '任务持久化记录', **ref)
            for item in records(detail.get('executionTimeline')):
                self.add(item.get('occurredAt'), str(item.get('kind', 'task-event')), category(item.get('kind')),
                         item.get('title') or '任务事件', '任务执行日志', detail=item, **ref)
            for item in records(detail.get('operationHistory')):
                self.add(item.get('occurredAt'), 'task-operation', category(item.get('category')),
                         item.get('operation') or '任务操作', '任务操作日志',
                         resourceIds=[item['resourceId']] if item.get('resourceId') else [],
                         detail={k: item[k] for k in ('id', 'reason', 'category') if k in item}, **ref)
            for item in records(detail.get('commands')):
                command, vendor = item.get('commandId'), item.get('vendorCommandId')
                command_ref = dict(ref, vehicleId=item.get('vehicleId') or ref['vehicleId'], commandId=command,
                                   vendorCommandId=vendor)
                if vendor and command:
                    self.commands.setdefault(vendor, []).append(command_ref)
                if command and stamp(item.get('sentAt')) is not None:
                    self.sent[command] = command_ref
                self.add(item.get('sentAt'), 'command-sent', 'command', '后台记录命令已下发', '任务命令记录',
                         detail={k: item[k] for k in ('stepId', 'status', 'attemptCount') if k in item}, **command_ref)

    def call_rows(self, rows):
        if self.start is None:
            return
        for item in records(rows):
            offset = stamp(item.get('actualAtSeconds'))
            if offset is not None and offset >= 0:
                self.add(self.start + offset, 'plc-call', 'plc', 'PLC 呼叫触发', '场景呼叫记录',
                         taskId=item.get('taskId'), stationId=item.get('stationId'),
                         vehicleId=item.get('vehicleId'), detail={'planIndex': item.get('planIndex')})

    def traffic_lifecycle(self, rows):
        titles = {'Requested': '申请路权', 'Waiting': '等待路权', 'Acquired': '取得路权', 'Held': '保留路权',
                  'Released': '释放路权', 'SegmentReleased': '逐路段释放', 'BlockingStarted': '出现资源阻塞',
                  'BlockersChanged': '占用方变化', 'BlockingCleared': '资源阻塞解除'}
        for row in records(rows):
            kind = row.get('Kind')
            if not isinstance(kind, str):
                continue
            self.add(row.get('businessTime'), 'traffic-log-' + kind.lower(), 'traffic',
                     titles.get(kind, kind), '路权生命周期日志', taskId=row.get('TaskId'),
                     vehicleId=row.get('VehicleId'), resourceIds=resource_ids(row.get('ResourceIds')), detail=row)

    def release_audit(self, rows):
        for row in records(rows):
            raw = row.get('ReleasedAtUtc')
            try:
                time = datetime.fromisoformat(raw) if isinstance(raw, str) else None
            except ValueError:
                continue
            if time is None or not row.get('ResourceId'):
                continue
            if time.tzinfo is None:
                time = time.replace(tzinfo=timezone.utc)
            self.add(time.timestamp(), 'traffic-release-audited', 'traffic', '路权释放已审计', '路权提交审计',
                     taskId=row.get('TaskId'), vehicleId=row.get('VehicleId'), resourceIds=[row['ResourceId']],
                     detail=dict(row, reason='完整车体已离开资源边界，且原路线后续不再使用该资源。'))

    def recovery_audit(self, rows):
        for row in records(rows):
            vendor = row.get('NewVendorTaskId')
            if vendor and vendor not in self.commands:
                self.commands[vendor] = [dict(taskId=row.get('TaskId'), vehicleId=row.get('VehicleId'), vendorCommandId=vendor)]
            self.add(row.get('businessTime'), 'recovery-send-intent', 'anomaly', '改道续接发送意图', '恢复持久化审计',
                     taskId=row.get('TaskId'), vehicleId=row.get('VehicleId'),
                     vendorCommandId=row.get('NewVendorTaskId'), detail=row)

    def device_files(self, root):
        if self.origin is None or self.start is None or self.end is None:
            self.warnings.append('缺少本轮业务时钟原点或起止时间，设备日志未混入时间轴。')
            return
        robots = root / 'robots'
        paths = [(root / 'device-events.jsonl', None)]
        if robots.is_dir() and not linked(robots):
            for robot in sorted(robots.iterdir())[:64]:
                if robot.is_dir() and not linked(robot):
                    paths.append((robot / 'device-events.jsonl', robot.name))
        found = False
        for path, vehicle in paths:
            if linked(path) or not path.is_file():
                continue
            found = True
            try:
                if path.stat().st_size > MAX_DEVICE_LOG_BYTES:
                    self.warnings.append(f'{vehicle or "实例"}设备日志超出读取边界。')
                    continue
                self.read_device_file(path, vehicle)
            except (OSError, ValueError):
                self.warnings.append(f'{vehicle or "实例"}设备日志有无法读取的记录。')
        if not found:
            self.warnings.append('本轮实例未保留可读取的设备事件日志。')

    def read_device_file(self, path, vehicle):
        malformed = False
        with path.open('rb') as stream:
            offset = 0
            while offset < MAX_DEVICE_LOG_BYTES:
                line = stream.readline(1024 * 1024 + 1)
                if not line:
                    break
                offset += len(line)
                if len(line) > 1024 * 1024:
                    raise ValueError('Oversized device event')
                try:
                    item = json.loads(line)
                except ValueError:
                    malformed = True
                    continue
                if isinstance(item, dict):
                    self.device_event(item, vehicle)
        if malformed:
            self.warnings.append(f'{vehicle or "实例"}设备日志存在损坏或未写完的行。')

    def device_event(self, item, vehicle):
        wall = stamp(item.get('time'))
        if wall is None:
            return
        time = self.origin + (wall - self.origin) * self.rate
        if time < self.start or time > self.end:
            return
        kind = item.get('kind')
        if not isinstance(kind, str):
            return
        raw = {key: item[key] for key in ('address', 'before', 'value', 'byte', 'bit', 'hex', 'reason',
               'station', 'operation', 'api', 'sequence', 'task_ids') if key in item}
        if kind == 'robokit':
            api = item.get('api')
            if api not in (3051, 3055, 3066):
                return
            request, response = item.get('request'), item.get('response')
            if not isinstance(request, dict) or not isinstance(response, dict):
                return
            result = response.get('ret_code')
            if type(result) is not int:
                return
            segments = [entry.get('task_id') for entry in records(request.get('move_task_list'))]
            segments = [value for value in (segments or [request.get('task_id')]) if isinstance(value, str)]
            accepted = result == 0
            if not segments:
                return
            ref = self.device_ref(vehicle, segments[-1], chain=True)
            receipt = wall, ref.get('vehicleId') or vehicle, tuple(segments), result
            if receipt in self.device_receipts:
                return
            if len(self.device_receipts) < MAX_EVENTS:
                self.device_receipts.add(receipt)
            if ref.get('commandId'):
                self.received.add(ref['commandId'])
            self.add(time, 'device-accepted' if accepted else 'device-rejected', 'command',
                     '设备接受命令' if accepted else '设备拒绝命令', '设备事件日志',
                     detail=dict(raw, returnCode=result, segmentCount=len(segments),
                                 firstSegmentId=segments[0], lastSegmentId=segments[-1]), **ref)
            return
        if kind not in DEVICE_EVENTS:
            return
        group, title = DEVICE_EVENTS[kind]
        segment = item.get('task_id')
        task_ids = item.get('task_ids')
        if not segment and isinstance(task_ids, list) and task_ids:
            segment = task_ids[-1]
        ref = self.device_ref(vehicle, segment, chain=True)
        base = ref.get('vendorCommandId')
        if ref.get('commandId') and base and isinstance(segment, str):
            if kind == 'movement-start' and segment not in (base, base + '-01'):
                return
            if kind == 'movement-complete' and segment != base:
                return
        if isinstance(segment, str):
            raw['segmentId'] = segment
        self.add(time, kind, group, title, '设备事件日志', detail=raw, **ref)

    def device_ref(self, vehicle, vendor, *, chain=False):
        matches = self.commands.get(vendor, []) if isinstance(vendor, str) else []
        if chain and not matches and isinstance(vendor, str):
            # Robokit multi-segment command identifiers use the persisted base id plus a numeric suffix.
            base, separator, index = vendor.rpartition('-')
            if separator and index.isdecimal():
                matches = self.commands.get(base, [])
        matches = [item for item in matches if not vehicle or item.get('vehicleId') == vehicle]
        return matches[0].copy() if len(matches) == 1 else dict(vehicleId=vehicle,
                                                               vendorCommandId=vendor if isinstance(vendor, str) else None)

    def prepare_positions(self):
        self.position_targets = deque(sorted((row for _, _, row in self.events if row.get('vehicleId')),
                                             key=lambda row: row['time']))

    def attach_position(self, row):
        if self.latest_observation is None:
            return
        time, cars = self.latest_observation
        car = cars.get(row.get('vehicleId'))
        if not isinstance(car, dict) or stamp(car.get('x')) is None or stamp(car.get('y')) is None:
            return
        position = dict(time=time, x=car['x'], y=car['y'])
        if stamp(car.get('angle')) is not None:
            position['angle'] = car['angle']
        site = car.get('station') or car.get('site')
        if isinstance(site, str):
            position['site'] = site
        row['position'] = position

    def observe_position(self, sample, time):
        time = stamp(time)
        if time is None or self.latest_observation and time < self.latest_observation[0]:
            return
        while self.position_targets and self.position_targets[0]['time'] < time:
            self.attach_position(self.position_targets.popleft())
        cars = (sample.get('device') or {}).get('cars')
        self.latest_observation = (time, cars if isinstance(cars, dict) else {})
        while self.position_targets and self.position_targets[0]['time'] == time:
            self.attach_position(self.position_targets.popleft())

    def observe(self, sample):
        time = sample.get('time')
        self.observe_position(sample, time)
        leases = {(item.get('TaskId'), item.get('VehicleId'), item.get('ResourceId')): item
                  for item in records(sample.get('leases')) if item.get('ResourceId')}
        holders = {}
        for _, vehicle, resource in leases:
            if vehicle:
                holders.setdefault(resource, set()).add(vehicle)
        waits = {}
        for item in records(sample.get('trafficWaitRequests')):
            resources = tuple(resource_ids(item.get('ResourceIds')))
            if not resources:
                continue
            task, vehicle = item.get('TaskId'), item.get('VehicleId')
            blocking = tuple(sorted(set().union(*(holders.get(resource, set()) for resource in resources)) - {vehicle}))
            key = task, vehicle, resources, blocking
            waits[key] = item
            if self.previous_waits is not None and key not in self.previous_waits:
                self.add(time, 'traffic-wait', 'traffic', '观察到路权等待', '路权采样',
                         taskId=task, vehicleId=vehicle, resourceIds=list(resources),
                         holderVehicleIds=list(blocking), detail=item)
        if self.previous_waits is not None:
            remaining = {(task, vehicle, resources) for task, vehicle, resources, _ in waits}
            for task, vehicle, resources, _ in self.previous_waits.keys() - waits.keys():
                if (task, vehicle, resources) not in remaining:
                    self.add(time, 'traffic-wait-ended', 'traffic', '采样中等待消失', '路权采样',
                             taskId=task, vehicleId=vehicle, resourceIds=list(resources))
        wait_resources = {resource for _, _, resources, _ in (self.previous_waits or {}) for resource in resources}
        self.previous_waits = waits
        tasks = sample.get('tasks')
        if isinstance(tasks, dict) and tasks.get('sampleMode') != 'bounded-large-batch':
            for task_id, detail in tasks.items():
                if not isinstance(detail, dict):
                    continue
                vehicle, stage = detail.get('vehicleId'), detail.get('stage')
                previous = self.previous_tasks.get(task_id)
                station = detail.get('stationId')
                if self.have_task_sample and vehicle and (previous is None or previous[0] != vehicle):
                    self.add(time, 'assignment-observed', 'task', '观察到派车', '任务状态采样',
                             taskId=task_id, vehicleId=vehicle, stationId=station)
                if previous and stage != previous[1]:
                    self.add(time, 'stage-observed', 'task', '观察到任务阶段变化', '任务状态采样',
                             taskId=task_id, vehicleId=vehicle, stationId=station,
                             detail=dict(before=previous[1], after=stage, step=detail.get('progress')))
                self.previous_tasks[task_id] = (vehicle, stage)
            self.have_task_sample = True
        if self.previous_leases is not None:
            for current, other, kind, title in (
                (leases, self.previous_leases, 'traffic-acquired', '观察到资源占用'),
                (self.previous_leases, leases, 'traffic-release', '观察到资源释放'),
            ):
                groups = {}
                for task, vehicle, resource in current.keys() - other.keys():
                    groups.setdefault((task, vehicle), []).append(resource)
                for (task, vehicle), resources in groups.items():
                    self.add(time, kind, 'traffic', title, '路权采样',
                             taskId=task, vehicleId=vehicle, resourceIds=sorted(resources),
                             detail={'waitingRelated': True} if kind == 'traffic-release' and wait_resources.intersection(resources) else None)
        self.previous_leases = leases

    def finish(self, budget):
        while self.position_targets:
            self.attach_position(self.position_targets.popleft())
        results, used = [], 0
        for _, _, row in sorted(self.events, reverse=True):
            row_size = len(json.dumps(row, ensure_ascii=False, allow_nan=False).encode('utf-8')) + 64
            if used + row_size > budget:
                self.omitted += 1
                continue
            used += row_size
            results.append(row)
        results.sort(key=lambda e: (e['time'], e['kind'], str(e.get('taskId', '')),
                                    str(e.get('vehicleId', '')), str(e.get('resourceIds', ''))))
        missing = sorted(self.sent.keys() - self.received)
        if missing:
            self.warnings.append(f'{len(missing)} 条后台已下发命令未匹配到设备接收记录。')
        if self.omitted:
            self.warnings.append(f'事件响应达到保留上限，省略 {self.omitted} 条记录。')
        return results, dict(clockAligned=self.origin is not None, warnings=self.warnings,
                             omittedEvents=self.omitted, unconfirmedCommands=missing[:100])
