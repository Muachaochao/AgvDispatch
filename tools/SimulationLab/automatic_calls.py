"""Generate physical station calls while the production scheduler owns all tasks."""
import json
import time
import uuid


def run_backlog(runtime, request, evidence, observer, tasks, completed, discover, check):
    from controller import write
    from scenarios import describe_progress, fresh_device_state, external_input
    if runtime.settings.get('TaskCreation', {}).get('AutomaticCreationEnabled') is not True:
        raise ValueError('持续积压需要正式PLC自动接单模式。')
    offsets = {station['stationId']: station['offset'] for station in runtime.lab['simulationTopology']['stations']}
    pending = [dict(call, planIndex=index) for index, call in enumerate(request['calls'])]
    pending.sort(key=lambda call: (call['atSeconds'], call['planIndex']))
    inflight, stable_since, details = {}, {}, {}
    started = time.monotonic()
    # Full-factory startup and the first multi-station PLC scan can exceed the
    # legacy 40-second window when 305-station topology and several vehicles
    # initialise together. Keep the default for small scenarios, but allow a
    # bounded larger acknowledgement window for multi-round campaigns.
    call_acceptance_timeout = max(40, min(120, 10 + len(request['calls']) * 4))
    deadline = started + max(call['atSeconds'] for call in pending) + request['timeoutSeconds']
    maximum_waiting = maximum_active = maximum_pending = maximum_busy_waiting = 0
    maximum_admission_waiting = 0
    admission = runtime.settings.get('DownstreamAdmission', {})
    admission_limit = admission.get('Capacity') if admission.get('Enabled') else None
    if admission_limit is not None and (type(admission_limit) is not int or admission_limit < 1):
        raise ValueError('下游准入配置无效。')
    assignments = {}
    emitted = 0

    def event(kind, **value):
        with (evidence / 'call-queue.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(dict(time=time.time(), elapsedSeconds=time.monotonic()-started,
                                        kind=kind, **value), ensure_ascii=False) + '\n')

    while time.monotonic() < deadline:
        if runtime.abort.is_set():
            raise InterruptedError('持续积压已中止；原任务及路权保留，未发出的计划不会补发。')
        now = time.monotonic()
        for station, active in list(inflight.items()):
            if active.get('taskId') is None:
                tid = discover(station)
                if tid:
                    active['taskId'] = tid
                    event('created', stationId=station, taskId=tid, planIndex=active['call']['planIndex'])
                elif now - active['emittedAt'] > call_acceptance_timeout:
                    raise TimeoutError(
                        f'PLC呼叫已发出，正式后台{call_acceptance_timeout}秒未接单：{station}')
        for tid in list(tasks):
            if tid not in completed:
                details[tid] = runtime.api('/api/tasks/' + tid)
                tasks[tid]['vehicle'] = details[tid]['task'].get('vehicleId') or tasks[tid]['vehicle']
                if tasks[tid]['vehicle'] and tid not in assignments:
                    assignments[tid] = tasks[tid]['vehicle']
                    event('assigned', taskId=tid, vehicleId=tasks[tid]['vehicle'], stationId=tasks[tid]['station'])
                if details[tid]['task']['stage'] in ('Faulted', 'Cancelled'):
                    raise RuntimeError('积压任务出现故障或取消：' + tid)
                if details[tid]['task']['stage'] == 'Completed':
                    detail = details[tid]
                    check('积压原任务九步完成并有出区证据', len(detail['steps']) == 9 and
                          all(step['status'] == 'Completed' for step in detail['steps']) and
                          detail.get('handover', {}).get('exitPhysicallyVerified') is True, tid)
                    completed.add(tid)
                    event('completed', taskId=tid, vehicleId=tasks[tid]['vehicle'])
        observer.sample(details)
        memory = bytes.fromhex(fresh_device_state(runtime)['memory_hex'])
        for station, active in list(inflight.items()):
            if active.get('taskId') in completed and memory[offsets[station]] == 10:
                stable_since.setdefault(station, now)
                if now - stable_since[station] >= 1.5:
                    inflight.pop(station)
            else:
                stable_since.pop(station, None)
        # One PLC bit cannot represent two unacknowledged calls. Keep repeated
        # requests in an explicit plan queue until that station closes; other
        # stations continue independently, without waiting for the whole fleet.
        due, chosen = [], set()
        for call in pending:
            station = call['stationId']
            if call['atSeconds'] <= now-started and station not in inflight and station not in chosen and memory[offsets[station]] == 10:
                due.append(call); chosen.add(station)
        if due:
            sequence = uuid.uuid4().hex
            payload = dict(sequence=sequence, calls=[call['stationId'] for call in due])
            write(runtime.root / 'device-input.json', payload)
            applied = runtime.wait('积压呼叫未被虚拟PLC确认', lambda: external_input(runtime, sequence), 10)
            check('积压呼叫由虚拟PLC应用', applied.get('accepted') is True, applied)
            for call in due:
                pending.remove(call)
                inflight[call['stationId']] = dict(call=call, taskId=None, emittedAt=now)
                stable_since.pop(call['stationId'], None)
                emitted += 1
                event('emitted', **call, actualAtSeconds=time.monotonic()-started)
        waiting = sum(1 for tid, detail in details.items() if tid not in completed and not detail['task'].get('vehicleId'))
        active = sum(1 for tid, detail in details.items() if tid not in completed and detail['task'].get('vehicleId'))
        maximum_waiting = max(maximum_waiting, waiting)
        maximum_active = max(maximum_active, active)
        if active >= len(runtime.settings['Agv']['Robots']):
            maximum_busy_waiting = max(maximum_busy_waiting, waiting)
        if admission_limit is not None and active >= min(admission_limit, len(runtime.settings['Agv']['Robots'])):
            maximum_admission_waiting = max(maximum_admission_waiting, waiting)
        if admission_limit is not None and active > admission_limit:
            check('下游准入没有超卖', False, f'执行{active}单，准入上限{admission_limit}')
        maximum_pending = max(maximum_pending, sum(call['atSeconds'] <= now-started for call in pending))
        runtime.scenario.update(completedRounds=len(completed), emittedCalls=emitted, pendingCalls=len(pending),
            waitingTasks=waiting, activeTasks=active,
            message=f'计划待发{len(pending)}次；正式队列待派{waiting}单；执行{active}单；已完成{len(completed)}单。' +
                '；'.join(describe_progress(tasks[tid]['vehicle'] or '等待派车', detail)
                    for tid, detail in details.items() if tid not in completed))
        if not pending and not inflight and len(completed) == len(request['calls']):
            write(evidence / 'queue-summary.json', dict(plannedCalls=len(request['calls']), emittedCalls=emitted,
                maximumWaitingTasks=maximum_waiting, maximumActiveTasks=maximum_active,
                maximumDuePlanCalls=maximum_pending, maximumBusyWaitingTasks=maximum_busy_waiting,
                downstreamAdmissionCapacity=admission_limit, maximumAdmissionWaitingTasks=maximum_admission_waiting,
                stationPriorities=runtime.settings['TaskCreation'].get('StationPriorities', {}),
                priorityCoverage='按已保存的各工位优先级进入正式队列；全局匹配同时考虑等待时长和路线成本，记录实际派车顺序供核验。',
                orderCoverage='记录实际呼叫、建单、派车和完成顺序；全局匹配考虑路线成本，不把严格先到先派当成既定规则。'))
            check('持续计划全部经正式PLC接单完成', len(tasks) == len(request['calls']), f'{len(tasks)}个原任务')
            if admission_limit is None and len(request['calls']) > 1:
                check('忙车期间有正式等待队列', maximum_busy_waiting > 0,
                      f'全部车辆执行时最多{maximum_busy_waiting}单等待派车；若为0，请增加同时工位或减少车辆重新验证积压。')
            elif admission_limit is not None:
                check('下游准入没有超卖', maximum_active <= admission_limit, f'最大执行{maximum_active}，上限{admission_limit}')
                if len(request['calls']) > 1: check('下游准入满额时呼叫保持未取箱排队', maximum_admission_waiting > 0,
                      f'准入上限{admission_limit}时最多{maximum_admission_waiting}单等待；全车忙碌覆盖另记为{maximum_busy_waiting}，两者不混同。')
            return
        runtime.abort.wait(.5)
    raise TimeoutError('持续积压未在期限内全部闭合，请查看待发计划、正式等待链和首个阻塞原因。')
