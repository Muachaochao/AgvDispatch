"""Repeatable scenarios and independent read-only verdicts for the real scheduler."""
import hashlib
import json
import math
import sqlite3
import time
import uuid
from controller import read, write
from device_controls import PLC_DEFAULTS

NAMES = ('normal-cycle', 'simultaneous-calls', 'custom-calls', 'disconnect-reconnect',
         'plc-delay', 'plc-recovery', 'service-restart', 'resume-recovery')


def validate_scenario(value):
    if set(value) - {'name', 'rounds', 'intervalSeconds', 'timeoutSeconds', 'calls', 'plcFault', 'holdSeconds', 'restartStage'}:
        raise ValueError('未知场景参数。')
    result = dict(name=value.get('name', 'normal-cycle'), rounds=value.get('rounds', 1),
                  intervalSeconds=value.get('intervalSeconds', 0), timeoutSeconds=value.get('timeoutSeconds', 720))
    if result['name'] not in NAMES: raise ValueError('场景不存在。')
    if type(result['rounds']) is not int or not 1 <= result['rounds'] <= 100: raise ValueError('轮次须为1至100。')
    for key, low, high in [('intervalSeconds', 0, 60), ('timeoutSeconds', 30, 3600)]:
        number = result[key]
        if type(number) not in (int, float) or not math.isfinite(number) or not low <= number <= high: raise ValueError('场景时间参数无效。')
    if result['name'] == 'service-restart' and result['rounds'] != 1: raise ValueError('重启故障场景仅运行一轮，保留恢复现场。')
    if result['name'] == 'service-restart':
        stage = value.get('restartStage', 'pickup')
        if stage not in ('pickup', 'delivery'): raise ValueError('重启阶段须为取料运输或满箱运输。')
        result['restartStage'] = stage
    if result['name'] == 'resume-recovery' and result['rounds'] != 1: raise ValueError('恢复续接场景仅运行一轮。')
    if result['name'] == 'plc-recovery':
        if result['rounds'] != 1: raise ValueError('PLC卡位恢复场景仅运行一轮。')
        if value.get('plcFault') not in ('ready', 'done'): raise ValueError('PLC卡位须选择Ready或Done。')
        hold = value.get('holdSeconds', 10)
        if type(hold) not in (int, float) or not math.isfinite(hold) or not 1 <= hold <= 120:
            raise ValueError('PLC卡位保持时间须为1至120秒。')
        result.update(plcFault=value['plcFault'], holdSeconds=float(hold))
    calls = value.get('calls')
    if result['name'] == 'custom-calls':
        if not isinstance(calls, list) or not 1 <= len(calls) <= 500:
            raise ValueError('自定义呼叫计划须包含1至500条呼叫。')
        normalized = []
        simultaneous = {}
        for index, call in enumerate(calls):
            if not isinstance(call, dict) or set(call) - {'stationId', 'atSeconds', 'vehicleId'}:
                raise ValueError('呼叫计划字段无效。')
            station = call.get('stationId')
            at = call.get('atSeconds', 0)
            vehicle = call.get('vehicleId')
            if station not in ('CNC-001', 'CNC-002'):
                raise ValueError('呼叫工位必须是CNC-001或CNC-002。')
            if type(at) not in (int, float) or not math.isfinite(at) or not 0 <= at <= 86400:
                raise ValueError('呼叫时间须为0至86400秒。')
            if vehicle is not None and (not isinstance(vehicle, str) or not vehicle.strip()):
                raise ValueError('指定车辆编号无效。')
            key = round(float(at), 6)
            if station in simultaneous.setdefault(key, set()):
                raise ValueError('同一时刻不能重复呼叫同一工位。')
            simultaneous[key].add(station)
            if len(simultaneous[key]) > 2:
                raise ValueError('同一批次最多包含两个工位。')
            normalized.append(dict(stationId=station, atSeconds=float(at), vehicleId=vehicle))
        result['calls'] = normalized
    elif calls is not None:
        raise ValueError('仅自定义呼叫场景接受calls参数。')
    if result['name'] != 'plc-recovery' and ('plcFault' in value or 'holdSeconds' in value):
        raise ValueError('仅PLC卡位恢复场景接受plcFault和holdSeconds。')
    if result['name'] != 'service-restart' and 'restartStage' in value:
        raise ValueError('仅后台重启场景接受restartStage。')
    return result


