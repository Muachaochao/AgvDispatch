"""Repeatable scenarios and independent read-only verdicts for the real scheduler."""
import hashlib
import json
import math
import re
import sqlite3
import sys
import time
import uuid
from contextlib import closing
from pathlib import Path
import simulation_clock
from urllib.error import HTTPError, URLError
from controller import read, write
from device_controls import PLC_DEFAULTS
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
from footprint import swept_rectangles_overlap, vehicle_extents

NAMES = ('business-calls', 'normal-cycle', 'simultaneous-calls', 'custom-calls', 'dispatch-backlog', 'disconnect-reconnect',
         'plc-delay', 'plc-recovery', 'service-restart', 'resume-recovery', 'vendor-cancel-return',
         'response-loss', 'road-block-recovery', 'obstacle-auto-detour',
         'obstacle-auto-safety-return',
         'obstacle-timeout-pickup', 'obstacle-timeout-fullbox', 'obstacle-timeout-return',
         'obstacle-timeout-midroad', 'obstacle-timeout-safe-site', 'obstacle-timeout-manual')
OBSTACLE_TIMEOUT_CASES = {
    'obstacle-timeout-pickup': dict(index=0, step_type='GoToPickup', full_load=False,
                                     stage='GoingToPickup'),
    'obstacle-timeout-fullbox': dict(index=2, step_type='DeliverFullBoxToDumper', full_load=True,
                                      stage='GoingToDumper'),
    'obstacle-timeout-return': dict(index=6, step_type='ReturnEmptyBox', full_load=False,
                                     stage='ReturningEmptyBox'),
    'obstacle-timeout-midroad': dict(index=0, step_type='GoToPickup', full_load=False,
                                      stage='GoingToPickup'),
    'obstacle-timeout-safe-site': dict(index=2, step_type='DeliverFullBoxToDumper', full_load=True,
                                        stage='GoingToDumper'),
    'obstacle-timeout-manual': dict(index=0, step_type='GoToPickup', full_load=False,
                                     stage='GoingToPickup'),
}
TIMEOUT_OBSTACLE_NAMES = tuple(OBSTACLE_TIMEOUT_CASES)
STEP_NAMES = {'GoToPickup': '前往工位取满箱', 'PickupFullBox': '确认满箱已取走',
              'DeliverFullBoxToDumper': '运送满箱到等待位', 'WaitForDumperReady': '等待倒料机就绪',
              'EnterDumpingPosition': '进入倒料位', 'DumpFullBox': '等待倒料完成',
              'ReturnEmptyBox': '返送空箱到原工位', 'PlaceEmptyBox': '确认空箱已放到位',
              'CompletePlcHandshake': '完成工位握手收尾'}
TASK_STAGE_NAMES = {
    0: 'Created', 10: 'WaitingForVehicle', 15: 'SubmittingToAgv', 20: 'GoingToPickup',
    30: 'PickingUp', 40: 'GoingToDumper', 50: 'WaitingForDumper', 60: 'Dumping',
    70: 'ReturningEmptyBox', 80: 'WaitingForPlcConfirmation', 90: 'Completed',
    100: 'Faulted', 110: 'Cancelled', 120: 'CancellationPending', 130: 'CancellationReview',
}
STEP_TYPE_NAMES = {
    10: 'GoToPickup', 20: 'PickupFullBox', 30: 'DeliverFullBoxToDumper',
    40: 'WaitForDumperReady', 50: 'EnterDumpingPosition', 60: 'DumpFullBox',
    70: 'ReturnEmptyBox', 80: 'PlaceEmptyBox', 90: 'CompletePlcHandshake',
}
STEP_STATUS_NAMES = {
    0: 'Pending', 10: 'Ready', 20: 'CommandPending', 30: 'Sending', 40: 'Executing',
    50: 'Completed', 60: 'RetryScheduled', 90: 'Failed', 100: 'Cancelled',
    110: 'ManualIntervention',
}


def has_alternate_connection(navigation, edge_id):
    edge = next((item for item in navigation.get('edges', [])
                 if item.get('id') == edge_id and item.get('enabled', True)), None)
    if edge is None:
        return False
    adjacency = {}
    for item in navigation.get('edges', []):
        if item.get('id') == edge_id or not item.get('enabled', True):
            continue
        source, target = item.get('from'), item.get('to')
        adjacency.setdefault(source, set()).add(target)
        if item.get('bidirectional', True):
            adjacency.setdefault(target, set()).add(source)
    pending, seen = [edge.get('from')], {edge.get('from')}
    while pending:
        current = pending.pop()
        for target in adjacency.get(current, ()):
            if target == edge.get('to'):
                return True
            if target not in seen:
                seen.add(target)
                pending.append(target)
    return False


def obstacle_detour_observation_deadline(settings, hold_seconds):
    """Test budget, not permission to cancel or move; honour the backend gate."""
    timing = settings.get('ObstacleRecovery') or {}
    return max(hold_seconds + 20, timing.get('CancelAfterSeconds', 120) +
               timing.get('CancelTerminalWaitWarningSeconds', 30) + 30)


def obstacle_detour_send_observed(recovery):
    """A blocked test may clear only after the replacement command exists.

    DetourReady means planning finished; it does not prove that the original
    vendor chain was terminal or that a second navigation was sent.
    """
    return bool(recovery and recovery.get('newVendorTaskId') and
                recovery.get('state') in ('Executing', 'Recovered'))


def describe_progress(vehicle, detail):
    steps = detail.get('steps', [])
    if detail['task']['stage'] == 'Completed':
        return f'{vehicle}：任务已完成，握手已结束'
    current = next((s for s in steps if s.get('isCurrent')), None)
    if current is None:
        current = next((s for s in steps if s.get('status') in ('Ready', 'Executing', 'WaitingForTraffic')), None)
    if current is None:
        return f'{vehicle}：等待派车或前序条件'
    number = current.get('sequence') or steps.index(current) + 1
    label = STEP_NAMES.get(current.get('stepType'), '等待任务反馈')
    reason = current.get('waitingExplanation') or detail.get('waitingExplanation')
    return f'{vehicle}：第{number}/{len(steps)}步，{label}' + (f'；{reason}' if reason else '')


def summarize_tasks(details):
    """Return a bounded bilingual-safe task record for the report and UI."""
    result = []
    for task_id, detail in sorted(details.items()) if isinstance(details, dict) else []:
        if not isinstance(detail, dict):
            continue
        task = detail.get('task') if isinstance(detail.get('task'), dict) else {}
        steps = detail.get('steps') if isinstance(detail.get('steps'), list) else []
        current = next((step for step in steps if step.get('isCurrent')), None)
        stage = task.get('stage')
        if stage in ('Completed', 'Cancelled'):
            current = None
        fallback_name = {'Completed': '已完成', 'Cancelled': '已取消', 'Faulted': '任务故障',
                         'WaitingForVehicle': '等待派车'}.get(stage, '等待任务反馈')
        steps_out = []
        for index, step in enumerate(steps):
            if not isinstance(step, dict):
                continue
            number = step.get('sequence') or index + 1
            steps_out.append(dict(number=number, stepType=step.get('stepType'),
                                  stepName=STEP_NAMES.get(step.get('stepType'), '等待任务反馈'),
                                  status=step.get('status'), waitingExplanation=step.get('waitingExplanation')))
        result.append(dict(
            taskId=task_id, stationId=task.get('stationId'), vehicleId=task.get('vehicleId'),
            stage=task.get('stage'), currentStepNumber=current.get('sequence') if current else None,
            currentStepType=current.get('stepType') if current else None,
            currentStepName=STEP_NAMES.get(current.get('stepType'), fallback_name) if current else fallback_name,
            currentStepStatus=current.get('status') if current else None,
            waitingExplanation=detail.get('waitingExplanation') or (current or {}).get('waitingExplanation'),
            completed=task.get('stage') == 'Completed', steps=steps_out))
    return result


def active_step_wait_seconds(details, starts, now):
    longest = 0
    for tid, detail in details.items():
        if detail.get('task', {}).get('stage') in ('Completed', 'Cancelled'):
            starts.pop(tid, None)
            continue
        signature = tuple(step['status'] for step in detail['steps'])
        prior = starts.get(tid)
        if not prior or prior[0] != signature:
            starts[tid] = (signature, now)
        longest = max(longest, now - starts[tid][1])
    return longest


def submit_original_task(runtime, payload, evidence):
    # Save the identity before sending. A lost HTTP receipt is not evidence that
    # no task was created; repeat only the identical idempotent request.
    intent = dict(request=payload, attempts=[])
    path = evidence / ('create-intent-' + payload['requestId'] + '.json')
    write(path, intent)
    for attempt in range(3):
        try:
            response = runtime.api('/api/tasks', payload)
            intent['response'] = response
            write(path, intent)
            return response
        except (TimeoutError, URLError) as error:
            transient = not isinstance(error, HTTPError) or error.code in (502, 503, 504)
            intent['attempts'].append(dict(attempt=attempt + 1, error=str(error)))
            write(path, intent)
            if not transient or attempt == 2: raise
            if runtime.abort.wait(.5): raise InterruptedError('场景已中止，请按原请求编号核对建单结果。')


def validate_scenario(value):
    if set(value) - {'name', 'rounds', 'intervalSeconds', 'timeoutSeconds', 'calls', 'plcFault', 'holdSeconds', 'restartStage'}:
        raise ValueError('未知场景参数。')
    result = dict(name=value.get('name', 'normal-cycle'), rounds=value.get('rounds', 1),
                  intervalSeconds=value.get('intervalSeconds', 0), timeoutSeconds=value.get('timeoutSeconds', 720))
    if result['name'] not in NAMES: raise ValueError('场景不存在。')
    if type(result['rounds']) is not int or not 1 <= result['rounds'] <= 100: raise ValueError('轮次须为1至100。')
    # Full-factory campaigns can queue 305 stations across five vehicles and
    # therefore need a bounded multi-day observation window.
    for key, low, high in [('intervalSeconds', 0, 60), ('timeoutSeconds', 30, 604800)]:
        number = result[key]
        if type(number) not in (int, float) or not math.isfinite(number) or not low <= number <= high: raise ValueError('场景时间参数无效。')
    if result['name'] == 'service-restart' and result['rounds'] != 1: raise ValueError('重启故障场景仅运行一轮，保留恢复现场。')
    if result['name'] == 'vendor-cancel-return' and result['rounds'] != 1:
        raise ValueError('车端返程取消场景仅运行一轮，保留取消现场。')
    if result['name'] in ('response-loss', 'road-block-recovery', 'obstacle-auto-detour',
                          'obstacle-auto-safety-return',
                          *TIMEOUT_OBSTACLE_NAMES) and result['rounds'] != 1:
        raise ValueError('单车通信或道路故障场景仅运行一轮。')
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
    elif result['name'] in ('road-block-recovery', 'obstacle-auto-detour',
                            'obstacle-auto-safety-return', *TIMEOUT_OBSTACLE_NAMES):
        hold = value.get('holdSeconds', 5)
        if type(hold) not in (int, float) or not math.isfinite(hold) or not 1 <= hold <= 120:
            raise ValueError('故障保持或车端超时触发时间须为1至120秒。')
        result['holdSeconds'] = float(hold)
    calls = value.get('calls')
    if result['name'] in ('custom-calls', 'dispatch-backlog', 'business-calls'):
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
            if not isinstance(station, str) or not re.fullmatch(r'CNC-\d{3}', station) or not 1 <= int(station[4:]) <= 305:
                raise ValueError('呼叫工位编号必须是已配置的CNC-001至CNC-305。')
            if type(at) not in (int, float) or not math.isfinite(at) or not 0 <= at <= 86400:
                raise ValueError('呼叫时间须为0至86400秒。')
            if vehicle is not None and (not isinstance(vehicle, str) or not vehicle.strip()):
                raise ValueError('指定车辆编号无效。')
            if vehicle is not None and result['name'] in ('dispatch-backlog', 'business-calls'):
                raise ValueError('持续积压场景只产生PLC呼叫，不能指定车辆。')
            key = round(float(at), 6)
            if station in simultaneous.setdefault(key, set()) and result['name'] not in ('dispatch-backlog', 'business-calls'):
                raise ValueError('同一时刻不能重复呼叫同一工位。')
            simultaneous[key].add(station)
            normalized.append(dict(stationId=station, atSeconds=float(at), vehicleId=vehicle))
        result['calls'] = normalized
    elif calls is not None:
        raise ValueError('仅自定义呼叫和持续积压场景接受calls参数。')
    if result['name'] != 'plc-recovery' and 'plcFault' in value:
        raise ValueError('仅PLC卡位恢复场景接受plcFault。')
    if result['name'] not in ('plc-recovery', 'road-block-recovery', 'obstacle-auto-detour',
                              'obstacle-auto-safety-return',
                              *TIMEOUT_OBSTACLE_NAMES) and 'holdSeconds' in value:
        raise ValueError('仅PLC卡位、道路阻塞、车端障碍或超时恢复场景接受holdSeconds。')
    if result['name'] != 'service-restart' and 'restartStage' in value:
        raise ValueError('仅后台重启场景接受restartStage。')
    return result


