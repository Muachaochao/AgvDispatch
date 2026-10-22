"""Exercise current 28-stage loopback instance via normal Web APIs and virtual sensor events.

No database writes, task-state injection, device relocation or automatic recovery of active tasks.
"""
import argparse
import json
import re
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path
from lab_paths import read_ready, resolve_lab_root


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    parser.add_argument('--rounds', type=int, default=2)
    parser.add_argument('--station', choices=['CNC-001', 'CNC-002'], default='CNC-001')
    parser.add_argument('--release-idle-recovery', action='store_true', help='Review only an idle, synthetic __SITE__ boot ticket.')
    args = parser.parse_args()
    if not 1 <= args.rounds <= 10:
        raise RuntimeError('Choose 1..10 sequential test rounds.')
    labroot = resolve_lab_root(['--root', args.root])
    ready = read_ready(labroot)
    root = Path(ready['root'])
    lab = json.loads((root / 'lab.json').read_text(encoding='utf-8-sig'))
    settings = json.loads((root / 'service/appsettings.json').read_text(encoding='utf-8-sig'))
    if lab.get('simulationOnly') is not True or settings['HandshakeExecution']['PlcContractVersion'] != 'plc-28-stage-v2':
        raise RuntimeError('Explicit new synthetic 28-stage instance required.')
    if any(node['IpAddress'] != '127.0.0.1' for node in [settings['Plc'], *settings['Agv']['Robots']]):
        raise RuntimeError('Only loopback device endpoints are supported.')
    communication_path = root / 'data/communication-settings.json'
    if communication_path.exists():
        communication = json.loads(communication_path.read_text(encoding='utf-8-sig'))['Configuration']
        if any(p['IpAddress'] != '127.0.0.1' for p in [*communication['Plcs'], *communication['Agvs']]):
            raise RuntimeError('Saved communication settings include a non-loopback device; probe refused.')
    evidence = root / ('rounds-' + time.strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6])
    evidence.mkdir()
    base = ready['url'].rstrip('/')

    def api(path, request=None):
        payload = json.dumps(request, ensure_ascii=False).encode() if request is not None else None
        query = urllib.request.Request(base + path, data=payload, headers={'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(query, timeout=40) as response:
                result = json.load(response)
        except urllib.error.HTTPError as ex:
            (evidence / 'http-error.txt').write_text(path + '\n' + ex.read().decode(), encoding='utf-8')
            raise
        if isinstance(result, dict) and result.get('accepted') is False:
            raise RuntimeError(f'{path}: {result}')
        return result

    def save(name, value):
        (evidence / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

    def device():
        return json.loads((root / 'device-state.json').read_text(encoding='utf-8'))

    def wait(label, predicate, seconds=30):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            result = predicate()
            if result:
                return result
            time.sleep(.3)
        raise RuntimeError('Timed out: ' + label)

    snapshot = api('/api/snapshot')
    save('initial-backend.json', snapshot)
    if snapshot['status']['execution']['configuredContract'] != 'plc-28-stage-v2':
        raise RuntimeError('Running backend is not using the explicit 28-stage contract.')
    if snapshot['status']['namedPipe']['pipeName'] != settings['Security']['PipeName'] or settings['Security']['PipeName'] != ready['pipe']:
        raise RuntimeError('The running backend does not belong to this private pipe.')
    if args.release_idle_recovery:
        state = device()
        tasks = api('/api/tasks')['items']
        memory = bytes.fromhex(state['memory_hex'])
        if state['loaded'] or state['current_task'] or any(v in (1, 2, 3) for v in state['tasks'].values()) or \
                any(t['stage'] != 'Completed' for t in tasks) or memory[610] != 0 or \
                any(memory[offset] != 10 for offset in (0, 2)):
            raise RuntimeError('Idle baseline not verified; active-task recovery requires individual review.')
        recovery = api('/api/recovery')
        save('boot-recovery-before.json', recovery)
        if not recovery['released']:
            if any(t['taskId'] != '__SITE__' for t in recovery['items']):
                raise RuntimeError('Only idle site boot review is allowed by this runner.')
            for ticket in recovery['items']:
                api('/api/recovery/decisions', dict(sessionId=recovery['sessionId'], ticketId=ticket['ticketId'],
                    revision=ticket['revision'], decision=10, materialReference='SIM-ONLY:虚拟车空载静止、原PLC低基线、无活动任务；不是现场核对',
                    reason='新独立合成设备启动核对', vehicleAndLocationConfirmed=True, loadAndOwnershipConfirmed=True, plcCycleConfirmed=True))
            api('/api/recovery/release', dict(sessionId=recovery['sessionId'], reason='SIM-ONLY独立实例启动基线已核对'))
        save('boot-recovery-after.json', api('/api/recovery'))
    if not api('/api/recovery')['released'] or api('/api/maps/switch')['mode'] != 'Running':
        raise RuntimeError('Use normal map/recovery review before starting test rounds.')

    # Read the maintained table rather than silently carrying a second, drifting protocol definition.
    source = (Path(__file__).resolve().parents[1] / 'PlcHandshakeLab/Sequence.cs').read_text(encoding='utf-8-sig')
    patterns = re.findall(r'new\("([^"\n]+)", "[^"\n]+", "([01?]{10})"\)', source)
    if len(patterns) != 28:
        raise RuntimeError('Cannot identify the maintained 28-stage table.')
    offset = 0 if args.station == 'CNC-001' else 2
    addresses = [(offset, 0), (offset, 1), (offset, 4), (offset, 5), (offset, 6), (offset, 7),
                 (610, 0), (610, 1), (610, 2), (610, 3)]
    rounds = []
    for index in range(1, args.rounds + 1):
        state = device()
        initial = bytes.fromhex(state['memory_hex'])
        if initial[offset] != 10 or initial[610] != 0:
            raise RuntimeError('Previous PLC round has not closed; refusing a new call.')
        start_time = time.time()
        event = dict(sequence='round-' + uuid.uuid4().hex, call=args.station)
        temporary = root / ('input-' + uuid.uuid4().hex + '.tmp')
        temporary.write_text(json.dumps(event), encoding='utf-8')
        temporary.replace(root / 'device-input.json')

        def available():
            options = api('/api/task-options')
            return options if options['available'] and any(s['stationId'] == args.station and s['available'] for s in options['stations']) else None

        options = wait('fresh call accepted in task options', available)
        request = dict(requestId=uuid.uuid4().hex, stationId=args.station, requestedVehicleId='AGV-LOCAL-01', priority=0,
                       mapId=options['mapId'], mapVersionId=options['mapVersionId'], parameterRevision=options['parameterRevision'])
        created = api('/api/tasks', request)
        task_id = created['taskId']
        save(f'round-{index}-creation.json', dict(request=request, response=created))
        print(f'ROUND {index} task={task_id}', flush=True)
        deadline = time.monotonic() + 600
        signature = None
        while True:
            detail = api('/api/tasks/' + task_id)
            current = (detail['task']['stage'], tuple((s['status'], s.get('errorMessage')) for s in detail['steps']))
            if current != signature:
                signature = current
                save(f'round-{index}-progress-{time.time_ns()}.json', detail)
                print(f'ROUND {index}: {current}', flush=True)
            if detail['task']['stage'] == 'Completed':
                break
            if detail['task']['stage'] in ('Faulted', 'Cancelled') or time.monotonic() > deadline:
                save(f'round-{index}-failure.json', detail)
                raise RuntimeError(f'Task {task_id} did not complete normally; original evidence retained.')
            time.sleep(.7)
        save(f'round-{index}-completed.json', detail)
        wait('shared and station PLC closed', lambda: (lambda m: m[offset] == 10 and m[610] == 0)(bytes.fromhex(device()['memory_hex'])))
        events = [json.loads(line) for line in (root / 'device-events.jsonl').read_text(encoding='utf-8').splitlines()]
        events = [e for e in events if e['time'] >= start_time]
        save(f'round-{index}-device-events.json', events)
        bits = [bool(initial[b] & (1 << bit)) for b, bit in addresses]
        history = [bits[:]]
        for event in events:
            if event['kind'] == 'plc-bit':
                for at, (byte, bit) in enumerate(addresses):
                    if event['address'] == f'DB1.DBX{byte}.{bit}':
                        bits[at] = event['value']
                history.append(bits[:])
        cursor = 0
        matched = []
        for stage, (name, pattern) in enumerate(patterns, 1):
            while cursor < len(history) and not all(p == '?' or (p == '1') == v for p, v in zip(pattern, history[cursor])):
                cursor += 1
            if cursor == len(history):
                raise RuntimeError(f'Round{index}: stage{stage} ({name}) signal projection was not observed.')
            matched.append(dict(stage=stage, name=name, eventStateIndex=cursor))
        sends = [e for e in events if e['kind'] == 'robokit' and e['api'] == 3066 and e['response'].get('ret_code') == 0]
        if len(sends) != 4 or sum(s['status'] == 'Completed' for s in detail['steps']) != 9:
            raise RuntimeError('Expected four real TCP transportation submissions and nine completed backend steps.')
        cycle_low = next(e for e in events if e['kind'] == 'plc-bit' and e['address'] == 'DB1.DBX610.1' and e['value'] is False)
        if sends[-1]['time'] >= cycle_low['time']:
            raise RuntimeError('Return transport must start before physical exit closes the cycle.')
        result = dict(round=index, taskId=task_id, stage=detail['task']['stage'], signalProjection=matched,
                      accepted3066=len(sends), backendCompletedSteps=9, returnAcceptedBeforeCycleClear=True)
        rounds.append(result)
        save('result.json', dict(boundary='loopback Robokit/S7 twins; stable equal-level stages require task/position evidence, not separate sensor edges', rounds=rounds))
        print(f'PASS ROUND {index}: 28 signal projections, 9 backend steps, 4 TCP transports', flush=True)
    print('Evidence: ' + str(evidence), flush=True)


if __name__ == '__main__':
    main()