def query(root, sql, parameters=()):
    with sqlite3.connect((root / 'data/agv-dispatch.db').as_uri() + '?mode=ro', uri=True, timeout=3) as db:
        db.row_factory = sqlite3.Row
        return [dict(row) for row in db.execute(sql, parameters)]


def separation(a0, a1, b0, b1):
    """Minimum separation for linearly interpolated paired samples, no model feedback."""
    dx, dy = a0['x'] - b0['x'], a0['y'] - b0['y']
    vx, vy = a1['x'] - b1['x'] - dx, a1['y'] - b1['y'] - dy
    t = max(0, min(1, -(dx * vx + dy * vy) / (vx * vx + vy * vy))) if vx * vx + vy * vy else 0
    return math.hypot(dx + vx * t, dy + vy * t)


def control_applied(state, controls, vehicle=None):
    """A written input is not a device acknowledgement. Match the applied sequence and fields."""
    if time.time() - state.get('time', 0) > 3:
        return None
    observed = state.get('cars', {}).get(vehicle, {}).get('simulationControl', {}) if vehicle else state.get('simulationControl', {})
    if observed.get('sequence') != controls['sequence'] or observed.get('error'):
        return None
    expected = controls.get('vehicles', {}).get(vehicle, {}) if vehicle else controls.get('plc', {})
    actual = observed if vehicle else observed.get('plc', {})
    # Response loss is one-shot; its post-consumption pending flag is not a persistent setting.
    if any(actual.get(key) != value for key, value in expected.items() if key != 'dropNextResponse'):
        return None
    return observed


def complete_command_set(commands, tasks):
    """Totals alone can hide one task's duplicate replacing another task's missing command."""
    if len(commands) != len(tasks) * 4 or len({c['CommandId'] for c in commands}) != len(commands):
        return False
    for task in tasks:
        rows = [c for c in commands if c['TaskId'] == task]
        if len(rows) != 4 or len({c['StepId'] for c in rows}) != 4 or any(c['Status'] != 30 or c['AttemptCount'] != 1 for c in rows):
            return False
    return True


class Observer:
    def __init__(self, runtime, evidence):
        self.runtime, self.evidence = runtime, evidence
        self.previous = None
        self.samples = 0
        self.minimum = None
        self.violations = []
        self.max_wait = 0
        self.wait_started = {}
        self.last_details = {}
        self.seen_details = {}
        self.resources = {r['id']: r.get('capacity', 1) for r in read(runtime.root / 'navigation.agvmap.json')['resources']}
        self.radii = {p['VehicleId']: p['RadiusFromReportedReferenceMeters'] for p in runtime.settings['HandshakeExecution']['DumperExit']['BodyProfiles']}

    def sample(self, details):
        self.last_details = details
        self.seen_details.update(details)
        state = self.runtime.device()
        if time.time() - state.get('time', 0) > 3: raise RuntimeError('设备快照过期，不能继续判定。')
        leases = query(self.runtime.root, 'select ResourceId,TaskId,VehicleId,OwnerStepId from traffic_resource_lease')
        for resource, capacity in self.resources.items():
            owners = {r['TaskId'] for r in leases if r['ResourceId'] == resource}
            if len(owners) > capacity: self.violations.append(dict(kind='capacity', resource=resource, owners=sorted(owners)))
        cars = state['cars']
        ids = sorted(cars)
        for i, first in enumerate(ids):
            for second in ids[i + 1:]:
                if first not in self.radii or second not in self.radii: raise RuntimeError('缺少合成车体尺寸，无法判定碰撞。')
                previous = self.previous or state
                distance = separation(previous['cars'][first], cars[first], previous['cars'][second], cars[second])
                self.minimum = distance if self.minimum is None else min(self.minimum, distance)
                limit = self.radii[first] + self.radii[second]
                if distance < limit: self.violations.append(dict(kind='envelope-overlap', vehicles=[first, second], distance=distance, threshold=limit))
        now = time.monotonic()
        for tid, detail in details.items():
            signature = tuple(s['status'] for s in detail['steps'])
            prior = self.wait_started.get(tid)
            if not prior or prior[0] != signature: self.wait_started[tid] = (signature, now)
            self.max_wait = max(self.max_wait, now - self.wait_started[tid][1])
        self.samples += 1; self.previous = state
        with (self.evidence / 'observations.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(dict(time=time.time(), device=state, leases=leases,
                tasks={t:dict(stage=d['task']['stage'], steps=[s['status'] for s in d['steps']]) for t, d in details.items()}), ensure_ascii=False) + '\n')
        if self.violations: raise RuntimeError('独立交通检查发现资源超容量或合成包络重叠。')
        return leases