def query(root, sql, parameters=()):
    with closing(sqlite3.connect((root / 'data/agv-dispatch.db').resolve().as_uri() + '?mode=ro', uri=True, timeout=3)) as db:
        db.row_factory = sqlite3.Row
        return [dict(row) for row in db.execute(sql, parameters)]


def charging_session_ids(root):
    """Active automatic-charging sessions. Their bay and route leases are owned by the formal charging
    workflow (TaskId == SessionId), not left over by a business task."""
    path = root / 'data/charging-sessions.json'
    try:
        rows = json.loads(path.read_text(encoding='utf-8-sig')) if path.exists() else []
    except (OSError, ValueError):
        return set()
    return {row['SessionId'] for row in rows if row.get('Phase') not in (13, 'Completed') and row.get('SessionId')}


def business_leases(root):
    """Traffic leases excluding those held by active charging sessions."""
    charging = charging_session_ids(root)
    if not charging:
        return query(root, 'select ResourceId from traffic_resource_lease')
    return [row for row in query(root, 'select ResourceId,TaskId from traffic_resource_lease') if row['TaskId'] not in charging]


def separation(a0, a1, b0, b1):
    """Minimum separation for linearly interpolated paired samples, no model feedback."""
    dx, dy = a0['x'] - b0['x'], a0['y'] - b0['y']
    vx, vy = a1['x'] - b1['x'] - dx, a1['y'] - b1['y'] - dy
    t = max(0, min(1, -(dx * vx + dy * vy) / (vx * vx + vy * vy))) if vx * vx + vy * vy else 0
    return math.hypot(dx + vx * t, dy + vy * t)


def control_applied(state, controls, vehicle=None, now=None):
    """A written input is not a device acknowledgement. Match the applied sequence and fields."""
    current = time.time() if now is None else now
    if current - state.get('time', 0) > 3:
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


def fresh_device_state(runtime, timeout=5):
    clock = getattr(runtime, 'clock', None)
    monotonic = clock.monotonic if clock else time.monotonic
    time_now = clock.time if clock else time.time
    wait_business = clock.business_delay if clock else lambda value: value
    deadline = monotonic() + timeout
    while True:
        if runtime.abort.is_set():
            raise InterruptedError('场景已中止，停止等待设备反馈。')
        if any(child.poll() is not None for child in runtime.children.values()):
            raise RuntimeError('仿真子进程已退出，不能继续判定。')
        state = runtime.device()
        stamp = state.get('time', 0)
        if type(stamp) in (int, float) and math.isfinite(stamp) and 0 <= time_now() - stamp <= timeout:
            return state
        if monotonic() >= deadline:
            raise RuntimeError('设备快照过期，不能继续判定。')
        runtime.abort.wait(wait_business(.1))


def external_input(runtime, sequence):
    observed = fresh_device_state(runtime).get('externalInput', {})
    return observed if observed.get('sequence') == sequence else None


def mutual_wait_pairs(cars):
    """Return unresolved reciprocal blockers; one-way queues are not failures."""
    blocked = {vehicle: car.get('blockedByVehicle') for vehicle, car in cars.items()
               if car.get('blockedByVehicle')}
    return sorted(tuple(sorted((vehicle, blocker))) for vehicle, blocker in blocked.items()
                  if blocked.get(blocker) == vehicle and vehicle < blocker)


def complete_command_set(commands, tasks, recoveries=None):
    """Validate four business steps, including every committed rolling window."""
    if not commands or len({c['CommandId'] for c in commands}) != len(commands):
        return False

    recovered = {}
    recovery_ids = set()
    for recovery in recoveries or []:
        key = (recovery.get('TaskId'), recovery.get('StepId'))
        vendor = recovery.get('NewVendorTaskId')
        if (key in recovered or key[0] not in tasks or recovery.get('State') != 'Completed' or
                not recovery.get('SendAttemptedAt') or not isinstance(vendor, str) or
                not vendor.startswith('TIMEOUTRECOVERY-') or vendor in recovery_ids or
                recovery.get('VehicleId') != tasks[key[0]].get('vehicle') or
                not any(c.get('TaskId') == key[0] and c.get('StepId') == key[1] and
                        c.get('VendorCommandId') == recovery.get('OriginalVendorTaskId') for c in commands)):
            return False
        recovered[key] = recovery
        recovery_ids.add(vendor)

    def payload(row):
        raw = row.get('PayloadJson')
        if not raw:
            return None
        try:
            value = json.loads(raw)
        except (TypeError, ValueError):
            return False
        return value if isinstance(value, dict) else False

    def one_step_chain(rows, step_id, task_id):
        if any(row.get('Status') != 30 or row.get('AttemptCount') != 1 for row in rows):
            return False
        decoded = [payload(row) for row in rows]
        if any(value is False for value in decoded):
            return False
        if all(value is None for value in decoded):
            # Legacy command rows had one fixed command per business step.
            return len(rows) == 1
        if any(value is None for value in decoded):
            return False

        indexes = []
        issued_plans = []
        for row, value in zip(rows, decoded):
            business_step = value.get('businessStepId') or value.get('businessTaskId')
            index = value.get('windowIndex', 0)
            count = value.get('windowCount', 1)
            windows = value.get('windows') or []
            if (business_step != step_id or type(index) is not int or
                    type(count) is not int or count < 1 or not 0 <= index < count or
                    (count > 1 and not isinstance(windows, list))):
                return False
            indexes.append(index)
            expected_id = step_id if count == 1 else f'{step_id}-W{index + 1:02d}'
            expected_previous = None if index == 0 else f'{step_id}-W{index:02d}'
            if (value.get('businessTaskId') != expected_id or
                    row.get('VendorCommandId') != expected_id or
                    (value.get('previousVendorTaskId') or None) != expected_previous):
                return False
            if windows:
                ordered = sorted(windows, key=lambda item: item.get('index', -1))
                if (len(ordered) != count or
                        [item.get('index') for item in ordered] != list(range(count)) or
                        ordered[-1].get('final') is not True or
                        any(item.get('final') is not False for item in ordered[:-1])):
                    return False
                current = ordered[index]
                if (value.get('source') != current.get('source') or
                        value.get('destination') != current.get('destination') or
                        value.get('path') != current.get('path')):
                    return False
                nodes = []
                for window in ordered:
                    path = window.get('path') or []
                    if (not isinstance(path, list) or not path or
                            path[0] != window.get('source') or
                            path[-1] != window.get('destination') or
                            nodes and path[0] != nodes[-1]):
                        return False
                    nodes.extend(path if not nodes else path[1:])
                issued_plans.append((index, ordered, current))
            else:
                issued_plans.append((index, None, None))
        recovery = recovered.get((task_id, step_id))
        if sorted(indexes) != list(range(len(rows))):
            return False
        if recovery:
            last_row = rows[indexes.index(max(indexes))]
            if last_row.get('VendorCommandId') != recovery.get('OriginalVendorTaskId'):
                return False
        elif not issued_plans or issued_plans[-1][2].get('final') is not True:
            return False
        previous = None
        for index, planned_windows, current in issued_plans:
            if previous is not None and planned_windows is not None:
                previous_index, previous_windows, previous_current = previous
                if previous_windows is None or previous_current is None:
                    return False
                if (index != previous_index + 1 or
                        planned_windows[:previous_index + 1] !=
                        previous_windows[:previous_index + 1] or
                        current.get('source') != previous_current.get('destination')):
                    return False
            previous = (index, planned_windows, current)
        return True

    for task in tasks:
        rows = [c for c in commands if c['TaskId'] == task]
        step_ids = {row.get('StepId') for row in rows}
        if len(step_ids) != 4:
            return False
        if any(not one_step_chain([row for row in rows if row.get('StepId') == step_id], step_id, task)
               for step_id in step_ids):
            return False
    return True


def leases_by_resource(leases):
    holders = {}
    for lease in leases:
        holders.setdefault(lease['ResourceId'], []).append(lease)
    return holders


def resource_wait_cycles(requests, leases, ownership, capacities, cars, *, holders=None):
    """Only durable assigned tasks and currently held, saturated leases count.

    A dispatch candidate is not the owner of its suggested vehicle. Historical
    BlockingKey text is deliberately ignored because leases may already have
    changed. Moving vehicles are excluded from persistent stopped wait cycles.
    """
    assigned = {r['TaskId']: r['VehicleId'] for r in ownership}
    if holders is None:
        holders = leases_by_resource(leases)
    dependencies = {}
    for request in requests:
        task, vehicle = request.get('TaskId'), request.get('VehicleId')
        if request.get('State') != 'Waiting' or not vehicle or assigned.get(task) != vehicle:
            continue
        if vehicle not in cars or cars[vehicle].get('moving') is not False:
            continue
        try:
            resource_ids = json.loads(request.get('ResourceIds') or '[]')
        except (ValueError, TypeError):
            continue
        for resource in resource_ids:
            resource_leases = holders.get(resource, ())
            tasks = {lease['TaskId'] for lease in resource_leases}
            if task in tasks or len(tasks) < capacities.get(resource, 1):
                continue
            owners = {lease['VehicleId'] for lease in resource_leases
                      if lease.get('VehicleId') and lease['VehicleId'] != vehicle}
            if owners:
                dependencies.setdefault(vehicle, []).append(owners)
    # A running vendor navigation can be physically stopped by a parked car
    # that is itself waiting on this navigation's leases (mixed wait cycle).
    assigned_vehicles = set(assigned.values())
    for vehicle, car in cars.items():
        blocker = car.get('blockedByVehicle')
        if (vehicle in assigned_vehicles and car.get('moving') is False
                and blocker in cars and blocker != vehicle):
            dependencies.setdefault(vehicle, []).append({blocker})
    # Capacity > 1 is an AND dependency: one progressing outside owner can free
    # a slot. A simple pairwise cycle alone is not proof of a closed wait set.
    stuck = set(dependencies)
    while True:
        reduced = {v for v in stuck if any(owners <= stuck for owners in dependencies[v])}
        if reduced == stuck:
            break
        stuck = reduced
    graph = {v: set().union(*(owners for owners in dependencies[v] if owners <= stuck)) for v in stuck}
    cycles = set()
    def walk(start, current, path):
        for target in graph.get(current, ()):
            if target == start and len(path) > 1:
                cycles.add(tuple(sorted(path)))
            elif target not in path and target in graph:
                walk(start, target, (*path, target))
    for start in graph:
        walk(start, start, (start,))
    return cycles


