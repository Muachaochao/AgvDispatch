"""Run two-car Web/S7/TCP integration on an explicitly isolated synthetic instance."""
import argparse
import hashlib
import json
import sqlite3
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
from lab_paths import resolve_lab_root, read_ready
from run_restart_probe import restart_owned_service


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    parser.add_argument('--setup', action='store_true')
    parser.add_argument('--resume', help='Continue the same preserved two-car evidence; never create replacement tasks')
    args = parser.parse_args()
    root = resolve_lab_root(['--root', args.root])
    lab = json.loads((root / 'lab.json').read_text(encoding='utf-8'))
    settings = json.loads((root / 'service/appsettings.json').read_text(encoding='utf-8'))
    if not lab.get('simulationOnly') or not lab.get('twoVehicleSimulation'):
        raise RuntimeError('Explicit two-car isolated simulation required')
    if any(r['IpAddress'] != '127.0.0.1' for r in [settings['Plc'], *settings['Agv']['Robots']]):
        raise RuntimeError('Loopback devices only')
    if (root / 'data/communication-settings.json').exists():
        active = json.loads((root / 'data/communication-settings.json').read_text(encoding='utf-8-sig'))['Configuration']
        if any(r['IpAddress'] != '127.0.0.1' for r in [*active['Plcs'], *active['Agvs']]):
            raise RuntimeError('Non-loopback saved communication configuration')
    ready = read_ready(root)
    if settings['Security']['PipeName'] != ready['pipe'] or Path(settings['Database']['Path']).resolve() != root / 'data/agv-dispatch.db':
        raise RuntimeError('Private pipe/database mismatch')
    evidence = Path(args.resume).resolve() if args.resume else root / (('setup-' if args.setup else 'two-car-') + time.strftime('%Y%m%d-%H%M%S'))
    if not evidence.is_relative_to(root) or not evidence.name.startswith(('setup-', 'two-car-')):
        raise RuntimeError('Evidence must stay inside this selected private lab')
    if not args.resume:
        evidence.mkdir()
    def save(name, value):
        (evidence / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    def api(path, data=None, allow_rejection=False):
        req = urllib.request.Request(ready['url'].rstrip('/') + path,
            data=json.dumps(data).encode() if data is not None else None,
            headers={'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(req, timeout=25) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            save('http-error-' + str(time.time_ns()) + '.json', dict(path=path, response=error.read().decode()))
            raise
        if data is not None:
            save('operation-' + str(time.time_ns()) + '.json', dict(path=path, request=data, response=result))
        if not allow_rejection and isinstance(result, dict) and result.get('accepted') is False:
            raise RuntimeError(f'{path}: {result}')
        return result
    def wait(label, condition, timeout=60):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = condition()
            if value:
                return value
            time.sleep(.5)
        raise RuntimeError('Timeout: ' + label)
    def state():
        return json.loads((root / 'multi-state.json').read_text(encoding='utf-8'))
    def event(filename, **data):
        temporary = root / ('input-' + uuid.uuid4().hex + '.tmp')
        temporary.write_text(json.dumps(dict(sequence=uuid.uuid4().hex, **data)), encoding='utf-8')
        temporary.replace(root / filename)
    def idle_review(review_attempt=0):
        s = state()
        memory = bytes.fromhex(s['memory_hex'])
        if any(c['loaded'] or c['current_task'] or any(t in (1,2,3) for t in c['tasks'].values()) for c in s['cars'].values()) or memory[610] != 0 or any(memory[o] & 254 != 10 for o in (0,2)) or any(t['stage'] != 'Completed' for t in api('/api/tasks')['items']):
            raise RuntimeError('Not an idle two-car baseline')
        recovery = api('/api/recovery')
        if not recovery['released']:
            if any(t['taskId'] != '__SITE__' for t in recovery['items']):
                raise RuntimeError('Active-task recovery requires explicit individual review')
            for ticket in recovery['items']:
                decision = api('/api/recovery/decisions', dict(sessionId=recovery['sessionId'], ticketId=ticket['ticketId'], revision=ticket['revision'],
                    decision=10, materialReference=f'SIM-ONLY两车空载静止，无任务及H责任；工位字节{memory[0]}/{memory[2]}，P呼叫是尚未受理需求。',
                    reason='isolated synthetic boot baseline', vehicleAndLocationConfirmed=True, loadAndOwnershipConfirmed=True, plcCycleConfirmed=True), allow_rejection=True)
                if decision.get('accepted') is False:
                    if review_attempt < 2 and '已变化' in decision.get('message',''):
                        time.sleep(.5)
                        return idle_review(review_attempt+1)
                    raise RuntimeError('Idle baseline review rejected: '+str(decision))
            api('/api/recovery/release', dict(sessionId=recovery['sessionId'], reason='SIM-ONLY two-car idle baseline verified'))
    def connected():
        snap = api('/api/snapshot')
        expected_pid = next(p['Id'] for p in json.loads((root/'processes.json').read_text(encoding='utf-8'))
            if Path(p['Path']).resolve() == root/'service/AgvDispatch.Service.exe')
        return snap if snap.get('connected') and len(snap.get('status',{}).get('agvs',[])) == 2 and \
            snap['status']['service']['processId'] == expected_pid else None
    initial = wait('two fresh backend cars', connected)
    save('initial-snapshot.json', initial)
    if initial['status']['namedPipe']['pipeName'] != ready['pipe']:
        raise RuntimeError('Wrong running backend')
    if args.setup:
        metadata = json.loads((root / 'map-operation-results.json').read_text(encoding='utf-8'))
        target = {k: metadata[k] for k in ('mapId', 'versionId')}
        switch = api('/api/maps/switch')
        active_target = any(m['mapId'] == target['mapId'] and m['isActive'] and m['activeVersionId'] == target['versionId'] for m in api('/api/maps'))
        if not switch.get('current') and not active_target:
            api('/api/maps/switch/begin', target)
            switch = api('/api/maps/switch')
        if switch.get('current') and any(switch['current'][k] != v for k,v in target.items()):
            raise RuntimeError('Another map switch is in progress; refusing to replace it')
        name = metadata['published']['workspace']['project']['name']
        if switch.get('current') and switch['current']['phase'] == 'Draining':
            api('/api/maps/activate', dict(**target, confirmationText='ACTIVATE ' + name,
                siteSafetyConfirmed=True, vehicleMapCompatibilityConfirmed=True,
                evidence='SIM-ONLY two loopback cars use identical synthetic map bytes', expectedReleaseId=switch['current']['releaseId']))
        if switch.get('current'):
            wait('fresh map observations permit normal resume', lambda: api('/api/maps/switch/resume',
                dict(releaseId=switch['current']['releaseId'], siteSafetyConfirmed=True,
                    vehicleMapCompatibilityConfirmed=True, evidence='SIM-ONLY idle virtual map verified'), allow_rejection=True).get('accepted'))
        # Refresh the site ticket after the map execution pointer has changed.
        # A stale revision is still rejected rather than force-approved.
        api('/api/recovery')
        time.sleep(.5)
        idle_review()
        binding = api('/api/map-bindings')
        md5 = hashlib.md5((root / 'navigation.agvmap.json').read_bytes()).hexdigest()
        api('/api/map-bindings', dict(expectedRevision=binding['revision'], bindings=[dict(vehicleId=r['VehicleId'],
            localMapId=target['mapId'], localMapVersion=target['versionId'], robotMap='single_vehicle_lab',
            robotMapMd5=md5, coordinatesVerified=True, confirmationNote='SIM-ONLY same synthetic map bytes and coordinates; NOT field calibration') for r in settings['Agv']['Robots']]))
        save('owned-setup-restart.json', restart_owned_service(root))
        time.sleep(3)
        wait('private Service restarted', connected)
        idle_review()
        if not any(m['mapId'] == target['mapId'] and m['isActive'] and m['activeVersionId'] == target['versionId'] for m in api('/api/maps')):
            raise RuntimeError('Published map did not remain active after setup restart')
        print('SETUP PASS ' + str(evidence), flush=True)
        return
    if lab.get('waitingBayProtection'):
        raise RuntimeError('Protected single-bay fixture: use run_waiting_bay.py. This historical staggered runner requires the follower to reach an unprotected waiting bay.')
    if not args.resume:
        idle_review()
    if api('/api/maps/switch')['mode'] != 'Running':
        raise RuntimeError('Map execution not Running')
    def create(station, vehicle):
        event('device-input.json', call=station)
        def available():
            options = api('/api/task-options')
            return options if options['available'] and any(s['stationId'] == station and s['available'] for s in options['stations']) else None
        options = wait('new PLC call ' + station, available)
        request = dict(requestId=uuid.uuid4().hex, stationId=station, requestedVehicleId=vehicle, priority=0,
            mapId=options['mapId'], mapVersionId=options['mapVersionId'], parameterRevision=options['parameterRevision'])
        result = api('/api/tasks', request)
        print('CREATED ' + vehicle + ' ' + result['taskId'], flush=True)
        return result['taskId']
    def detail(task):
        return api('/api/tasks/' + task)
    if args.resume:
        original = json.loads((evidence / 'task-identities.json').read_text(encoding='utf-8'))
        task1, task2, started = original['first'], original['second'], original['startedAt']
        disconnected_at = original['disconnectedAt']
        if not state()['cars']['AGV-LOCAL-01']['offline']:
            raise RuntimeError('Resume expects the original owner still disconnected')
    else:
        started = time.time()
        task1 = create('CNC-001', 'AGV-LOCAL-01')
        # Staggered admission prevents this fixture's SINGLE physical waiting bay
        # being occupied by two cars before either owns the dumping equipment.
        # Simultaneous approach requires a separately commissioned holding-bay resource.
        wait('first car physically at dumper', lambda: state()['cars']['AGV-LOCAL-01']['station'] == 'TEST-LM108', 240)
        wait('first current PLC cycle notified', lambda: bool(bytes.fromhex(state()['memory_hex'])[610] & 2), 35)
        disconnected_at = time.time()
        event('network-input.json', vehicleId='AGV-LOCAL-01', offline=True)
        task2 = create('CNC-002', 'AGV-LOCAL-02')
        save('task-identities.json', dict(first=task1, second=task2, startedAt=started, disconnectedAt=disconnected_at))
    signatures = {}
    observed_wait = False
    deadline = time.monotonic() + 650
    offline_at = None
    reconnected = False
    finished = {}
    while time.monotonic() < deadline:
        s = state()
        d1, d2 = detail(task1), detail(task2)
        with (evidence / 'observations.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(dict(time=time.time(), devices=s, firstStage=d1['task']['stage'], secondStage=d2['task']['stage'])) + '\n')
        for task, d in ((task1,d1),(task2,d2)):
            signature = (d['task']['stage'], tuple(x['status'] for x in d['steps']))
            if signatures.get(task) != signature:
                save('progress-' + str(time.time_ns()) + '.json', dict(detail=d, devices=s))
                signatures[task] = signature
                print(task + ' ' + str(signature), flush=True)
            if d['task']['stage'] in ('Faulted','Cancelled'):
                raise RuntimeError('Unexpected terminal task state')
        waiting = api('/api/traffic/waiting', {})
        # Capture all wait API observations; no assumptions about DTO row names.
        if s['cars']['AGV-LOCAL-02']['station'] == 'TEST-LM107' and d1['task']['stage'] != 'Completed':
            if not observed_wait:
                observed_wait = True
                save('follower-at-waiting.json', dict(first=d1, second=d2, devices=s, waiting=waiting))
                offline_at = time.monotonic()
                print('Owner remains disconnected; follower reached shared equipment wait', flush=True)
        if offline_at is not None and not reconnected and time.monotonic() - offline_at >= 8:
            save('during-disconnect.json', dict(first=d1, second=d2, devices=s, snapshot=api('/api/snapshot'), waiting=waiting))
            if s['cars']['AGV-LOCAL-02']['station'] == 'TEST-LM108' or not (bytes.fromhex(s['memory_hex'])[610] & 2):
                raise RuntimeError('Follower entered or cycle was cleared during owner disconnect')
            with sqlite3.connect((root / 'data/agv-dispatch.db').as_uri() + '?mode=ro', uri=True) as db:
                db.row_factory = sqlite3.Row
                commands = [dict(row) for row in db.execute('select StepId,Status,AttemptCount from agv_command_outbox where TaskId=?', (task1,))]
            save('offline-original-commands.json', commands)
            pending_return = next((c for c in commands if c['StepId'] == d1['steps'][6]['stepId']), None)
            if pending_return is not None and pending_return['AttemptCount'] != 0:
                raise RuntimeError('Known-offline owner consumed a return send attempt')
            event('network-input.json', vehicleId='AGV-LOCAL-01', offline=False)
            reconnected = True
            print('NETWORK restored; original task may resume from physical observations', flush=True)
        if all(d['task']['stage'] == 'Completed' for d in (d1,d2)):
            finished = {task1:d1, task2:d2}
            break
        time.sleep(.8)
    if not finished or not observed_wait or not reconnected:
        raise RuntimeError('Two-car completion / real equipment wait / disconnect evidence incomplete')
    save('completed.json', finished)
    final = wait('both origins and shared PLC closed', lambda: (lambda s:s if all(bytes.fromhex(s['memory_hex'])[o] == 10 for o in (0,2)) and bytes.fromhex(s['memory_hex'])[610] == 0 else None)(state()))
    save('final-devices.json', final)
    for vehicle in ('AGV-LOCAL-01','AGV-LOCAL-02'):
        events = [json.loads(line) for line in (root / 'robots' / vehicle / 'device-events.jsonl').read_text(encoding='utf-8').splitlines()]
        accepted = [e for e in events if e['time'] >= started and e['kind'] == 'robokit' and e['api'] == 3066 and e['response'].get('ret_code') == 0]
        save(vehicle + '-accepted-3066.json', accepted)
        if len(accepted) != 4:
            raise RuntimeError(vehicle + ': expected exactly 4 accepted original transports')
    for d in finished.values():
        if len(d['steps']) != 9 or any(x['status'] != 'Completed' for x in d['steps']):
            raise RuntimeError('Nine backend steps did not complete')
    with sqlite3.connect((root / 'data/agv-dispatch.db').as_uri() + '?mode=ro', uri=True) as db:
        leases = db.execute('select count(*) from traffic_resource_lease').fetchone()[0]
    if leases != 0:
        raise RuntimeError('Unexplained final resource leases remain')
    result = dict(passed=True, tasks=[task1,task2], completedSteps=18, accepted3066=8,
        finalLeases=leases, ownerDisconnectedAt=disconnected_at, followerWaitBeforeReconnectSeconds=8, observedFollowerWaiting=True,
        boundary='Synthetic two TCP cars/shared S7/production Web+Service. Staggered single-bay approach; no field or geometric collision certification.')
    save('result.json', result)
    print('PASS ' + str(evidence), flush=True)


if __name__ == '__main__':
    main()
