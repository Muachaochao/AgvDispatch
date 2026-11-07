"""Chinese recovery guidance and bounded, read-only historical replay."""
import json
import math
import re

REPORT_ID = re.compile(r'^[a-f0-9]{32}$')
MAX_REPORT_BYTES = 2 * 1024 * 1024
MAX_REPLAY_BYTES = 256 * 1024 * 1024
MAX_LINE_BYTES = 4 * 1024 * 1024
MAX_RESPONSE_BYTES = 7 * 1024 * 1024


def _linked(path):
    return path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction())


def _bounded_file(path, maximum):
    if _linked(path) or not path.is_file() or path.stat().st_size > maximum:
        raise ValueError('证据文件不存在或超出读取边界。')
    return path


def _finite_time(value):
    return value if type(value) in (int, float) and math.isfinite(value) else 0
SIGNALS = {'FullBoxRequested':'满箱呼叫', 'BoxPresent':'箱体检测', 'Faulted':'设备故障',
    'CommunicationOnline':'设备通讯', 'FullBoxRequestAcknowledged':'满箱呼叫响应',
    'FullBoxRemoved':'满箱已拉走', 'EmptyBoxTaskAcknowledged':'空箱任务响应',
    'EmptyBoxPlaced':'空箱已放到位', 'Ready':'倒料就绪', 'Done':'倒料完成',
    'DumperReady':'倒料就绪', 'DumpingCompleted':'倒料完成'}


def guidance(details, device):
    from scenarios import STEP_NAMES
    result = []
    for tid, detail in details.items():
        task = detail.get('task', {})
        if task.get('stage') in ('Completed', 'Cancelled'): continue
        steps = detail.get('steps', [])
        current = next((step for step in steps if step.get('isCurrent')), None)
        current = current or next((step for step in steps if step.get('status') in (
            'Ready','Executing','WaitingForTraffic','ManualIntervention','Failed')), {})
        vehicle = task.get('vehicleId')
        car = device.get('cars', {}).get(vehicle, {})
        unmet = [condition for condition in (current.get('progressConditions') or detail.get('plcConditions', []))
                 if condition.get('satisfied') is False]
        conditions = []
        for condition in unmet:
            label = SIGNALS.get(condition.get('variableName'), condition.get('name') or '设备条件')
            expected = '有效' if condition.get('expectedValue') else '复位'
            quality = condition.get('quality')
            conditions.append(f"{condition.get('stationId') or task.get('stationId') or '设备'}的{label}需{expected}" +
                              ('，当前通讯数据不可靠' if quality not in (None, 'Good') else ''))
        blocked = ('；'.join(conditions) or current.get('waitingExplanation') or
                   current.get('errorMessage') or detail.get('waitingExplanation') or
                   '等待正式调度与设备反馈')
        action = '按当前步骤核对车辆位置、载荷归属和设备周期；恢复后继续原任务。'
        feedback = '原任务编号保持不变，当前步骤完成后推进下一步；路权由正式后台按证据释放。'
        if car.get('offline'):
            blocked = f'{vehicle}通讯已断开'
            action = '在故障面板解除该车断连，核对停车位置和实际载荷；已有恢复条目需逐项核验。'
            feedback = '车辆恢复在线且观测新鲜，原运输编号仍一致，原任务继续。'
        elif car.get('paused'):
            blocked = f'{vehicle}处于暂停'
            action = '先确认路线可通行，再解除该车暂停。'
        elif car.get('obstacle') or car.get('blocked') or car.get('blockedByVehicle'):
            blocked = (f"前方车辆{car['blockedByVehicle']}占用通行空间"
                       if car.get('blockedByVehicle') else '车端仍报告障碍或任务已进入受阻冻结')
            action = ('先清除虚拟障碍并确认车辆停稳；如果任务已进入状态7，必须重新评估恢复候选，'
                      '不能直接恢复运动。')
            feedback = ('若存在可信站点和安全路线，由原车发送一次替代导航；否则先移动到安全点或转人工处置。')
        elif conditions:
            action = '核对并恢复：' + '；'.join(conditions) + '。先解除故障注入，按设备实际周期恢复条件。'
            feedback = '对应设备条件全部满足、数据新鲜，当前步骤自动推进；结束态信号保持复位，不额外置位。'
        elif not vehicle:
            action = '查看车辆排除原因：在线、空载、无原任务、无残留占用且能到达工位；忙车完成后由正式调度接单。'
            feedback = '此原任务获得唯一车辆；已执行任务不换车，同一车辆不同时承接两单。'
        result.append(dict(taskId=tid, vehicleId=vehicle, stationId=task.get('stationId'),
            stepNumber=current.get('sequence'), stepName=STEP_NAMES.get(current.get('stepType'), '等待派车'),
            blockedBy=blocked, waitReason=blocked, restoreAction=action, expectedFeedback=feedback,
            site=car.get('site'), x=car.get('x'), y=car.get('y')))
    return result