class Observer:
    def __init__(self, runtime, evidence):
        self.runtime, self.evidence = runtime, evidence
        self.previous = None
        self.ownership_samples = 0
        self.samples = 0
        self.minimum = None
        self.violations = []
        self.max_wait = 0
        self.wait_started = {}
        self.mutual_wait_started = {}
        self.resource_cycle_started = {}
        self.overlap_pairs = set()
        self.last_details = {}
        self.seen_details = {}
        self.resources = {r['id']: r.get('capacity', 1) for r in read(runtime.root / 'navigation.agvmap.json')['resources']}
        self.footprints = {
            p['VehicleId']: vehicle_extents(p)
            for p in runtime.settings['HandshakeExecution']['DumperExit']['BodyProfiles']}

    def sample(self, details, compact=False):
        self.last_details = details
        self.seen_details.update(details)
        state = fresh_device_state(self.runtime)
        ownership = query(self.runtime.root, 'select VehicleId,TaskId from dispatch_task where VehicleId is not null and VehicleId != \'\' and Stage not in (90,110)')
        by_vehicle = {}
        for row in ownership:
            by_vehicle.setdefault(row['VehicleId'], set()).add(row['TaskId'])
        for vehicle, task_ids in by_vehicle.items():
            if len(task_ids) > 1:
                self.violations.append(dict(kind='vehicle-multiple-tasks', vehicle=vehicle, tasks=sorted(task_ids)))
        self.ownership_samples += 1
        leases = query(self.runtime.root, 'select ResourceId,TaskId,VehicleId,OwnerStepId from traffic_resource_lease')
        holders = leases_by_resource(leases)
        for resource, resource_leases in holders.items():
            owners = {lease['TaskId'] for lease in resource_leases}
            if len(owners) > self.resources.get(resource, 1):
                self.violations.append(dict(kind='capacity', resource=resource, owners=sorted(owners)))
        cars = state['cars']
        ids = sorted(cars)
        for i, first in enumerate(ids):
            for second in ids[i + 1:]:
                if first not in self.footprints or second not in self.footprints:
                    raise RuntimeError('缺少合成车体长宽，无法判定碰撞。')
                previous = self.previous or state
                distance = separation(previous['cars'][first], cars[first], previous['cars'][second], cars[second])
                self.minimum = distance if self.minimum is None else min(self.minimum, distance)
                first_from = dict(previous['cars'][first], lengthMeters=self.footprints[first][0],
                                  widthMeters=self.footprints[first][1])
                first_to = dict(cars[first], lengthMeters=self.footprints[first][0],
                                widthMeters=self.footprints[first][1])
                second_from = dict(previous['cars'][second], lengthMeters=self.footprints[second][0],
                                   widthMeters=self.footprints[second][1])
                second_to = dict(cars[second], lengthMeters=self.footprints[second][0],
                                 widthMeters=self.footprints[second][1])
                pair = (first, second)
                if pair not in self.overlap_pairs and swept_rectangles_overlap(
                        first_from, first_to, second_from, second_to, margin=.05):
                    self.overlap_pairs.add(pair)
                    self.violations.append(dict(kind='envelope-overlap', vehicles=[first, second],
                        distance=distance, dimensions={first: self.footprints[first], second: self.footprints[second]},
                        marginMeters=.05))
        clock = getattr(self.runtime, 'clock', None)
        now = clock.monotonic() if clock else time.monotonic()
        current_pairs = set(mutual_wait_pairs(cars))
        requests = query(self.runtime.root,
                         "select TaskId,VehicleId,State,ResourceIds from traffic_request where State='Waiting'")
        cycles = resource_wait_cycles(requests, leases, ownership, self.resources, cars, holders=holders)
        for cycle in tuple(self.resource_cycle_started):
            if cycle not in cycles:
                self.resource_cycle_started.pop(cycle)
        for cycle in cycles:
            started = self.resource_cycle_started.setdefault(cycle, now)
            if now - started >= 45:
                self.violations.append(dict(kind='traffic-resource-cycle', vehicles=list(cycle),
                    seconds=round(now-started, 1), tasks=sorted(
                        r['TaskId'] for r in ownership if r['VehicleId'] in cycle),
                    sites={v: cars[v].get('station') for v in cycle}))
        for pair in tuple(self.mutual_wait_started):
            if pair not in current_pairs: self.mutual_wait_started.pop(pair)
        for pair in current_pairs:
            started = self.mutual_wait_started.setdefault(pair, now)
            if now - started >= 45:
                owners = [row for row in ownership if row['VehicleId'] in pair]
                detail = dict(kind='mutual-vehicle-wait', vehicles=list(pair), seconds=round(now-started, 1),
                              tasks=sorted(row['TaskId'] for row in owners),
                              sites={vehicle: cars[vehicle].get('station') for vehicle in pair})
                if not any(item.get('kind') == 'mutual-vehicle-wait' and item.get('vehicles') == list(pair)
                           for item in self.violations):
                    self.violations.append(detail)
        self.max_wait = max(self.max_wait, active_step_wait_seconds(details, self.wait_started, now))
        self.samples += 1; self.previous = state
        task_observation = ({
            'total': len(details),
            'stageCounts': {
                stage: sum(1 for detail in details.values() if detail['task']['stage'] == stage)
                for stage in sorted({detail['task']['stage'] for detail in details.values()})
            },
            'activeVehicles': sorted({
                detail['task'].get('vehicleId') for detail in details.values()
                if detail['task'].get('vehicleId') and detail['task'].get('stage') not in ('Completed', 'Cancelled')
            }),
            'sampleMode': 'bounded-large-batch',
        } if compact else {
            t: dict(
                stage=d['task']['stage'],
                stationId=d['task'].get('stationId'),
                vehicleId=d['task'].get('vehicleId'),
                progress=describe_progress(d['task'].get('vehicleId') or '待派车', d),
                waitingExplanation=d.get('waitingExplanation'),
                steps=[s['status'] for s in d['steps']],
            )
            for t, d in details.items()
        })
        with (self.evidence / 'observations.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(dict(
                time=clock.time() if clock else time.time(),
                device=state,
                leases=leases,
                trafficWaitRequests=requests,
                vehicleTaskOwnership=ownership,
                tasks=task_observation,
            ), ensure_ascii=False) + '\n')
        mutual = next((item for item in self.violations if item.get('kind') in
                       ('mutual-vehicle-wait', 'traffic-resource-cycle')), None)
        if mutual:
            raise RuntimeError(f"互锁等待：{'、'.join(mutual['vehicles'])} 连续45秒形成车辆或路权等待环；"
                               f"任务 {', '.join(mutual['tasks']) or '未绑定'}。先检查共享路口路权和候选路线，"
                               "原任务及路权已保留，禁止删除任务或人工改信号绕过。")
        if self.violations: raise RuntimeError('独立交通检查发现资源超容量或合成包络重叠。')
        return leases