def execute(runtime, request):
    started = time.time()
    evidence = runtime.root / ('simulation-run-' + runtime.scenario['id'])
    evidence.mkdir(exist_ok=False)
    write(evidence / 'scenario.json', dict(**request, startedAt=started, rootName=runtime.root.name))
    checks = []
    tasks = {}
    observer = None
    fault_observed = False
    plc_fault_controls = None
    recovery_hold = False
    completed_tasks = set()
    result_status = 'failed'
    message = ''
    def check(name, passed, detail):
        checks.append(dict(name=name, status='passed' if passed else 'failed', detail=detail))
        if not passed: raise RuntimeError(name + '：' + str(detail))
    def create(station, vehicle, trigger=True):
        if trigger:
            write(runtime.root / 'device-input.json', dict(sequence=uuid.uuid4().hex, call=station))
        def available():
            value = runtime.api('/api/task-options')
            return value if value['available'] and any(s['stationId'] == station and s['available'] for s in value['stations']) else None
        options = runtime.wait('PLC呼叫未满足建单条件：' + station, available, 40)
        station_option = next(s for s in options['stations'] if s['stationId'] == station)
        requested = vehicle
        if requested is None:
            reachable = station_option.get('pickupReachableVehicleIds')
            available = options.get('vehicles')
            candidates = [v['vehicleId'] for v in (available or [])
                          if v.get('available') and (not isinstance(reachable, list) or v['vehicleId'] in reachable)]
            if not candidates and not isinstance(available, list):
                candidates = [r['VehicleId'] for r in runtime.settings['Agv']['Robots']]
            if not candidates:
                raise RuntimeError('工位没有可自动编排的在线车辆：' + station)
            requested = candidates[0]
        elif (isinstance(options.get('vehicles'), list) and not any(v['vehicleId'] == requested and v.get('available')
                     for v in options['vehicles'])) or (
                         isinstance(station_option.get('pickupReachableVehicleIds'), list) and
                         requested not in station_option['pickupReachableVehicleIds']):
            raise RuntimeError(f'指定车辆{requested}当前不可用或无法到达{station}。')
        payload = dict(requestId=uuid.uuid4().hex, stationId=station, requestedVehicleId=requested, priority=0,
                       mapId=options['mapId'], mapVersionId=options['mapVersionId'], parameterRevision=options['parameterRevision'])
        response = runtime.api('/api/tasks', payload)
        tid = response['taskId']; tasks[tid] = dict(vehicle=requested, station=station)
        write(evidence / 'task-identities.json', tasks)
        write(evidence / ('create-' + tid + '.json'), dict(request=payload, response=response))
        return tid
    def complete(ids):
        nonlocal fault_observed, recovery_hold
        deadline = time.monotonic() + request['timeoutSeconds']
        write(evidence / ('completion-start-' + uuid.uuid4().hex + '.json'), dict(taskIds=list(ids), startedAt=time.time()))
        offline_at = None; fault_vehicle = None; held = set(); restored = False; offline_controls = None
        plc_fault_started = None; plc_recovery_controls = None
        while time.monotonic() < deadline:
            if runtime.abort.is_set(): raise InterruptedError('场景已中止，原任务及占用保留；请核对后恢复。')
            details = {tid: runtime.api('/api/tasks/' + tid) for tid in ids}
            leases = observer.sample(details)
            if any(d['task']['stage'] in ('Faulted', 'Cancelled') for d in details.values()): raise RuntimeError('任务出现非预期故障或取消。')
            name = request['name']
            state = runtime.device()
            if name == 'plc-recovery' and plc_fault_started is None:
                target_index = 3 if request['plcFault'] == 'ready' else 5
                target = next((tid for tid, detail in details.items()
                    if len(detail['steps']) > target_index and detail['steps'][target_index]['status'] in ('Ready', 'Executing')), None)
                if target:
                    signal = 'suppressReady' if request['plcFault'] == 'ready' else 'suppressDone'
                    runtime.control(dict(plc={signal: True}))
                    plc_recovery_controls = json.loads(json.dumps(runtime.controls))
                    applied = runtime.wait('PLC卡位控制未生效', lambda: control_applied(runtime.device(), plc_recovery_controls), 10)
                    check('PLC卡位在建单执行中实际生效', True, dict(taskId=target, signal=signal, applied=applied))
                    write(evidence / 'plc-fault-applied.json', dict(taskId=target, signal=signal, applied=applied, detail=details[target]))
                    plc_fault_started = time.monotonic()
            if name == 'plc-recovery' and plc_fault_started is not None and not fault_observed and time.monotonic() - plc_fault_started >= request['holdSeconds']:
                target_index = 3 if request['plcFault'] == 'ready' else 5
                target = next(tid for tid in ids if len(details[tid]['steps']) > target_index)
                check('卡位期间原任务保持等待且未跳步', details[target]['steps'][target_index]['status'] in ('Ready', 'Executing'),
                      details[target]['steps'][target_index])
                signal = 'suppressReady' if request['plcFault'] == 'ready' else 'suppressDone'
                runtime.control(dict(plc={signal: False}))
                acknowledged = runtime.wait('PLC卡位解除未被设备确认', lambda: control_applied(runtime.device(), runtime.controls), 10)
                write(evidence / 'plc-recovery-applied.json', acknowledged)
                fault_observed = True
            fault_index = (0 if request.get('restartStage', 'pickup') == 'pickup' else 2) if name == 'service-restart' else 2
            candidate = next((tid for tid, d in details.items() if len(d['steps']) > fault_index and d['steps'][fault_index]['status'] == 'Executing'
                and state.get('cars', {}).get(tasks[tid]['vehicle'], {}).get('current_task')
                and (fault_index == 0 or any(r['TaskId'] == tid for r in leases))), None)
            if name in ('disconnect-reconnect', 'service-restart') and not fault_observed and offline_at is None and candidate:
                fault_vehicle = tasks[candidate]['vehicle']
                held = {r['ResourceId'] for r in leases if r['TaskId'] == candidate}
                check('故障前原运输正在执行' + ('且持有路权' if fault_index == 2 else ''),
                      bool(state['cars'][fault_vehicle].get('current_task')) and (fault_index == 0 or bool(held)),
                      dict(taskId=candidate, held=sorted(held)))
                if name == 'service-restart':
                    commands_before = query(runtime.root, 'select CommandId from agv_command_outbox where TaskId=?', (candidate,))
                    old = runtime.children['service']; old.terminate(); old.wait(timeout=10)
                    runtime.children.pop('service')
                    runtime.launch('service', [str(runtime.root / 'service/AgvDispatch.Service.exe')], runtime.root / 'service')
                    runtime.wait('重启后后台连接超时', runtime.connected)
                    recovery = runtime.api('/api/recovery')
                    write(evidence / 'restart-recovery.json', recovery)
                    check('重启保留恢复屏障', not recovery['released'] and any(i.get('taskId') == candidate for i in recovery['items']), '原活动任务未自动批准恢复')
                    remaining = query(runtime.root, 'select ResourceId,TaskId from traffic_resource_lease')
                    if fault_index == 2:
                        check('重启保留原任务与路权', held <= {r['ResourceId'] for r in remaining if r['TaskId'] == candidate}, sorted(held))
                    else:
                        check('重启保留原运输身份', runtime.device()['cars'][fault_vehicle].get('current_task') is not None,
                              runtime.device()['cars'][fault_vehicle].get('current_task'))
                    retained = runtime.api('/api/tasks/' + candidate)
                    write(evidence / 'restart-original-task.json', retained)
                    commands_after = query(runtime.root, 'select CommandId from agv_command_outbox where TaskId=?', (candidate,))
                    check('重启未补造完成或替换原命令', retained['task']['stage'] not in ('Completed', 'Cancelled') and
                          bool(commands_before) and {c['CommandId'] for c in commands_before} == {c['CommandId'] for c in commands_after}, candidate)
                    fault_observed = True
                    recovery_hold = True
                    return False
                runtime.control(dict(vehicles={fault_vehicle: dict(offline=True)})); offline_at = time.monotonic()
                offline_controls = json.loads(json.dumps(runtime.controls))
                write(evidence / 'disconnect.json', dict(taskId=candidate, held=sorted(held), device=runtime.device()))
            if offline_at and not restored and time.monotonic() - offline_at >= 5:
                car = runtime.device()['cars'][fault_vehicle]
                check('断连故障已被模型应用', car.get('offline') is True and bool(control_applied(runtime.device(), offline_controls, fault_vehicle)), fault_vehicle)
                tid = next(t for t in ids if tasks[t]['vehicle'] == fault_vehicle)
                check('断连保留原路权', held <= {r['ResourceId'] for r in leases if r['TaskId'] == tid}, sorted(held))
                runtime.control(dict(vehicles={fault_vehicle: dict(offline=False)}))
                acknowledged = runtime.wait('重连控制未被设备确认', lambda: control_applied(runtime.device(), runtime.controls, fault_vehicle), 10)
                write(evidence / 'reconnect-applied.json', acknowledged)
                restored = True; fault_observed = True
            if all(d['task']['stage'] == 'Completed' for d in details.values()):
                write(evidence / ('completion-observed-' + uuid.uuid4().hex + '.json'),
                      {tid: dict(stage=detail['task']['stage'], steps=[step['status'] for step in detail['steps']])
                       for tid, detail in details.items()})
                write(evidence / ('completed-' + uuid.uuid4().hex + '.json'), details)
                check('原任务九步完成', all(len(d['steps']) == 9 and all(s['status'] == 'Completed' for s in d['steps']) for d in details.values()), list(ids))
                check('原任务出区证据', all(d.get('handover', {}).get('exitPhysicallyVerified') for d in details.values()), list(ids))
                completed_tasks.update(ids)
                return True
            runtime.scenario['message'] = '；'.join(f"{tasks[t]['vehicle']}：{d['task']['stage']} / " + ','.join(s['status'] for s in d['steps']) for t, d in details.items())
            runtime.abort.wait(.5)
        raise TimeoutError('任务未在期限内闭合，可能存在长期等待；保留全部现场。')
    try:
        observer = Observer(runtime, evidence)
        recovering = request['name'] == 'resume-recovery'
        if recovering:
            recovery = runtime.api('/api/recovery')
            write(evidence / 'resume-recovery-state.json', recovery)
            check('恢复屏障已经人工放行', recovery.get('released') is True, recovery.get('message'))
            page = runtime.api('/api/tasks?page=1&pageSize=100')
            active = [item for item in page.get('items', []) if item.get('stage') not in ('Completed', 'Cancelled')]
            if not active: raise RuntimeError('没有可续接的原任务。')
            for item in active:
                tasks[item['taskId']] = dict(vehicle=item.get('vehicleId'), station=item.get('stationId'))
            write(evidence / 'resumed-task-identities.json', tasks)
            complete(list(tasks))
            runtime.scenario['completedRounds'] = 1
        else:
            runtime.idle_review()
            check('运行前无残留路权', not query(runtime.root, 'select ResourceId from traffic_resource_lease'), '只读数据库核对')
        if any(v.get('offline') or v.get('paused') or v.get('dropNextResponse') or v.get('blockedEdges') for v in runtime.controls['vehicles'].values()) or any(value != PLC_DEFAULTS.get(key) for key, value in runtime.controls['plc'].items()):
            raise RuntimeError('开始场景前请解除人工故障；不自动覆盖控制。')
        if request['name'] == 'plc-delay':
            runtime.control(dict(plc=dict(readyDelaySeconds=3, doneDelaySeconds=8)))
            plc_fault_controls = json.loads(json.dumps(runtime.controls))
            applied = runtime.wait('PLC延迟控制未被设备应用', lambda: control_applied(runtime.device(), plc_fault_controls), 10)
            write(evidence / 'plc-delay-applied.json', applied)
            check('PLC延迟在建单前被设备确认', True, applied)
            fault_observed = True
        pairs = list(zip(runtime.settings['Dispatching']['StationPickupSites'], [r['VehicleId'] for r in runtime.settings['Agv']['Robots']]))
        if not pairs: raise ValueError('没有可测试的车辆与工位配对。')
        if request['name'] == 'plc-recovery': pairs = pairs[:1]
        if request['name'] == 'simultaneous-calls' and len(pairs) < 2: raise ValueError('同时呼叫场景至少需要两辆车和两个工位。')
        if request['name'] == 'custom-calls':
            ordered = sorted(request['calls'], key=lambda call: (call['atSeconds'], call['stationId']))
            grouped = []
            for call in ordered:
                if not grouped or grouped[-1][0] != call['atSeconds']:
                    grouped.append((call['atSeconds'], [call]))
                else:
                    grouped[-1][1].append(call)
            all_created = []
            round_started = time.monotonic()
            station_offsets = {station['stationId']: station['offset'] for station in runtime.lab.get('simulationTopology', {}).get('stations', [])}
            def calls_closed(group_calls):
                memory = bytes.fromhex(runtime.device()['memory_hex'])
                return memory[610] == 0 and all(memory[station_offsets.get(call['stationId'], 0 if call['stationId'] == 'CNC-001' else 2)] == 10
                                                for call in group_calls)
            closed_since = None
            def cycles_stable(group_calls):
                nonlocal closed_since
                if not calls_closed(group_calls):
                    closed_since = None
                    return False
                closed_since = closed_since or time.monotonic()
                return time.monotonic() - closed_since >= 1.5
            for due, group in grouped:
                remaining = due - (time.monotonic() - round_started)
                if remaining > 0 and runtime.abort.wait(remaining): raise InterruptedError('场景已中止。')
                if all_created:
                    closed_since = None
                    runtime.wait('上一轮工位握手尚未稳定闭合', lambda: cycles_stable(group), request['timeoutSeconds'])
                stations = [call['stationId'] for call in group]
                sequence = uuid.uuid4().hex
                payload = dict(sequence=sequence, call=stations[0]) if len(stations) == 1 else dict(sequence=sequence, calls=stations)
                write(runtime.root / 'device-input.json', payload)
                applied = runtime.wait('自定义呼叫批次未被设备确认', lambda: runtime.device().get('externalInput') if runtime.device().get('externalInput', {}).get('sequence') == sequence else None, 10)
                check('自定义呼叫批次已由设备应用', applied.get('accepted') is True, applied)
                write(evidence / ('call-batch-' + str(len(all_created)) + '-' + uuid.uuid4().hex + '.json'),
                      dict(request=payload, requestedAtSeconds=due, actualElapsedSeconds=time.monotonic()-round_started, applied=applied))
                batch_ids = [create(call['stationId'], call.get('vehicleId'), trigger=False) for call in group]
                all_created.extend(batch_ids)
                complete(batch_ids)
                runtime.scenario['completedRounds'] = len(all_created)
            check('全部自定义呼叫均已创建并完成原任务', len(all_created) == len(request['calls']), f'{len(all_created)}个任务')
            runtime.scenario['completedRounds'] = len(all_created)
        elif request['name'] != 'resume-recovery':
            for iteration in range(request['rounds']):
                if request['name'] in ('simultaneous-calls', 'disconnect-reconnect'):
                    sequence = uuid.uuid4().hex
                    write(runtime.root / 'device-input.json', dict(sequence=sequence, calls=[station for station, _ in pairs]))
                    applied = runtime.wait('批量呼叫未被设备确认', lambda: runtime.device().get('externalInput') if runtime.device().get('externalInput', {}).get('sequence') == sequence else None, 10)
                    check('同一扫描周期批量呼叫已应用', applied.get('accepted') is True, applied)
                    write(evidence / ('batch-call-' + str(iteration) + '.json'), applied)
                    ids = [create(station, vehicle, trigger=False) for station, vehicle in pairs]
                    complete(ids)
                else:
                    for station, vehicle in pairs:
                        if not complete([create(station, vehicle)]): break
                if request['name'] == 'service-restart': break
                def closed():
                    memory = bytes.fromhex(runtime.device()['memory_hex'])
                    offsets = [station.get('offset', 0) for station in getattr(runtime, 'lab', {}).get('simulationTopology', {}).get('stations', [])] or [0, 2]
                    return all(memory[o] == 10 for o in offsets) and memory[610] == 0
                runtime.wait('PLC最终握手未闭合', closed, 30)
                check('本轮PLC握手闭合', True, '工位箱体/在线初态，H输出和倒料周期复位')
                runtime.scenario['completedRounds'] = iteration + 1
                if runtime.abort.wait(request['intervalSeconds']): raise InterruptedError('场景已中止。')
        if request['name'] != 'service-restart':
            check('最终无残留路权', not query(runtime.root, 'select ResourceId from traffic_resource_lease'), '所有资源租约已释放')
            commands = query(runtime.root, 'select TaskId,StepId,CommandId,Status,AttemptCount from agv_command_outbox')
            commands = [c for c in commands if c['TaskId'] in tasks]
            write(evidence / 'outbox-readonly.json', commands)
            check('运输命令无重复或遗漏', complete_command_set(commands, tasks), f'{len(commands)}条原命令 / {len(tasks)}个任务')
            sends = []
            allowed_step_ids = {
                str(value)
                for detail in (getattr(observer, 'seen_details', None) or getattr(observer, 'last_details', None) or {}).values()
                for step in detail.get('steps', [])
                for value in (step.get('stepId'), step.get('vendorTaskId'))
                if value
            }
            for vehicle in {t['vehicle'] for t in tasks.values()}:
                source = runtime.root / 'robots' / vehicle / 'device-events.jsonl'
                if not source.exists(): source = runtime.root / 'device-events.jsonl'
                for line in source.read_text(encoding='utf-8').splitlines():
                    event = json.loads(line)
                    requested_ids = {str(item.get('task_id')) for item in event.get('request', {}).get('move_task_list', [])}
                    if ((recovering or event['time'] >= started) and event['kind'] == 'robokit' and event['api'] == 3066 and
                            event['response'].get('ret_code') == 0 and (not allowed_step_ids or requested_ids & allowed_step_ids)):
                        sends.append(dict(vehicle=vehicle, **event))
            write(evidence / 'accepted-3066.json', sends)
            check('设备实际接收原运输命令', len(sends) == len(tasks) * 4, f'{len(sends)}次成功接收')
        if request['name'] in ('disconnect-reconnect', 'service-restart', 'plc-recovery'):
            check('指定阶段故障实际触发', fault_observed, request['name'])
        if request['name'] == 'plc-delay':
            applied = control_applied(runtime.device(), plc_fault_controls)
            check('PLC延迟控制被设备确认', bool(applied), applied)
            runtime.control(dict(plc=dict(readyDelaySeconds=0, doneDelaySeconds=5)))
            runtime.controls['plc'] = {}
        check('采样期间资源容量与合成包络检查', observer.samples > 0 and not observer.violations, f'{observer.samples}次独立采样；相邻采样间线性包络检查')
        result_status = 'passed'; message = '恢复屏障已验证，保留活动任务待核对。' if request['name'] == 'service-restart' else '场景及闭环检查通过。'
    except InterruptedError as error:
        result_status = 'aborted'; message = str(error)
        checks.append(dict(name='场景中止', status='unknown', detail=message))
    except Exception as error:
        message = str(error); checks.append(dict(name='场景执行', status='failed', detail=message))
    finally:
        if observer and getattr(observer, 'last_details', None):
            write(evidence / 'last-task-details.json', observer.last_details)
        if observer and getattr(observer, 'seen_details', None):
            write(evidence / 'all-task-details.json', observer.seen_details)
        write(evidence / 'final-device-observation.json', runtime.device())
        report = dict(id=runtime.scenario['id'], name=request['name'], status=result_status, message=message, checks=checks,
            completedBusinessCycle=result_status == 'passed' and bool(tasks) and completed_tasks == set(tasks) and not recovery_hold,
            recoveryHold=recovery_hold, faultApplied=fault_observed if request['name'] in
                ('disconnect-reconnect', 'service-restart', 'plc-delay', 'plc-recovery') else None,
            startedAt=started, finishedAt=time.time(), evidenceDirectory=evidence.name,
            metrics=dict(taskCount=len(tasks), completedRounds=runtime.scenario['completedRounds'], durationSeconds=time.time()-started,
                         samples=observer.samples if observer else 0, minimumSeparationMeters=observer.minimum if observer else None,
                         longestUnchangedStepSeconds=observer.max_wait if observer else 0),
            violations=observer.violations if observer else [], scope='正式后台 + 回环Robokit/S7合成模型；碰撞为采样间线性包络检查；超时可发现长期等待，不构成死锁/公平性证明；不代替现场固件、机械及安全验收。')
        write(evidence / 'report.json', report)
        files = [dict(path=p.name, sha256=hashlib.sha256(p.read_bytes()).hexdigest(), bytes=p.stat().st_size) for p in sorted(evidence.iterdir()) if p.is_file()]
        write(evidence / 'manifest.json', files)
        write(runtime.root / 'simulation-report.json', report)
        runtime.report = report
        runtime.scenario.update(status=result_status, message=message)