def _roots(runtime):
    from controller import validate_root
    roots = []
    try:
        current, *_ = validate_root(runtime.root)
        roots.append(current)
    except (OSError, ValueError, KeyError): pass
    store = getattr(runtime, 'scheme_store', None)
    for entry in store.catalog()['recentInstances'] if store else []:
        candidate = runtime.root.parent / str(entry.get('rootName', ''))
        try:
            candidate, *_ = validate_root(candidate)
            if candidate not in roots: roots.append(candidate)
        except (OSError, ValueError, KeyError): pass
    return roots


def _runs(runtime):
    checked = 0
    for root in _roots(runtime):
        for path in root.glob('simulation-run-*'):
            checked += 1
            if checked > 5000: return
            if _linked(path): continue
            if path.is_dir() and REPORT_ID.fullmatch(path.name.removeprefix('simulation-run-')):
                yield root, path


def history(runtime):
    from controller import read
    records = []
    for root, run in _runs(runtime):
        path = run / 'report.json'
        try:
            value = read(_bounded_file(path, MAX_REPORT_BYTES))
            if not isinstance(value, dict) or value.get('id') != run.name.removeprefix('simulation-run-'): continue
            records.append({key:value.get(key) for key in ('id','name','status','message','finishedAt','metrics','firstFailure')}
                           | dict(rootName=root.name))
        except (OSError, ValueError, TypeError): continue
    return sorted(records, key=lambda item:_finite_time(item.get('finishedAt')), reverse=True)[:100]


def find_run(runtime, identity):
    if not isinstance(identity, str) or not REPORT_ID.fullmatch(identity):
        raise ValueError('报告编号无效。')
    for _, run in _runs(runtime):
        if run.name == 'simulation-run-' + identity: return run
    raise ValueError('历史报告不存在或未保存在最近实例中。')


def report(runtime, identity=None):
    from controller import read
    if identity is None: return runtime.report
    path = find_run(runtime, identity) / 'report.json'
    value = read(_bounded_file(path, MAX_REPORT_BYTES))
    if not isinstance(value, dict) or value.get('id') != identity:
        raise ValueError('报告编号与证据目录不一致。')
    return value


def replay(runtime, identity):
    from controller import read
    run = find_run(runtime, identity)
    saved_report = read(_bounded_file(run / 'report.json', MAX_REPORT_BYTES))
    if not isinstance(saved_report, dict) or saved_report.get('id') != identity:
        raise ValueError('报告编号与证据目录不一致。')
    path = run / 'observations.jsonl'
    _bounded_file(path, MAX_REPLAY_BYTES)
    def lines():
        with path.open('rb') as stream:
            while True:
                line = stream.readline(MAX_LINE_BYTES + 1)
                if not line: break
                if len(line) > MAX_LINE_BYTES:
                    raise ValueError('单次采样超出4MB，不能在界面回放，请查看原始证据。')
                yield line
    count = sum(1 for _ in lines())
    stride = max(1, math.ceil(count/1999))
    map_path = run / 'map.json'
    navigation = None
    map_message = None
    if map_path.exists():
        if _linked(map_path) or not map_path.is_file() or map_path.stat().st_size > MAX_LINE_BYTES:
            map_message = '地图文件超出界面回放边界；位置与时间轴仍可查看，请在实例中查看原始地图。'
        else:
            navigation = read(map_path)
    map_bytes = len(json.dumps(navigation, ensure_ascii=False, allow_nan=False).encode('utf-8'))
    if map_bytes > MAX_LINE_BYTES:
        navigation = None
        map_bytes = 4
        map_message = '地图超出界面响应边界；请在实例中查看原始地图。'
    timeline_budget = MAX_RESPONSE_BYTES - map_bytes - 1024
    timeline = []
    timeline_bytes = 0
    for index, line in enumerate(lines()):
            if index % stride and index != count-1: continue
            value = json.loads(line)
            vehicles = [dict(vehicleId=vehicle, x=car.get('x'), y=car.get('y'), angle=car.get('angle'), site=car.get('site'),
                waitReason=car.get('waitReason'), taskId=car.get('current_task'))
                for vehicle, car in value.get('device', {}).get('cars', {}).items()]
            tasks = [dict(taskId=tid, stationId=task.get('stationId'), vehicleId=task.get('vehicleId'),
                stage=task.get('stage'), step=task.get('progress'), waitReason=task.get('waitingExplanation'))
                for tid, task in value.get('tasks', {}).items()]
            frame = dict(time=value['time'], vehicles=vehicles, tasks=tasks, leases=value.get('leases', []))
            frame_bytes = len(json.dumps(frame, ensure_ascii=False, allow_nan=False).encode('utf-8')) + 2
            if frame_bytes > timeline_budget:
                raise ValueError('单次回放数据过大，请查看实例中的原始采样证据。')
            timeline.append((index, frame, frame_bytes))
            timeline_bytes += frame_bytes
            while timeline_bytes > timeline_budget and len(timeline) > 1:
                stride *= 2
                reduced = [row for row in timeline if row[0] % stride == 0 or row[0] == count-1]
                timeline = reduced if len(reduced) < len(timeline) else [timeline[-1]]
                timeline_bytes = sum(row[2] for row in timeline)
    return dict(id=identity, timeline=[row[1] for row in timeline], map=navigation, mapMessage=map_message,
                totalSamples=count, sampled=count > len(timeline))