def execute(runtime, request):
    if not hasattr(runtime, 'clock'):
        runtime.clock = simulation_clock.SimulationClock(1, time.time())
    if not hasattr(runtime, 'simulation_rate'):
        runtime.simulation_rate = runtime.clock.rate
    started = runtime.clock.time()
    wall_started = time.monotonic()
    evidence = runtime.root / ('simulation-run-' + runtime.scenario['id'])
    evidence.mkdir(exist_ok=False)
    write(evidence / 'scenario.json', dict(**request, startedAt=started,
        simulationRate=runtime.simulation_rate, rootName=runtime.root.name))
    if (runtime.root / 'runtime-release.json').is_file():
        write(evidence / 'runtime-release.json', read(runtime.root / 'runtime-release.json'))
    if (runtime.root / 'navigation.agvmap.json').is_file():
        write(evidence / 'map.json', read(runtime.root / 'navigation.agvmap.json'))
    simulation_configuration = getattr(runtime, 'lab', {}).get('simulationConfiguration') or {}
    plc_signal_table = getattr(runtime, 'lab', {}).get('plcSignalTable')
    if plc_signal_table:
        write(evidence / 'plc-signal-table.json', plc_signal_table)
        source_table = runtime.root / 'plc-signals.source.csv'
        if source_table.is_file() and not source_table.is_symlink():
            (evidence / 'plc-signals.source.csv').write_bytes(source_table.read_bytes())
    write(evidence / 'simulation-configuration.json', dict(
        mapIdentity=simulation_configuration.get('mapIdentity'),
        downstreamAdmissionCapacity=simulation_configuration.get('downstreamAdmissionCapacity', 0),
        plcSignalTable=plc_signal_table,
        stations=simulation_configuration.get('stations'),
        dumper=simulation_configuration.get('dumper'),
        vehicles=simulation_configuration.get('vehicles')))
    execution_configuration = {key: runtime.settings.get(key) for key in
                               ('TaskCreation', 'Dispatching', 'DispatchOptimization', 'TrafficSegmentRelease', 'DownstreamAdmission')}
    execution_configuration['plcSignalTable'] = plc_signal_table
    write(evidence / 'execution-configuration.json', execution_configuration)
    checks = []
    tasks = {}
    observer = None
    fault_observed = False
    auto_detour_sends = 0
    plc_fault_controls = None
    recovery_hold = False
    completed_tasks = set()
    result_status = 'failed'
    message = ''
    version_start = None
    automatic = runtime.settings.get('TaskCreation', {}).get('AutomaticCreationEnabled') is True
    baseline = set()
    def discover(station):
        rows = [row for row in query(runtime.root,
            'select TaskId,StationId,VehicleId,RequestedVehicleId,CreateRequestId from dispatch_task where StationId=? order by CreatedAt', (station,))
            if row['TaskId'] not in baseline and row['TaskId'] not in tasks]
        if len(rows) > 1:
            raise RuntimeError('一次工位呼叫出现多个新任务，保留现场核查：' + station)
        if not rows: return None
        row = rows[0]
        if row.get('RequestedVehicleId') or row.get('CreateRequestId'):
            raise RuntimeError('呼叫未经过正式PLC自动建单通道：' + station)
        tid = row['TaskId']; tasks[tid] = dict(vehicle=row.get('VehicleId'), station=station)
        write(evidence / 'task-identities.json', tasks)
        return tid
    def check(name, passed, detail):
        checks.append(dict(name=name, status='passed' if passed else 'failed', detail=detail))
        if not passed: raise RuntimeError(name + '：' + str(detail))
    def control_applied_now(state, controls, vehicle=None):
        return control_applied(state, controls, vehicle, now=runtime.clock.time())
    def create(station, vehicle, trigger=True):
        if trigger:
            write(runtime.root / 'device-input.json', dict(sequence=uuid.uuid4().hex, call=station))
        if automatic:
            return runtime.wait('正式后台未从PLC呼叫创建原任务：' + station, lambda: discover(station), 40)
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
        response = submit_original_task(runtime, payload, evidence)
        tid = response['taskId']; tasks[tid] = dict(vehicle=requested, station=station)
        write(evidence / 'task-identities.json', tasks)
        write(evidence / ('create-' + tid + '.json'), dict(request=payload, response=response))
        return tid
    def collect_batch(stations):
        """Collect one simultaneous PLC batch without timing out stations serially.

        The production coordinator persists and acknowledges calls one by one.
        A large station batch therefore needs one shared deadline; waiting 40
        seconds inside each station would reject later stations even though
        their PLC calls were valid and still queued.
        """
        ordered = list(dict.fromkeys(stations))
        found = {}
        # A 305-station wave is acknowledged by the production PLC coordinator
        # sequentially. The previous 1.5 seconds per station allowance expired
        # while the coordinator was still safely persisting valid calls. Keep
        # the shared deadline capacity-based, while a separate no-progress
        # guard still fails quickly if the queue truly stops advancing.
        timeout = max(120.0, min(float(request['timeoutSeconds']), 120.0 + len(ordered) * 3.0))
        stall_timeout = 120.0 if len(ordered) > 32 else 60.0
        deadline = runtime.clock.monotonic() + timeout
        last_progress = runtime.clock.monotonic()
        last_count = -1
        progress_path = evidence / 'batch-creation-progress.jsonl'
        while runtime.clock.monotonic() < deadline:
            if runtime.abort.is_set():
                raise InterruptedError('批量呼叫建单已中止；已经建出的原任务保留。')
            placeholders = ','.join('?' for _ in ordered)
            rows = query(
                runtime.root,
                'select TaskId,StationId,VehicleId,RequestedVehicleId,CreateRequestId '
                f'from dispatch_task where StationId in ({placeholders}) order by CreatedAt',
                ordered,
            )
            new_rows = [
                row for row in rows
                if row['TaskId'] not in baseline
                and row['TaskId'] not in tasks
                and row['StationId'] not in found
            ]
            by_station = {}
            for row in new_rows:
                by_station.setdefault(row['StationId'], []).append(row)
            duplicates = [station for station, station_rows in by_station.items() if len(station_rows) > 1]
            if duplicates:
                raise RuntimeError('一次工位呼叫出现多个新任务，保留现场核查：' + duplicates[0])
            for station, station_rows in by_station.items():
                row = station_rows[0]
                if row.get('RequestedVehicleId') or row.get('CreateRequestId'):
                    raise RuntimeError('呼叫未经过正式PLC自动建单通道：' + station)
                task_id = row['TaskId']
                tasks[task_id] = dict(vehicle=row.get('VehicleId'), station=station)
                found[station] = task_id
            if by_station:
                write(evidence / 'task-identities.json', tasks)
            if len(found) != last_count:
                last_count = len(found)
                last_progress = runtime.clock.monotonic()
                runtime.scenario['message'] = f'同时呼叫 {len(ordered)} 个工位；正式后台已建单 {len(found)} 个，等待 {len(ordered) - len(found)} 个。'
                with progress_path.open('a', encoding='utf-8') as stream:
                    stream.write(json.dumps(
                        dict(
                            elapsedSeconds=timeout - max(0.0, deadline - runtime.clock.monotonic()),
                            createdCount=len(found),
                            requestedCount=len(ordered),
                            firstMissing=next((station for station in ordered if station not in found), None),
                        ),
                        ensure_ascii=False,
                    ) + '\n')
            if len(found) == len(ordered):
                write(evidence / 'batch-creation.json', dict(
                    requestedStations=ordered, createdStations=list(found),
                    elapsedSeconds=timeout - max(0.0, deadline - runtime.clock.monotonic()),
                    waitTimeoutSeconds=timeout))
                return [found[station] for station in ordered]
            # If no new task appears for the bounded stall window, report the exact stations
            # instead of waiting until the global scenario deadline.
            if runtime.clock.monotonic() - last_progress >= stall_timeout:
                break
            runtime.abort.wait(.25)
        missing = [station for station in ordered if station not in found]
        write(evidence / 'batch-creation-timeout.json', dict(
            requestedStations=ordered, createdStations=list(found), missingStations=missing,
            createdCount=len(found), requestedCount=len(ordered), waitTimeoutSeconds=timeout))
        raise TimeoutError(
            f'同时呼叫批次未在 {timeout:.0f} 秒内全部建单：'
            f'已建 {len(found)}/{len(ordered)}，首个未建工位 {missing[0] if missing else "无"}。')
    def complete(ids):
        nonlocal fault_observed, recovery_hold, auto_detour_sends
        deadline = runtime.clock.monotonic() + request['timeoutSeconds']
        large_batch = request['name'] in ('custom-calls', 'dispatch-backlog', 'business-calls') and len(ids) > 32

        def batch_task_details():
            placeholders = ','.join('?' for _ in ids)
            task_rows = query(
                runtime.root,
                'select TaskId,StationId,VehicleId,Stage,CreatedAt,UpdatedAt,ErrorMessage '
                f'from dispatch_task where TaskId in ({placeholders})',
                ids,
            )
            step_rows = query(
                runtime.root,
                'select StepId,TaskId,Sequence,StepType,Status,SourceSite,DestinationSite,VehicleId,'
                'VendorTaskId,ErrorMessage,UpdatedAt '
                f'from dispatch_task_step where TaskId in ({placeholders}) order by TaskId,Sequence',
                ids,
            )
            completed_exit_ids = {
                row['TaskId'] for row in query(
                    runtime.root,
                    'select distinct TaskId from task_runtime_event '
                    f'where Kind=? and TaskId in ({placeholders})',
                    ('ExitVerified', *ids),
                )
            }
            steps_by_task = {}
            for row in step_rows:
                steps_by_task.setdefault(row['TaskId'], []).append(dict(
                    stepId=row['StepId'],
                    sequence=row['Sequence'],
                    stepType=STEP_TYPE_NAMES.get(row['StepType'], str(row['StepType'])),
                    status=STEP_STATUS_NAMES.get(row['Status'], str(row['Status'])),
                    sourceSite=row.get('SourceSite'),
                    destinationSite=row.get('DestinationSite'),
                    vehicleId=row.get('VehicleId'),
                    vendorTaskId=row.get('VendorTaskId'),
                    errorMessage=row.get('ErrorMessage'),
                    updatedAt=row.get('UpdatedAt'),
                ))
            for task_steps in steps_by_task.values():
                task_steps.sort(key=lambda step: step['sequence'])
                current = next(
                    (
                        step for step in task_steps
                        if step['status'] in (
                            'Ready', 'CommandPending', 'Sending', 'Executing',
                            'RetryScheduled', 'Failed', 'ManualIntervention',
                        )
                    ),
                    None,
                )
                if current is not None:
                    current['isCurrent'] = True
            values = {}
            for row in task_rows:
                tid = row['TaskId']
                values[tid] = dict(
                    task=dict(
                        taskId=tid,
                        stationId=row['StationId'],
                        vehicleId=row.get('VehicleId'),
                        stage=TASK_STAGE_NAMES.get(row['Stage'], str(row['Stage'])),
                        createdAt=row.get('CreatedAt'),
                        updatedAt=row.get('UpdatedAt'),
                        errorMessage=row.get('ErrorMessage'),
                    ),
                    steps=steps_by_task.get(tid, []),
                    handover=dict(exitPhysicallyVerified=tid in completed_exit_ids),
                )
            return {tid: values.get(tid, dict(
                task=dict(taskId=tid, stationId=tasks.get(tid, {}).get('station'), vehicleId=None, stage='Unknown'),
                steps=[],
                handover=dict(exitPhysicallyVerified=False),
            )) for tid in ids}

        write(evidence / ('completion-start-' + uuid.uuid4().hex + '.json'), dict(taskIds=list(ids), startedAt=runtime.clock.time()))
        offline_at = None; fault_vehicle = None; held = set(); restored = False; offline_controls = None
        plc_fault_started = None; plc_recovery_controls = None
        road_block_started = None; road_block_controls = None
        obstacle_scan_started = None; obstacle_armed_at = None; obstacle_started = None
        obstacle_controls = None; obstacle_recovery = None
        while runtime.clock.monotonic() < deadline:
            if runtime.abort.is_set(): raise InterruptedError('场景已中止，原任务及占用保留；请核对后恢复。')
            details = batch_task_details() if large_batch else {
                tid: runtime.api('/api/tasks/' + tid) for tid in ids
            }
            for tid, detail in details.items():
                tasks[tid]['vehicle'] = detail['task'].get('vehicleId') or tasks[tid]['vehicle']
            leases = observer.sample(details, compact=large_batch)
            name = request['name']
            if name == 'vendor-cancel-return' and not fault_observed:
                candidate = next((tid for tid, detail in details.items()
                                  if next((step for step in detail['steps']
                                           if step.get('sequence') == 6), {}).get('status') == 'Executing'), None)
                if candidate:
                    fault_vehicle = details[candidate]['task'].get('vehicleId')
                    if not fault_vehicle: raise RuntimeError('倒料完成阶段未取得车辆身份。')
                    runtime.control(dict(vehicles={fault_vehicle: dict(cancelFinalReturnSegments=2)}))
                    applied = runtime.wait('车端返程取消控制未被设备应用',
                                           lambda: control_applied_now(runtime.device(), runtime.controls, fault_vehicle), 10)
                    write(evidence / 'vendor-cancel-applied.json', dict(taskId=candidate, vehicleId=fault_vehicle, applied=applied))
                    check('车端返程取消已在返程命令前武装到原车辆', True, fault_vehicle)
                    fault_observed = True
            if name not in ('obstacle-auto-detour', 'obstacle-auto-safety-return') and any(
                    d['task']['stage'] in ('Faulted', 'Cancelled') for d in details.values()):
                if name == 'vendor-cancel-return' and fault_observed:
                    cancelled = next(d for d in details.values() if d['task']['stage'] == 'Cancelled')
                    fault_vehicle = cancelled['task'].get('vehicleId') or tasks[cancelled['task']['taskId']].get('vehicle')
                    return_step = next((step for step in cancelled['steps'] if step.get('sequence') == 7), None)
                    held = query(runtime.root, 'select ResourceId,TaskId,VehicleId,OwnerStepId from traffic_resource_lease where TaskId=?',
                                 (cancelled['task']['taskId'],))
                    commands = query(runtime.root, 'select CommandId,Status from agv_command_outbox where TaskId=?',
                                     (cancelled['task']['taskId'],))
                    events = [
                        runtime.root / 'device-events.jsonl',
                        runtime.root / 'robots' / (fault_vehicle or '') / 'device-events.jsonl',
                    ]
                    cancelled_segments = []
                    for event_path in events:
                        if not event_path.is_file():
                            continue
                        for line in event_path.read_text(encoding='utf-8').splitlines():
                            event = json.loads(line)
                            if event.get('kind') == 'vendor-segment-cancelled':
                                cancelled_segments.append(event)
                    write(evidence / 'vendor-cancel-observed.json',
                          dict(task=cancelled['task'], returnStep=return_step, held=held, commands=commands,
                               cancelledSegments=cancelled_segments))
                    check('车端返程3066后段实际取消', (return_step or {}).get('status') == 'Cancelled' and
                          '=6' in str((return_step or {}).get('errorMessage') or '') and bool(cancelled_segments),
                          cancelled['task']['taskId'])
                    check('车端取消后保留原任务命令和路权', bool(commands) and bool(held) and commands[-1]['Status'] in (20, 30),
                          dict(commands=commands, held=held))
                    check('取消没有被伪造成业务完成', cancelled['task']['stage'] == 'Cancelled' and
                          not bool(cancelled.get('handover', {}).get('returnCommandCompleted')), cancelled['task']['taskId'])
                    return False
                raise RuntimeError('任务出现非预期故障或取消。')
            state = runtime.device()
            if name == 'obstacle-auto-detour':
                if obstacle_armed_at is None:
                    target_index = 2
                    active = next((tid for tid, detail in details.items()
                                   if next((step for step in detail['steps']
                                            if step.get('sequence') == target_index + 1), {}).get('status') == 'Executing'), None)
                    if active:
                        obstacle_scan_started = obstacle_scan_started or runtime.clock.monotonic()
                        vehicle_id = details[active]['task'].get('vehicleId')
                        car = (state.get('cars') or {}).get(vehicle_id or '', {})
                        navigation = read(runtime.root / 'navigation.agvmap.json')
                        moving_on_detourable_edge = bool(car.get('moving')) and has_alternate_connection(
                            navigation, car.get('motionEdge'))
                        if vehicle_id and moving_on_detourable_edge:
                            runtime.control(dict(vehicles={vehicle_id: dict(
                                obstacleAfterSegments=int(car.get('completed_segments') or 0) + 1)}))
                            obstacle_controls = json.loads(json.dumps(runtime.controls))
                            applied = runtime.wait('车端障碍控制未生效',
                                                   lambda: control_applied_now(runtime.device(), obstacle_controls, vehicle_id), 10)
                            obstacle_armed_at = runtime.clock.monotonic()
                            obstacle_recovery = dict(taskId=active, vehicleId=vehicle_id, firstObserved=None)
                            write(evidence / 'obstacle-auto-armed.json',
                                  dict(taskId=active, vehicleId=vehicle_id, edge=car.get('motionEdge'),
                                       completedSegments=car.get('completed_segments'), applied=applied))
                        elif runtime.clock.monotonic() - obstacle_scan_started >= 30:
                            check('当前地图存在可自动改道的独立连通道路', False,
                                  '原配送路线持续30秒未经过具有独立备选连接的路段；未注入虚假障碍。')
                elif obstacle_started is None:
                    car = (state.get('cars') or {}).get(obstacle_recovery['vehicleId'], {})
                    if car.get('blocked') is True and car.get('simulationControl', {}).get('obstacle') is True:
                        obstacle_started = runtime.clock.monotonic()
                        write(evidence / 'obstacle-auto-triggered.json',
                              dict(taskId=obstacle_recovery['taskId'], vehicleId=obstacle_recovery['vehicleId'],
                                   station=car.get('station'), completedSegments=car.get('completed_segments')))
                        check('车端在合法站点边界先报Blocked', True,
                              dict(taskId=obstacle_recovery['taskId'], station=car.get('station')))
                    elif runtime.clock.monotonic() - obstacle_armed_at >= 40:
                        check('车端在合法站点边界先报Blocked', False,
                              '障碍触发控制已应用，但车辆未在限制时间内到达可改道站点。')
                elif not fault_observed:
                    try:
                        snapshot = runtime.api('/api/snapshot')
                        observed = next((agv.get('recovery') for agv in snapshot.get('status', {}).get('agvs', [])
                                         if agv.get('recovery') and agv.get('vehicleId') == obstacle_recovery['vehicleId']), None)
                    except Exception:
                        observed = None
                    if observed:
                        obstacle_recovery['latest'] = observed
                        if obstacle_recovery.get('firstObserved') is None:
                            obstacle_recovery['firstObserved'] = observed
                        if observed.get('newVendorTaskId') or observed.get('sendAttempted'):
                            obstacle_recovery['sent'] = observed
                    elapsed = runtime.clock.monotonic() - obstacle_started
                    if elapsed >= request['holdSeconds'] + 20 and observed is None:
                        check('后台收到车端障碍并建立恢复记录', False,
                              '超过观察窗口仍未取得原任务、车辆或恢复状态；保留设备与任务现场。')
                    if (elapsed >= request['holdSeconds'] and
                            obstacle_detour_send_observed(observed)):
                        runtime.control(dict(vehicles={obstacle_recovery['vehicleId']: dict(
                            obstacle=False, obstacleAfterSegments=None)}))
                        cleared = runtime.wait('障碍清除未生效',
                                               lambda: control_applied_now(runtime.device(), runtime.controls,
                                                                       obstacle_recovery['vehicleId']), 10)
                        write(evidence / 'obstacle-auto-recovery.json',
                              dict(clearApplied=cleared, firstObserved=obstacle_recovery.get('firstObserved'),
                                   latest=obstacle_recovery.get('latest'), sent=obstacle_recovery.get('sent')))
                        check('后台先保留原业务任务再自动生成替代路线',
                              bool(obstacle_recovery.get('firstObserved')) and
                              obstacle_recovery['firstObserved'].get('taskId') == obstacle_recovery['taskId'],
                              obstacle_recovery.get('firstObserved'))
                        check('原导航终态后只发送一条替代3066并绑定原任务',
                              bool(obstacle_recovery.get('sent')) and
                              obstacle_recovery['sent'].get('state') in ('Executing', 'Recovered'),
                              obstacle_recovery.get('latest'))
                        auto_detour_sends += 1
                        fault_observed = True
                    elif elapsed >= obstacle_detour_observation_deadline(runtime.settings, request['holdSeconds']):
                        check('后台等待原3066终态并发送替代导航', False,
                              dict(state=(observed or {}).get('state'),
                                   message=(observed or {}).get('message'),
                                   sendAttempted=(observed or {}).get('sendAttempted'),
                                   newVendorTaskId=(observed or {}).get('newVendorTaskId'),
                                   originalPath=(observed or {}).get('originalPath'),
                                   candidatePath=(observed or {}).get('candidatePath')))
                        return False
            if name == 'obstacle-auto-safety-return':
                if obstacle_armed_at is None:
                    target_index = 2
                    active = next((tid for tid, detail in details.items()
                                   if next((step for step in detail['steps']
                                            if step.get('sequence') == target_index + 1), {}).get('status') == 'Executing'), None)
                    if active:
                        obstacle_scan_started = obstacle_scan_started or runtime.clock.monotonic()
                        vehicle_id = details[active]['task'].get('vehicleId')
                        car = (state.get('cars') or {}).get(vehicle_id or '', {})
                        navigation = read(runtime.root / 'navigation.agvmap.json')
                        station_node = next(
                            (node for node in navigation.get('nodes', [])
                             if node.get('agvSiteId') == car.get('station')),
                            None)
                        deviation = (
                            math.hypot(float(car.get('x', 0)) - station_node['x'],
                                       float(car.get('y', 0)) - station_node['y'])
                            if station_node is not None else 0)
                        if (vehicle_id and car.get('moving') and car.get('motionEdge')
                                and deviation >= .8):
                            runtime.control(dict(vehicles={vehicle_id: dict(obstacle=True)}))
                            obstacle_controls = json.loads(json.dumps(runtime.controls))
                            applied = runtime.wait(
                                '车端道路中段障碍控制未生效',
                                lambda: control_applied_now(runtime.device(), obstacle_controls, vehicle_id), 10)
                            obstacle_armed_at = runtime.clock.monotonic()
                            obstacle_recovery = dict(
                                taskId=active, vehicleId=vehicle_id, firstObserved=None,
                                states=[], safeTarget=None, sent=None)
                            write(evidence / 'obstacle-auto-safety-armed.json',
                                  dict(taskId=active, vehicleId=vehicle_id,
                                       edge=car.get('motionEdge'),
                                       station=car.get('station'),
                                       x=car.get('x'), y=car.get('y'), applied=applied))
                        elif runtime.clock.monotonic() - obstacle_scan_started >= 30:
                            check('道路中段障碍能够绑定原运输车辆', False,
                                  '满箱运输步骤持续30秒未在道路中移动，未注入虚假障碍。')
                elif obstacle_started is None:
                    car = (state.get('cars') or {}).get(obstacle_recovery['vehicleId'], {})
                    if car.get('blocked') is True and car.get('simulationControl', {}).get('obstacle') is True:
                        obstacle_started = runtime.clock.monotonic()
                        write(evidence / 'obstacle-auto-safety-triggered.json',
                              dict(taskId=obstacle_recovery['taskId'],
                                   vehicleId=obstacle_recovery['vehicleId'],
                                   station=car.get('station'),
                                   edge=car.get('motionEdge'),
                                   x=car.get('x'), y=car.get('y')))
                        check('虚拟车在路段中间真实上报Blocked', True,
                              dict(taskId=obstacle_recovery['taskId'],
                                   vehicleId=obstacle_recovery['vehicleId'],
                                   edge=car.get('motionEdge')))
                    elif runtime.clock.monotonic() - obstacle_armed_at >= 15:
                        check('虚拟车在路段中间真实上报Blocked', False,
                              '障碍控制已应用，但车辆未形成车端阻挡反馈。')
                elif not fault_observed:
                    try:
                        snapshot = runtime.api('/api/snapshot')
                        observed = next(
                            (agv.get('recovery') for agv in snapshot.get('status', {}).get('agvs', [])
                             if agv.get('recovery') and
                             agv.get('vehicleId') == obstacle_recovery['vehicleId']),
                            None)
                    except Exception:
                        observed = None
                    if observed:
                        obstacle_recovery['latest'] = observed
                        if obstacle_recovery.get('firstObserved') is None:
                            obstacle_recovery['firstObserved'] = observed
                        state_name = observed.get('state')
                        if state_name and state_name not in obstacle_recovery['states']:
                            obstacle_recovery['states'].append(state_name)
                        if observed.get('safetyPointTarget'):
                            obstacle_recovery['safeTarget'] = observed['safetyPointTarget']
                        if state_name == 'Executing' and observed.get('newVendorTaskId'):
                            obstacle_recovery['sent'] = observed
                    if obstacle_recovery.get('sent'):
                        runtime.control(dict(vehicles={
                            obstacle_recovery['vehicleId']: dict(
                                obstacle=False, obstacleAfterSegments=None)
                        }))
                        cleared = runtime.wait(
                            '道路中段退回后的障碍清除未生效',
                            lambda: control_applied_now(
                                runtime.device(), runtime.controls,
                                obstacle_recovery['vehicleId']), 10)
                        event_rows = []
                        for event_path in (
                            runtime.root / 'device-events.jsonl',
                            runtime.root / 'robots' /
                            obstacle_recovery['vehicleId'] / 'device-events.jsonl',
                        ):
                            if not event_path.is_file():
                                continue
                            for line in event_path.read_text(
                                    encoding='utf-8').splitlines():
                                event = json.loads(line)
                                if event.get('kind') == 'robokit' and event.get('api') in (3051, 3055):
                                    event_rows.append(event)
                        write(evidence / 'obstacle-auto-safety-recovery.json',
                              dict(
                                  clearApplied=cleared,
                                  firstObserved=obstacle_recovery.get('firstObserved'),
                                  states=obstacle_recovery['states'],
                                  safeTarget=obstacle_recovery.get('safeTarget'),
                                  latest=obstacle_recovery.get('latest'),
                                  sent=obstacle_recovery.get('sent'),
                                  protocolSafetyRetreat=event_rows))
                        check('后台识别道路中段受阻并进入独立退站流程',
                              'SafetyPointReady' in obstacle_recovery['states'] or
                              'SafetyPointMoving' in obstacle_recovery['states'] or
                              'SafetyPointReached' in obstacle_recovery['states'],
                              obstacle_recovery)
                        check('原全链终态后只发送一次安全后退命令',
                              len(event_rows) == 1 and (
                                  (
                                      event_rows[0].get('api') == 3051 and
                                      (event_rows[0].get('request') or {}).get('source_id') == 'SELF_POSITION' and
                                      (event_rows[0].get('request') or {}).get('method') == 'backward' and
                                      obstacle_recovery.get('safeTarget') ==
                                      (event_rows[0].get('request') or {}).get('id')
                                  ) or (
                                      event_rows[0].get('api') == 3055 and
                                      0 < (event_rows[0].get('request') or {}).get('dist', 0) <= 1 and
                                      (event_rows[0].get('request') or {}).get('vx') == -.1 and
                                      (event_rows[0].get('request') or {}).get('mode') == 1
                                  )
                              ),
                              event_rows)
                        check('车辆到安全站后再续接原业务任务',
                              bool(obstacle_recovery.get('sent')) and
                              obstacle_recovery['sent'].get('taskId') ==
                              obstacle_recovery['taskId'],
                              obstacle_recovery.get('sent'))
                        auto_detour_sends += 1
                        fault_observed = True
                    elif runtime.clock.monotonic() - obstacle_started >= request['timeoutSeconds']:
                        check('道路中段安全点退回在观察期内完成', False,
                              obstacle_recovery)
            if name == 'road-block-recovery':
                if road_block_started is None:
                    active = next((tid for tid, detail in details.items()
                                   if next((step for step in detail['steps']
                                            if step.get('sequence') == 1), {}).get('status') == 'Executing'), None)
                    if active:
                        vehicle_id = details[active]['task'].get('vehicleId')
                        edge = state.get('cars', {}).get(vehicle_id or '', {}).get('motionEdge')
                        if vehicle_id and edge:
                            runtime.control(dict(vehicles={vehicle_id: dict(blockedEdges=[edge])}))
                            road_block_controls = json.loads(json.dumps(runtime.controls))
                            applied = runtime.wait('道路阻塞控制未生效',
                                                   lambda: control_applied_now(runtime.device(), road_block_controls, vehicle_id), 10)
                            write(evidence / 'road-block-applied.json',
                                  dict(taskId=active, vehicleId=vehicle_id, edge=edge, applied=applied))
                            check('道路阻塞在正式运输途中实际生效', True, dict(vehicleId=vehicle_id, edge=edge))
                            road_block_started = runtime.clock.monotonic()
                elif not fault_observed and runtime.clock.monotonic() - road_block_started >= request['holdSeconds']:
                    vehicle_id = next(tasks[tid]['vehicle'] for tid in ids if tasks[tid]['vehicle'])
                    check('道路阻塞期间原任务未完成', any(d['task']['stage'] != 'Completed' for d in details.values()),
                          dict(taskIds=list(ids), elapsed=runtime.clock.monotonic() - road_block_started))
                    runtime.control(dict(vehicles={vehicle_id: dict(blockedEdges=[])}))
                    applied = runtime.wait('道路阻塞解除未生效',
                                           lambda: control_applied_now(runtime.device(), runtime.controls, vehicle_id), 10)
                    write(evidence / 'road-block-released.json', dict(vehicleId=vehicle_id, applied=applied))
                    fault_observed = True
            if name == 'plc-recovery' and plc_fault_started is None:
                target_index = 3 if request['plcFault'] == 'ready' else 5
                target = next((tid for tid, detail in details.items()
                    if len(detail['steps']) > target_index and detail['steps'][target_index]['status'] in ('Ready', 'Executing')), None)
                if target:
                    signal = 'suppressReady' if request['plcFault'] == 'ready' else 'suppressDone'
                    runtime.control(dict(plc={signal: True}))
                    plc_recovery_controls = json.loads(json.dumps(runtime.controls))
                    applied = runtime.wait('PLC卡位控制未生效', lambda: control_applied_now(runtime.device(), plc_recovery_controls), 10)
                    check('PLC卡位在建单执行中实际生效', True, dict(taskId=target, signal=signal, applied=applied))
                    write(evidence / 'plc-fault-applied.json', dict(taskId=target, signal=signal, applied=applied, detail=details[target]))
                    plc_fault_started = runtime.clock.monotonic()
            if name == 'plc-recovery' and plc_fault_started is not None and not fault_observed and runtime.clock.monotonic() - plc_fault_started >= request['holdSeconds']:
                target_index = 3 if request['plcFault'] == 'ready' else 5
                target = next(tid for tid in ids if len(details[tid]['steps']) > target_index)
                check('卡位期间原任务保持等待且未跳步', details[target]['steps'][target_index]['status'] in ('Ready', 'Executing'),
                      details[target]['steps'][target_index])
                signal = 'suppressReady' if request['plcFault'] == 'ready' else 'suppressDone'
                runtime.control(dict(plc={signal: False}))
                acknowledged = runtime.wait('PLC卡位解除未被设备确认', lambda: control_applied_now(runtime.device(), runtime.controls), 10)
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
                runtime.control(dict(vehicles={fault_vehicle: dict(offline=True)})); offline_at = runtime.clock.monotonic()
                offline_controls = json.loads(json.dumps(runtime.controls))
                write(evidence / 'disconnect.json', dict(taskId=candidate, held=sorted(held), device=runtime.device()))
            if offline_at and not restored and runtime.clock.monotonic() - offline_at >= 5:
                car = runtime.device()['cars'][fault_vehicle]
                check('断连故障已被模型应用', car.get('offline') is True and bool(control_applied_now(runtime.device(), offline_controls, fault_vehicle)), fault_vehicle)
                tid = next(t for t in ids if tasks[t]['vehicle'] == fault_vehicle)
                check('断连保留原路权', held <= {r['ResourceId'] for r in leases if r['TaskId'] == tid}, sorted(held))
                runtime.control(dict(vehicles={fault_vehicle: dict(offline=False)}))
                acknowledged = runtime.wait('重连控制未被设备确认', lambda: control_applied_now(runtime.device(), runtime.controls, fault_vehicle), 10)
                write(evidence / 'reconnect-applied.json', acknowledged)
                restored = True; fault_observed = True
            if all(d['task']['stage'] == 'Completed' for d in details.values()):
                if name == 'vendor-cancel-return' and fault_observed:
                    raise RuntimeError('返程取消已注入但任务仍完成，取消未命中真实3066任务链。')
                write(evidence / ('completion-observed-' + uuid.uuid4().hex + '.json'),
                      {tid: dict(stage=detail['task']['stage'], steps=[step['status'] for step in detail['steps']])
                       for tid, detail in details.items()})
                write(evidence / ('completed-' + uuid.uuid4().hex + '.json'), details)
                check('原任务九步完成', all(len(d['steps']) == 9 and all(s['status'] == 'Completed' for s in d['steps']) for d in details.values()), list(ids))
                check('原任务出区证据', all(d.get('handover', {}).get('exitPhysicallyVerified') for d in details.values()), list(ids))
                if name == 'response-loss' and not fault_observed:
                    dropped = []
                    for vehicle in {tasks[tid]['vehicle'] for tid in ids}:
                        source = runtime.root / 'robots' / vehicle / 'device-events.jsonl'
                        if not source.exists(): source = runtime.root / 'device-events.jsonl'
                        if source.exists():
                            dropped.extend(json.loads(line) for line in source.read_text(encoding='utf-8').splitlines()
                                           if '"simulation-response-dropped"' in line)
                    write(evidence / 'response-loss-observed.json', dropped)
                    check('正式运输命令回包丢失已实际触发', bool(dropped), dropped)
                    fault_observed = True
                completed_tasks.update(ids)
                return True
            if large_batch:
                stage_counts = {}
                for detail in details.values():
                    stage = detail['task']['stage']
                    stage_counts[stage] = stage_counts.get(stage, 0) + 1
                assigned = sum(1 for detail in details.values() if detail['task'].get('vehicleId'))
                runtime.scenario['message'] = (
                    f'{len(details)} 单压力批次：已派车 {assigned} 单；'
                    + '，'.join(f'{stage} {count}' for stage, count in sorted(stage_counts.items()))
                )
            else:
                runtime.scenario['message'] = '；'.join(describe_progress(tasks[t]['vehicle'], d) for t, d in details.items())
            runtime.abort.wait(.5)
        raise TimeoutError('任务未在期限内闭合，可能存在长期等待；保留全部现场。')

    def run_obstacle_timeout(task_id, scenario_name):
        nonlocal fault_observed, recovery_hold, auto_detour_sends
        case = OBSTACLE_TIMEOUT_CASES[scenario_name]
        target_index = case['index']
        deadline = runtime.clock.monotonic() + request['timeoutSeconds']
        armed = False
        timeout_controls = None
        obstacle_safe_site = None
        vehicle_id = tasks[task_id]['vehicle']
        detail = None
        step = None
        while runtime.clock.monotonic() < deadline:
            if runtime.abort.is_set():
                raise InterruptedError('障碍超时场景已中止；原任务和现场占用保留。')
            detail = runtime.api('/api/tasks/' + task_id)
            observer.sample({task_id: detail})
            if len(detail.get('steps', [])) <= target_index:
                runtime.abort.wait(.2)
                continue
            step = detail['steps'][target_index]
            state = fresh_device_state(runtime)
            car = (state.get('cars') or {}).get(vehicle_id, {})
            runtime.scenario['message'] = (
                f'等待{STEP_NAMES[case["step_type"]]}；'
                f'步骤={step.get("status")}，车辆任务={car.get("current_task") or "无"}，'
                f'运动中={"是" if car.get("moving") else "否"}')
            if not armed:
                requires_midroad = scenario_name in (
                    'obstacle-timeout-midroad', 'obstacle-timeout-manual', 'obstacle-timeout-safe-site')
                midroad_ready = True
                if requires_midroad:
                    navigation = read(runtime.root / 'navigation.agvmap.json')
                    station_node = next((item for item in navigation.get('nodes', [])
                                         if item.get('agvSiteId') == car.get('station')), None)
                    midroad_ready = bool(
                        station_node and car.get('current_task') and car.get('moving') and
                        math.hypot(float(car.get('x', 0)) - station_node['x'],
                                   float(car.get('y', 0)) - station_node['y']) > 0.3)
                    if scenario_name == 'obstacle-timeout-safe-site':
                        midroad_ready = midroad_ready and has_alternate_connection(
                            navigation, car.get('motionEdge'))
                if (step.get('stepType') == case['step_type'] and step.get('status') == 'Executing'
                        and midroad_ready):
                    check(f'车辆已进入{STEP_NAMES[case["step_type"]]}运输'
                          + ('且处于道路中段' if requires_midroad else '且原任务身份有效'),
                          True, dict(taskId=task_id, station=car.get('station'),
                                     x=car.get('x'), y=car.get('y')))
                    runtime.control(dict(vehicles={vehicle_id: dict(
                        speedScale=0.2, obstacle=True,
                        navigationTimeoutAfterBlockedSeconds=request['holdSeconds'])}))
                    timeout_controls = json.loads(json.dumps(runtime.controls))
                    applied = runtime.wait('车端障碍与导航超时控制未生效',
                                           lambda: control_applied_now(runtime.device(), timeout_controls, vehicle_id), 10)
                    write(evidence / 'obstacle-timeout-armed.json',
                          dict(taskId=task_id, vehicleId=vehicle_id, stepType=case['step_type'],
                               timeoutAfterBlockedSeconds=request['holdSeconds'], applied=applied,
                               safeSite=car.get('station') if scenario_name == 'obstacle-timeout-safe-site' else None))
                    if scenario_name == 'obstacle-timeout-safe-site':
                        obstacle_safe_site = car.get('station')
                    check('障碍保持到车端状态7的超时注入已生效', True, applied)
                    armed = True
                else:
                    runtime.abort.wait(.1)
                    continue
            failed_status = next((item for item in (car.get('tasks') or {}).values() if item == 7), None)
            if (detail['task'].get('stage') == 'Faulted' and
                    step.get('status') == 'ManualIntervention' and failed_status == 7):
                write(evidence / 'obstacle-timeout-observed.json',
                      dict(task=detail['task'], step=step, vehicle=car))
                break
            runtime.abort.wait(.2)
        else:
            raise TimeoutError('车辆未在场景期限内形成“持续受阻并返回状态7”的故障。')

        recovery = runtime.wait(
            '后台未记录运输超时恢复现场',
            lambda: (lambda current: current if current.get('taskId') == task_id else None)(
                runtime.api('/api/timeout-recovery/' + task_id)),
            20)
        write(evidence / 'timeout-recovery-captured.json', recovery)
        check('状态7已进入原任务冻结而不是伪完成',
              detail['task']['stage'] == 'Faulted' and step['status'] == 'ManualIntervention', step)
        check('超时恢复记录绑定原任务、原步骤和原车辆',
              recovery.get('taskId') == task_id and recovery.get('stepId') == step.get('stepId') and
              recovery.get('vehicleId') == vehicle_id, recovery)
        check('首次超时没有自动发送第二条导航',
              recovery.get('sendAttempted') is False and not recovery.get('newVendorTaskId'),
              recovery)
        leases = query(runtime.root, 'select ResourceId from traffic_resource_lease where TaskId=?', (task_id,))
        write(evidence / 'timeout-held-resources.json', leases)

        car = (fresh_device_state(runtime).get('cars') or {}).get(vehicle_id, {})
        if scenario_name == 'obstacle-timeout-pickup':
            check('取料途中故障时车辆为空载', car.get('loaded') is False, car)
            check('取料超时后原任务和路权仍保留',
                  bool(leases) and recovery.get('originalVendorTaskId') == step.get('vendorTaskId'), leases)
        elif scenario_name == 'obstacle-timeout-fullbox':
            check('满箱运输故障时车辆仍持有满箱', car.get('loaded') is True, car)
            check('满箱运输没有自动换车',
                  recovery.get('vehicleId') == vehicle_id and detail['task'].get('vehicleId') == vehicle_id,
                  detail['task'])
        elif scenario_name == 'obstacle-timeout-return':
            dump_done = target_index > 0 and detail['steps'][target_index - 1].get('status') == 'Completed'
            check('空箱返程故障前倒料步骤已经结束', dump_done, detail['steps'][target_index - 1])
            check('返程超时没有提前完成返箱或放箱',
                  detail['steps'][target_index]['status'] == 'ManualIntervention' and
                  all(item.get('status') != 'Completed' for item in detail['steps'][target_index + 1:]),
                  detail['steps'][target_index:])
        elif scenario_name in ('obstacle-timeout-midroad', 'obstacle-timeout-manual',
                               'obstacle-timeout-safe-site'):
            navigation = read(runtime.root / 'navigation.agvmap.json')
            station = car.get('station')
            node = next((item for item in navigation.get('nodes', [])
                         if item.get('agvSiteId') == station), None)
            if node is None:
                raise RuntimeError('故障车辆没有可用于核对的当前厂家站点。')
            deviation = math.hypot(float(car.get('x', 0)) - node['x'],
                                   float(car.get('y', 0)) - node['y'])
            check('道路中段故障没有被最近站点冒充为可信起点',
                  deviation > 0.3 and recovery.get('outcome') != 'ReadyForControlledDetour',
                  dict(station=station, deviationMeters=deviation, recovery=recovery))

        if scenario_name == 'obstacle-timeout-safe-site':
            original_path = recovery.get('originalPath') or []
            if len(original_path) < 2:
                raise RuntimeError('原路线快照缺失，不能验证安全点续接。')
            navigation = read(runtime.root / 'navigation.agvmap.json')
            current_site = recovery.get('currentStation')
            position = (original_path.index(current_site)
                        if current_site in original_path else -1)
            safe_types = {'Station', 'WaitingPoint', 'ChargingPoint'}
            safe_candidates = [
                site for site in original_path[position + 1:-1]
                if next((node for node in navigation.get('nodes', [])
                         if node.get('agvSiteId') == site), {}).get('type') in safe_types]
            safe_site = (safe_candidates[0] if safe_candidates else
                         obstacle_safe_site or original_path[position if position >= 0 else 0])
            runtime.control(dict(vehicles={vehicle_id: dict(
                obstacle=False, navigationTimeoutAfterBlockedSeconds=None,
                manualRelocationSite=safe_site, speedScale=0.75)}))
            relocated_controls = json.loads(json.dumps(runtime.controls))
            relocated = runtime.wait(
                '受控安全点转移控制未生效',
                lambda: control_applied_now(runtime.device(), relocated_controls, vehicle_id), 10)
            write(evidence / 'safe-site-relocated.json',
                  dict(vehicleId=vehicle_id, safeSite=safe_site, applied=relocated))
            ready = runtime.wait(
                '障碍清除并到达安全点后未重新形成可执行候选',
                lambda: (lambda current: current if current.get('state') == 'ReadyForControlledDetour'
                         else None)(runtime.api('/api/timeout-recovery/' + task_id)), 30)
            write(evidence / 'safe-site-ready.json', ready)
            check('车辆到安全点、停稳且障碍清除后可以受控续接',
                  ready.get('currentStation') == safe_site and
                  ready.get('outcome') == 'ReadyForControlledDetour' and
                  len(ready.get('candidatePath') or []) >= 2, ready)
            executed = runtime.api('/api/timeout-recovery/' + task_id + '/execute', dict(
                taskId=task_id, expectedRevision=ready['revision'], executeConfirmed=True,
                vehicleAndLocationConfirmed=True, loadAndOwnershipConfirmed=True,
                plcConditionsConfirmed=True,
                reason=f'仿真确认：车辆已受控转移到安全点{safe_site}，载荷、PLC和原任务一致。'
            ))
            write(evidence / 'safe-site-recovery-executed.json', executed)
            check('安全点续接由原车发送一次替代导航并绑定原任务',
                  executed.get('accepted') is True and
                  (executed.get('snapshot') or {}).get('vehicleId') == vehicle_id, executed)
            auto_detour_sends += 1
            task_after = runtime.api('/api/tasks/' + task_id)
            check('安全点续接后原业务任务仍在且已重新执行',
                  task_after.get('task', {}).get('vehicleId') == vehicle_id and
                  task_after.get('task', {}).get('stage') not in ('Faulted', 'Cancelled', 'Completed'),
                  task_after.get('task'))
            complete([task_id])
            runtime.scenario['completedRounds'] = 1
            fault_observed = True
            return

        if scenario_name in ('obstacle-timeout-midroad', 'obstacle-timeout-manual'):
            runtime.control(dict(vehicles={vehicle_id: dict(
                obstacle=False, navigationTimeoutAfterBlockedSeconds=None,
                offline=scenario_name == 'obstacle-timeout-manual')}))
            cleared_controls = json.loads(json.dumps(runtime.controls))
            runtime.wait('障碍清除或离线注入未生效',
                         lambda: control_applied_now(runtime.device(), cleared_controls, vehicle_id), 10)
            expected = 'SafetyHold' if scenario_name == 'obstacle-timeout-manual' else 'ManualRelocationRequired'
            reassessed = runtime.wait(
                '超时恢复状态未重新评估',
                lambda: (lambda current: current if current.get('outcome') == expected else None)(
                    runtime.api('/api/timeout-recovery/' + task_id)), 30)
            write(evidence / 'timeout-recovery-reassessed.json', reassessed)
            check('无法安全自动恢复时进入明确的人工处置而不是自动发车',
                  reassessed.get('sendAttempted') is False and
                  reassessed.get('state') == 'ManualIntervention' and
                  reassessed.get('outcome') == expected, reassessed)

        fault_observed = True
        recovery_hold = True
        runtime.scenario['completedRounds'] = 0

    try:
        if hasattr(runtime, 'version_comparison'):
            version_start = runtime.version_comparison.refresh()
            write(evidence / 'version-start.json', version_start)
        if automatic:
            baseline = {row['TaskId'] for row in query(runtime.root, 'select TaskId from dispatch_task')}
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
            check('运行前无残留路权', not business_leases(runtime.root), '只读数据库核对（充电会话自身的充电位和路线租约除外）')
        if any(v.get('offline') or v.get('paused') or v.get('dropNextResponse') or v.get('blockedEdges') or
               v.get('obstacle') or v.get('obstacleAfterSegments') is not None or
               v.get('navigationTimeoutAfterBlockedSeconds') is not None or
               v.get('manualRelocationSite') is not None
               for v in runtime.controls['vehicles'].values()) or any(value != PLC_DEFAULTS.get(key) for key, value in runtime.controls['plc'].items()):
            raise RuntimeError('开始场景前请解除人工故障；不自动覆盖控制。')
        if request['name'] == 'plc-delay':
            runtime.control(dict(plc=dict(readyDelaySeconds=3, doneDelaySeconds=8)))
            plc_fault_controls = json.loads(json.dumps(runtime.controls))
            applied = runtime.wait('PLC延迟控制未被设备应用', lambda: control_applied_now(runtime.device(), plc_fault_controls), 10)
            write(evidence / 'plc-delay-applied.json', applied)
            check('PLC延迟在建单前被设备确认', True, applied)
            fault_observed = True
        if request['name'] == 'response-loss':
            vehicle_ids = [robot['VehicleId'] for robot in runtime.settings['Agv']['Robots']]
            runtime.control(dict(vehicles={vehicle_id: dict(dropNextResponse=True) for vehicle_id in vehicle_ids}))
            loss_controls = json.loads(json.dumps(runtime.controls))
            applied = {
                vehicle_id: runtime.wait('运输回包丢失控制未生效',
                                         lambda vehicle_id=vehicle_id: control_applied_now(runtime.device(), loss_controls, vehicle_id), 10)
                for vehicle_id in vehicle_ids
            }
            write(evidence / 'response-loss-applied.json', applied)
            check('运输回包丢失控制已应用到各虚拟车', all(applied.values()), applied)
        pairs = [(station, None) for station in runtime.settings['Dispatching']['StationPickupSites']] if automatic else list(zip(runtime.settings['Dispatching']['StationPickupSites'], [r['VehicleId'] for r in runtime.settings['Agv']['Robots']]))
        if not pairs: raise ValueError('没有可测试的车辆与工位配对。')
        if request['name'] == 'plc-recovery': pairs = pairs[:1]
        if request['name'] == 'simultaneous-calls' and len(pairs) < 2: raise ValueError('同时呼叫场景至少需要两辆车和两个工位。')
        if request['name'] in ('dispatch-backlog', 'business-calls'):
            from automatic_calls import run_backlog
            run_backlog(runtime, request, evidence, observer, tasks, completed_tasks, discover, check)
        elif request['name'] == 'custom-calls':
            ordered = sorted(request['calls'], key=lambda call: (call['atSeconds'], call['stationId']))
            grouped = []
            for call in ordered:
                if not grouped or grouped[-1][0] != call['atSeconds']:
                    grouped.append((call['atSeconds'], [call]))
                else:
                    grouped[-1][1].append(call)
            all_created = []
            batch_timeline = []
            round_started = runtime.clock.monotonic()
            station_offsets = {station['stationId']: station['offset'] for station in runtime.lab.get('simulationTopology', {}).get('stations', [])}
            def calls_closed(group_calls):
                memory = bytes.fromhex(fresh_device_state(runtime)['memory_hex'])
                return memory[610] == 0 and all(memory[station_offsets.get(call['stationId'], 0 if call['stationId'] == 'CNC-001' else 2)] == 10
                                                for call in group_calls)
            closed_since = None
            def cycles_stable(group_calls):
                nonlocal closed_since
                if not calls_closed(group_calls):
                    closed_since = None
                    return False
                closed_since = closed_since or runtime.clock.monotonic()
                return runtime.clock.monotonic() - closed_since >= 1.5
            for due, group in grouped:
                remaining = due - (runtime.clock.monotonic() - round_started)
                if remaining > 0 and runtime.abort.wait(remaining): raise InterruptedError('场景已中止。')
                if all_created:
                    closed_since = None
                    runtime.wait('上一轮工位握手尚未稳定闭合', lambda: cycles_stable(group), request['timeoutSeconds'])
                previous_completed_at = batch_timeline[-1]['completedAt'] if batch_timeline else started
                batch_issued_at = runtime.clock.time()
                stations = [call['stationId'] for call in group]
                sequence = uuid.uuid4().hex
                payload = dict(sequence=sequence, call=stations[0]) if len(stations) == 1 else dict(sequence=sequence, calls=stations)
                write(runtime.root / 'device-input.json', payload)
                applied = runtime.wait('自定义呼叫批次未被设备确认', lambda: external_input(runtime, sequence), 10)
                check('自定义呼叫批次已由设备应用', applied.get('accepted') is True, applied)
                write(evidence / ('call-batch-' + str(len(all_created)) + '-' + uuid.uuid4().hex + '.json'),
                      dict(request=payload, requestedAtSeconds=due, actualElapsedSeconds=runtime.clock.monotonic()-round_started, applied=applied))
                # The PLC already holds all calls in this batch. Poll the
                # durable task table with one shared deadline so later station
                # numbers are not judged as missing while earlier calls are
                # still being persisted and acknowledged.
                batch_ids = collect_batch([call['stationId'] for call in group]) if automatic else [
                    create(call['stationId'], call.get('vehicleId'), trigger=False) for call in group]
                all_created.extend(batch_ids)
                complete(batch_ids)
                batch_timeline.append(dict(
                    batchNumber=len(batch_timeline) + 1,
                    issuedAt=batch_issued_at,
                    completedAt=runtime.clock.time(),
                    previousBatchCompletedAt=previous_completed_at,
                    taskIds=list(batch_ids),
                    stations=list(stations)))
                write(evidence / 'batch-timeline.json', batch_timeline)
                runtime.scenario['completedRounds'] = len(all_created)
            check('自定义呼叫严格按批次串行推进',
                  all(row['batchNumber'] == 1 or row['issuedAt'] >= batch_timeline[index - 1]['completedAt']
                      for index, row in enumerate(batch_timeline)),
                  batch_timeline)
            check('全部自定义呼叫均已创建并完成原任务', len(all_created) == len(request['calls']), f'{len(all_created)}个任务')
            runtime.scenario['completedRounds'] = len(all_created)
        elif request['name'] == 'vendor-cancel-return':
            station, vehicle = pairs[0]
            task_id = create(station, vehicle)
            detail = runtime.api('/api/tasks/' + task_id)
            assigned = detail['task'].get('vehicleId')
            if not assigned:
                assigned = runtime.wait('返程取消任务未取得车辆身份',
                                        lambda: runtime.api('/api/tasks/' + task_id)['task'].get('vehicleId'), 20)
            # Arm the one-shot fault before the return command is sent. The
            # previous timing waited for step 7 "Executing", by which point the
            # complete 3066 chain had already been accepted by the virtual car.
            runtime.control(dict(vehicles={assigned: dict(cancelFinalReturnSegments=2)}))
            applied = runtime.wait('返程取消控制未被设备应用',
                                   lambda: control_applied_now(runtime.device(), runtime.controls, assigned), 10)
            write(evidence / 'vendor-cancel-armed.json',
                  dict(taskId=task_id, vehicleId=assigned, applied=applied))
            check('车端返程取消已在任何返程命令前武装', True, assigned)
            fault_observed = True
            complete([task_id])
            runtime.scenario['completedRounds'] = 0
        elif request['name'] == 'plc-recovery':
            station, vehicle = pairs[0]
            signal = 'suppressReady' if request['plcFault'] == 'ready' else 'suppressDone'
            target_index = 3 if request['plcFault'] == 'ready' else 5
            runtime.control(dict(plc={signal: True}))
            applied = runtime.wait('PLC卡位控制未被设备应用',
                                   lambda: control_applied_now(runtime.device(), runtime.controls), 10)
            write(evidence / 'plc-fault-armed.json', dict(signal=signal, applied=applied))
            task_id = create(station, vehicle)
            reached = runtime.wait(
                '原任务未到达指定PLC卡位步骤',
                lambda: next((detail for detail in [
                    runtime.api('/api/tasks/' + task_id)]
                    if len(detail['steps']) > target_index and
                    detail['steps'][target_index]['status'] in ('Ready', 'Executing') and
                    detail['task']['stage'] not in ('Completed', 'Cancelled', 'Faulted')),
                    None), 90)
            blocked_at = reached['steps'][target_index]
            write(evidence / 'plc-fault-observed.json',
                  dict(taskId=task_id, signal=signal, step=blocked_at))
            start = runtime.clock.monotonic()
            while runtime.clock.monotonic() - start < request['holdSeconds']:
                if runtime.abort.wait(.25):
                    raise InterruptedError('PLC卡位保持期间场景已中止。')
            held = runtime.api('/api/tasks/' + task_id)
            check('卡位期间原任务保持等待且未跳步',
                  len(held['steps']) > target_index and
                  held['steps'][target_index]['status'] in ('Ready', 'Executing') and
                  held['task']['stage'] not in ('Completed', 'Cancelled', 'Faulted'),
                  held['steps'][target_index])
            runtime.control(dict(plc={signal: False}))
            released = runtime.wait('PLC卡位解除未被设备确认',
                                    lambda: control_applied_now(runtime.device(), runtime.controls), 10)
            write(evidence / 'plc-fault-released.json',
                  dict(taskId=task_id, signal=signal, applied=released))
            fault_observed = True
            complete([task_id])
            runtime.scenario['completedRounds'] = 1
        elif request['name'] in TIMEOUT_OBSTACLE_NAMES:
            station, vehicle = pairs[0]
            task_id = create(station, vehicle)
            assigned = runtime.wait(
                '障碍超时场景未取得正式调度车辆',
                lambda: runtime.api('/api/tasks/' + task_id)['task'].get('vehicleId'),
                40)
            tasks[task_id]['vehicle'] = assigned
            run_obstacle_timeout(task_id, request['name'])
        elif request['name'] in (
                'response-loss', 'road-block-recovery', 'obstacle-auto-detour',
                'obstacle-auto-safety-return'):
            station, vehicle = pairs[0]
            task_id = create(station, vehicle)
            complete([task_id])
            runtime.scenario['completedRounds'] = 1
        elif request['name'] != 'resume-recovery':
            for iteration in range(request['rounds']):
                if request['name'] in ('simultaneous-calls', 'disconnect-reconnect'):
                    sequence = uuid.uuid4().hex
                    write(runtime.root / 'device-input.json', dict(sequence=sequence, calls=[station for station, _ in pairs]))
                    applied = runtime.wait('批量呼叫未被设备确认', lambda: external_input(runtime, sequence), 10)
                    check('同一扫描周期批量呼叫已应用', applied.get('accepted') is True, applied)
                    write(evidence / ('batch-call-' + str(iteration) + '.json'), applied)
                    ids = [create(station, vehicle, trigger=False) for station, vehicle in pairs]
                    complete(ids)
                else:
                    for station, vehicle in pairs:
                        if not complete([create(station, vehicle)]): break
                if request['name'] == 'service-restart': break
                def closed():
                    memory = bytes.fromhex(fresh_device_state(runtime)['memory_hex'])
                    offsets = [station.get('offset', 0) for station in getattr(runtime, 'lab', {}).get('simulationTopology', {}).get('stations', [])] or [0, 2]
                    return all(memory[o] == 10 for o in offsets) and memory[610] == 0
                runtime.wait('PLC最终握手未闭合', closed, 30)
                check('本轮PLC握手闭合', True, '工位箱体/在线初态，H输出和倒料周期复位')
                runtime.scenario['completedRounds'] = iteration + 1
                if runtime.abort.wait(request['intervalSeconds']): raise InterruptedError('场景已中止。')
        if request['name'] == 'vendor-cancel-return':
            check('预期车端取消已形成独立故障结果', fault_observed, '取消任务、原命令和原路权已保留待核对')
        elif request['name'] != 'service-restart' and request['name'] not in (
                set(TIMEOUT_OBSTACLE_NAMES) - {'obstacle-timeout-safe-site'}):
            check('最终无残留路权', not business_leases(runtime.root), '所有业务资源租约已释放（进行中的充电会话除外）')
            commands = query(runtime.root, 'select TaskId,StepId,CommandId,Status,AttemptCount,VendorCommandId,LastError,PayloadJson from agv_command_outbox')
            commands = [c for c in commands if c['TaskId'] in tasks]
            write(evidence / 'outbox-readonly.json', commands)
            sent_window_commands = [command for command in commands if command['Status'] == 30]
            timeout_recoveries = []
            if request['name'] in ('obstacle-auto-detour', 'obstacle-auto-safety-return'):
                sent = [c for c in commands if c['Status'] == 30]
                cancelled = [c for c in commands if c['Status'] == 100]
                step_count = len({(c['TaskId'], c['StepId']) for c in sent})
                recovery_commands = [
                    c for c in sent
                    if str(c.get('VendorCommandId') or '').startswith('OBSTACLERECOVERY-')
                ]
                command_ok = (step_count == len(tasks) * 4 and
                              len(cancelled) == auto_detour_sends and
                              len(recovery_commands) == auto_detour_sends and
                              len({c['CommandId'] for c in commands}) == len(commands) and
                              all(c['VendorCommandId'] and
                                  (c['VendorCommandId'].startswith('OBSTACLERECOVERY-') or
                                   c['VendorCommandId'].startswith(c['StepId'])) for c in sent))
                label = ('道路中段安全点退回保留原步骤且只有一条有效替代命令'
                         if request['name'] == 'obstacle-auto-safety-return'
                         else '自动改道运输命令保留原步骤且只有一条有效替代命令')
                check(label, command_ok,
                      f'{len(sent)}条有效命令，{len(cancelled)}条已取消原命令，{len(tasks)}个任务')
            elif request['name'] == 'obstacle-timeout-safe-site':
                timeout_recoveries = query(runtime.root,
                    'select TaskId,StepId,VehicleId,OriginalVendorTaskId,NewVendorTaskId,State,SendAttemptedAt from transport_timeout_recovery')
                timeout_recoveries = [row for row in timeout_recoveries if row['TaskId'] in tasks]
                write(evidence / 'timeout-replacement-commands.json', timeout_recoveries)
                check('原窗口与一次超时恢复导航共同覆盖原业务步骤',
                      len(timeout_recoveries) == auto_detour_sends == len(tasks) and
                      complete_command_set(commands, tasks, timeout_recoveries), timeout_recoveries)
            else:
                step_count = len({(command['TaskId'], command['StepId']) for command in sent_window_commands})
                check('运输命令无重复或遗漏', complete_command_set(commands, tasks),
                      f'{len(sent_window_commands)}条已确认窗口命令 / {step_count}个业务运输步骤 / {len(tasks)}个任务')
            sends = []
            allowed_window_ids = {
                str(command['VendorCommandId'])
                for command in sent_window_commands
                if command.get('VendorCommandId')
            }
            allowed_step_ids = {
                str(value)
                for detail in (getattr(observer, 'seen_details', None) or getattr(observer, 'last_details', None) or {}).values()
                for step in detail.get('steps', [])
                for value in (step.get('stepId'), step.get('vendorTaskId'))
                if value
            }
            # Rolling windows have a distinct vendor task id per command while
            # the task detail normally exposes only the latest window. Include
            # every persisted sent command so accepted-window evidence is not
            # undercounted to four events per business task.
            allowed_step_ids.update(allowed_window_ids)
            timeout_roots = {row['NewVendorTaskId'] for row in timeout_recoveries}
            allowed_step_ids.update(timeout_roots)
            for vehicle in {t['vehicle'] for t in tasks.values()}:
                source = runtime.root / 'robots' / vehicle / 'device-events.jsonl'
                if not source.exists(): source = runtime.root / 'device-events.jsonl'
                for line in source.read_text(encoding='utf-8').splitlines():
                    event = json.loads(line)
                    requested_ids = {str(item.get('task_id')) for item in event.get('request', {}).get('move_task_list', [])}
                    # B07-FIX C1: DeviceJournal stamps wall-clock time.time(); `started` is the business
                    # clock. Convert before comparing, otherwise accelerated runs drop early windows.
                    event_business_time = runtime.clock.origin_seconds + max(
                        0.0, event['time'] - runtime.clock.origin_seconds) * runtime.clock.rate
                    if ((recovering or event_business_time >= started) and event['kind'] == 'robokit' and event['api'] == 3066 and
                            event['response'].get('ret_code') == 0 and (not allowed_step_ids or requested_ids & allowed_step_ids)):
                        sends.append(dict(vehicle=vehicle, **event))
            write(evidence / 'accepted-3066.json', sends)
            # B07-FIX V1: match receipts to commands instead of comparing totals. A command that the device
            # accepted and the backend later cancelled (obstacle detour / safety return) legitimately has a receipt.
            command_status = {str(c['VendorCommandId']): c['Status'] for c in commands if c.get('VendorCommandId')}
            command_status.update({str(root): 30 for root in timeout_roots if root})
            receipts, unmatched = {}, 0
            for event in sends:
                ids = {str(item.get('task_id')) for item in event.get('request', {}).get('move_task_list', [])} & set(command_status)
                if len(ids) != 1: unmatched += 1; continue
                key = next(iter(ids)); receipts[key] = receipts.get(key, 0) + 1
            receipts_ok = (unmatched == 0 and
                           all(receipts.get(k, 0) == 1 for k, status in command_status.items() if status == 30) and
                           all(count == 1 for count in receipts.values()))
            check('设备实际接收原运输及必要改道命令',
                  receipts_ok if command_status else len(sends) == len(sent_window_commands) + len(timeout_roots),
                  f'{len(sends)}次成功接收；已确认窗口{len(sent_window_commands)}次，超时恢复{len(timeout_roots)}次，自动改道{auto_detour_sends}次')
            if timeout_roots:
                observed_roots = [str(item.get('task_id')) for event in sends
                                  for item in event.get('request', {}).get('move_task_list', [])
                                  if str(item.get('task_id')) in timeout_roots]
                check('每个超时恢复编号只被车端接收一次',
                      len(observed_roots) == len(timeout_roots) and set(observed_roots) == timeout_roots,
                      observed_roots)
        if request['name'] in ('disconnect-reconnect', 'service-restart', 'plc-recovery', 'response-loss',
                               'road-block-recovery', 'vendor-cancel-return', 'obstacle-auto-detour',
                               'obstacle-auto-safety-return',
                               *TIMEOUT_OBSTACLE_NAMES):
            check('指定阶段故障实际触发', fault_observed, request['name'])
        if request['name'] == 'plc-delay':
            applied = control_applied_now(runtime.device(), plc_fault_controls)
            check('PLC延迟控制被设备确认', bool(applied), applied)
            runtime.control(dict(plc=dict(readyDelaySeconds=0, doneDelaySeconds=5)))
            runtime.controls['plc'] = {}
        check('采样期间资源容量与合成包络检查', observer.samples > 0 and not observer.violations, f'{observer.samples}次独立采样；相邻采样间线性包络检查')
        if getattr(observer, 'ownership_samples', 0):
            check('同一车辆没有同时承担多个未结束任务', not any(v['kind'] == 'vehicle-multiple-tasks' for v in observer.violations),
                  f'{observer.ownership_samples}次独立数据库核对')
        if automatic and tasks:
            automatic_rows = query(runtime.root, 'select TaskId,RequestedVehicleId,CreateRequestId from dispatch_task')
            selected = [row for row in automatic_rows if row['TaskId'] in tasks]
            check('仅PLC呼叫建单且车辆由正式调度选择', len(selected) == len(tasks) and all(not row['RequestedVehicleId'] and not row['CreateRequestId'] for row in selected),
                  '场景没有提交人工任务、指定车辆或修改调度数据库。')
        result_status = 'passed'
        message = ('恢复屏障已验证，保留活动任务待核对。' if request['name'] == 'service-restart' else
                   '已复现车端返程3066后段取消；任务、命令和路权按异常现场保留。' if request['name'] == 'vendor-cancel-return' else
                   '已完成道路中段3055直线后退或3051低速退站、到站核验和原车3066续接。'
                   if request['name'] == 'obstacle-auto-safety-return' else
                   '已完成安全点受控转移、候选重评估和原车续接。' if request['name'] == 'obstacle-timeout-safe-site' else
                   '已复现持续受阻后的车端状态7；原任务、载荷、PLC和路权按处置现场保留。'
                   if request['name'] in TIMEOUT_OBSTACLE_NAMES else
                   '场景及闭环检查通过。')
    except InterruptedError as error:
        result_status = 'aborted'; message = str(error)
        checks.append(dict(name='场景中止', status='unknown', detail=message))
    except Exception as error:
        message = str(error); checks.append(dict(name='场景执行', status='failed', detail=message))
    finally:
        all_details = getattr(observer, 'seen_details', {}) if observer else {}
        if observer and getattr(observer, 'last_details', None):
            write(evidence / 'last-task-details.json', observer.last_details)
        if all_details:
            write(evidence / 'all-task-details.json', all_details)
        write(evidence / 'task-summary.json', summarize_tasks(all_details))
        write(evidence / 'final-device-observation.json', runtime.device())
        from simulation_reports import guidance
        waiting_chain = guidance(getattr(observer, 'last_details', {}) or {}, runtime.device())
        version_end = runtime.version_comparison.refresh() if hasattr(runtime, 'version_comparison') else None
        if version_end: write(evidence / 'version-end.json', version_end)
        queue_metrics = read(evidence / 'queue-summary.json') if (evidence / 'queue-summary.json').exists() else {}
        report = dict(id=runtime.scenario['id'], name=request['name'], status=result_status, message=message, checks=checks,
            firstFailure=next((str(item['detail']) for item in checks if item['status'] == 'failed'), None),
            waitingChain=waiting_chain, versionComparison=version_start,
            versionComparisonEnd=version_end,
            executionConfiguration=execution_configuration,
            callMode='plc-automatic' if automatic else 'legacy-manual',
            completedBusinessCycle=result_status == 'passed' and bool(tasks) and completed_tasks == set(tasks) and not recovery_hold,
            recoveryHold=recovery_hold, faultApplied=fault_observed if request['name'] in
                ('disconnect-reconnect', 'service-restart', 'plc-delay', 'plc-recovery', 'vendor-cancel-return',
                 'response-loss', 'road-block-recovery', 'obstacle-auto-detour',
                 'obstacle-auto-safety-return', *TIMEOUT_OBSTACLE_NAMES) else None,
            taskSummary=summarize_tasks(all_details),
            callRows=runtime.scenario.get('callRows', []),
            simulationRate=runtime.simulation_rate,
            startedAt=started, finishedAt=runtime.clock.time(), evidenceDirectory=evidence.name,
            metrics=dict(taskCount=len(tasks), completedRounds=runtime.scenario['completedRounds'],
                         durationSeconds=runtime.clock.time()-started,
                         wallDurationSeconds=time.monotonic()-wall_started,
                         effectiveRate=(runtime.clock.time()-started)/(time.monotonic()-wall_started)
                             if time.monotonic() > wall_started else None,
                         samples=observer.samples if observer else 0, minimumSeparationMeters=observer.minimum if observer else None,
                         longestUnchangedStepSeconds=observer.max_wait if observer else 0,
                         longestUnchangedStepScope='active-tasks-only-v2',
                         obstacleAutoDetourSends=auto_detour_sends,
                         **{key:queue_metrics[key] for key in ('maximumWaitingTasks','maximumActiveTasks','maximumDuePlanCalls','maximumBusyWaitingTasks', 'downstreamAdmissionCapacity', 'maximumAdmissionWaitingTasks') if key in queue_metrics}),
            violations=observer.violations if observer else [], scope='正式后台 + 回环Robokit/S7合成模型；碰撞按车辆长宽与朝向的旋转矩形包络检查，并保留5厘米合成余量；超时可发现长期等待，不构成死锁/公平性证明；不代替现场固件、机械及安全验收。')
        write(evidence / 'report.json', report)
        files = [dict(path=p.name, sha256=hashlib.sha256(p.read_bytes()).hexdigest(), bytes=p.stat().st_size) for p in sorted(evidence.iterdir()) if p.is_file()]
        write(evidence / 'manifest.json', files)
        write(runtime.root / 'simulation-report.json', report)
        runtime.report = report
        runtime.history_cache = (0, [])
        runtime.guidance_cache = (runtime.clock.monotonic(), waiting_chain)
        runtime.scenario.update(status=result_status, message=message)
