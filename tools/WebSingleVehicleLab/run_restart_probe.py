"""Opt-in crash/recovery probe for one owned synthetic Service; never stops devices or other services."""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from lab_paths import read_ready, resolve_lab_root


def restart_owned_service(root):
    expected = (root / 'service/AgvDispatch.Service.exe').resolve()
    path = root / 'processes.json'
    processes = json.loads(path.read_text(encoding='utf-8-sig'))
    owned = [p for p in processes if Path(p['Path']).resolve() == expected]
    if len(owned) != 1:
        raise RuntimeError('No unique owned Service in the private process manifest.')
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000 | 1 | 0x100000, False, owned[0]['Id'])
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        image = ctypes.create_unicode_buffer(32768)
        count = wintypes.DWORD(len(image))
        if not kernel.QueryFullProcessImageNameW(handle, 0, image, ctypes.byref(count)) or Path(image.value).resolve() != expected:
            raise RuntimeError('PID image does not match this private Service; no process was stopped.')
        if not kernel.TerminateProcess(handle, 17) or kernel.WaitForSingleObject(handle, 5000) != 0:
            raise RuntimeError('Owned synthetic crash did not terminate; preserving evidence.')
    finally:
        kernel.CloseHandle(handle)
    env = os.environ.copy()
    env['AGV_DISPATCH_DATABASE_PATH'] = str(root / 'data/agv-dispatch.db')
    stamp = time.strftime('%Y%m%d-%H%M%S')
    with (root / f'service-probe-{stamp}-stdout.log').open('ab') as out, (root / f'service-probe-{stamp}-stderr.log').open('ab') as err:
        process = subprocess.Popen([str(expected)], cwd=expected.parent, env=env, stdout=out, stderr=err, creationflags=subprocess.CREATE_NO_WINDOW)
    previous = owned[0]['Id']
    owned[0]['Id'] = process.pid
    path.write_text(json.dumps(processes, ensure_ascii=False, indent=2), encoding='utf-8')
    return dict(previousPid=previous, restartedPid=process.pid, executable=str(expected))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    parser.add_argument('--confirm-synthetic-crash-test', action='store_true')
    parser.add_argument('--resume-evidence', help='Continue the same held probe after inspecting a rejected recovery; never creates another task.')
    parser.add_argument('--review-new-boot', action='store_true', help='Explicitly review a newer held boot after updating this same synthetic Service; preserve prior boot evidence.')
    args = parser.parse_args()
    if not args.confirm_synthetic_crash_test or os.name != 'nt':
        raise RuntimeError('Explicit Windows synthetic crash-test opt-in required.')
    labroot = resolve_lab_root(['--root', args.root])
    ready = read_ready(labroot)
    root = Path(ready['root']).resolve()
    lab = json.loads((root / 'lab.json').read_text(encoding='utf-8-sig'))
    settings = json.loads((root / 'service/appsettings.json').read_text(encoding='utf-8-sig'))
    if lab.get('simulationOnly') is not True or settings['HandshakeExecution']['PlcContractVersion'] != 'plc-28-stage-v2' or \
            any(p['IpAddress'] != '127.0.0.1' for p in [settings['Plc'], *settings['Agv']['Robots']]):
        raise RuntimeError('Only an explicitly synthetic loopback 28-stage instance is eligible.')
    communication_path = root / 'data/communication-settings.json'
    if communication_path.exists():
        communication = json.loads(communication_path.read_text(encoding='utf-8-sig'))['Configuration']
        if any(p['IpAddress'] != '127.0.0.1' for p in [*communication['Plcs'], *communication['Agvs']]):
            raise RuntimeError('Saved communication settings are not loopback-only.')
    base = ready['url'].rstrip('/')
    evidence = Path(args.resume_evidence).resolve() if args.resume_evidence else root / ('restart-probe-' + time.strftime('%Y%m%d-%H%M%S'))
    if not evidence.resolve().is_relative_to(root) or not evidence.name.startswith('restart-probe-'):
        raise RuntimeError('Probe evidence must stay in this private instance.')
    if not args.resume_evidence:
        evidence.mkdir()

    def api(path, data=None):
        request = urllib.request.Request(base + path, data=json.dumps(data).encode() if data is not None else None, headers={'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=40) as response:
            value = json.load(response)
        if isinstance(value, dict) and value.get('accepted') is False:
            save('rejected-' + str(time.time_ns()) + '.json', dict(path=path, result=value))
            raise RuntimeError(f'{path}: {value}')
        return value

    def save(name, value):
        destination = evidence / name
        if destination.exists():
            destination = destination.with_name(destination.stem + '-' + str(time.time_ns()) + destination.suffix)
        destination.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

    def wait(label, predicate, seconds=90):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            try:
                value = predicate()
                if value:
                    return value
            except (urllib.error.URLError, TimeoutError):
                pass
            time.sleep(.4)
        raise RuntimeError('Timed out: ' + label)

    if not args.resume_evidence and any(t['stage'] != 'Completed' for t in api('/api/tasks')['items']):
        raise RuntimeError('Finish prior business before creating this one probe task.')
    before_recovery = api('/api/recovery')
    snapshot = api('/api/snapshot')
    if snapshot['status']['namedPipe']['pipeName'] != settings['Security']['PipeName'] or settings['Security']['PipeName'] != ready['pipe']:
        raise RuntimeError('Running backend does not match this laboratory pipe.')
    if not args.resume_evidence and not before_recovery['released']:
        raise RuntimeError('Review idle startup before this probe.')
    if args.resume_evidence:
        task = json.loads((evidence / 'creation.json').read_text(encoding='utf-8'))['taskId']
        boot = json.loads((evidence / 'boot-barrier.json').read_text(encoding='utf-8'))
        if before_recovery['released'] or before_recovery['sessionId'] != boot['sessionId'] and not args.review_new_boot:
            raise RuntimeError('Original probe recovery session no longer held; inspect it rather than applying stale decisions.')
        if before_recovery['sessionId'] != boot['sessionId']:
            save('additional-boot-' + str(time.time_ns()) + '.json', before_recovery)
    else:
        state = json.loads((root / 'device-state.json').read_text(encoding='utf-8'))
        memory = bytes.fromhex(state['memory_hex'])
        if state['loaded'] or state['current_task'] or memory[2] != 10 or memory[610] != 0:
            raise RuntimeError('CNC-002 synthetic idle baseline is not closed.')
        (root / 'device-input.json').write_text(json.dumps(dict(sequence=uuid.uuid4().hex, call='CNC-002')), encoding='utf-8')
        options = wait('CNC-002 fresh call', lambda: (lambda o: o if o['available'] and any(s['stationId'] == 'CNC-002' and s['available'] for s in o['stations']) else None)(api('/api/task-options')))
        created = api('/api/tasks', dict(requestId=uuid.uuid4().hex, stationId='CNC-002', requestedVehicleId='AGV-LOCAL-01', priority=0,
            mapId=options['mapId'], mapVersionId=options['mapVersionId'], parameterRevision=options['parameterRevision']))
        task = created['taskId']
        save('creation.json', created)
        detail = wait('first original transport executing', lambda: (lambda d: d if d['steps'][0]['status'] == 'Executing' else None)(api('/api/tasks/' + task)))
        save('before-crash.json', detail)
        save('owned-restart.json', restart_owned_service(root))
        print('Restarted owned synthetic Service during original pickup: ' + task, flush=True)
        status = wait('new recovery session', lambda: (lambda s: s if s['sessionId'] != before_recovery['sessionId'] and not s['released'] else None)(api('/api/recovery')))
        save('boot-barrier.json', status)
    # Let the already-issued transport finish on the independent twin. No extra command or position is injected.
    target = next(n['agvSiteId'] for n in json.loads((root / 'navigation.agvmap.json').read_text(encoding='utf-8'))['nodes'] if n['id'] == '取货B')
    def arrived():
        s = json.loads((root / 'device-state.json').read_text(encoding='utf-8'))
        return s if s['station'] == target and s['current_task'] is None and s['loaded'] else None
    save('physical-twin-pickup.json', wait('original twin pickup finishes while backend is held', arrived))
    def stopped_backend():
        snapshot = api('/api/snapshot')
        expected_pid = next(p['Id'] for p in json.loads((root / 'processes.json').read_text(encoding='utf-8-sig'))
            if Path(p['Path']).resolve() == (root / 'service/AgvDispatch.Service.exe').resolve())
        if not snapshot.get('connected') or snapshot.get('status', {}).get('service', {}).get('processId') != expected_pid:
            return None
        vehicle = next(v for v in snapshot['status']['agvs'] if v['vehicleId'] == 'AGV-LOCAL-01')
        if not vehicle.get('lastSuccessfulPollAt'):
            return None
        polled = datetime.fromisoformat(vehicle['lastSuccessfulPollAt'])
        started = datetime.fromisoformat(snapshot['status']['service']['startedAt'])
        return snapshot if vehicle['quality'] == 'Good' and vehicle['connection'] == 'Online' and vehicle['currentStation'] == target and \
            polled > started and 0 <= (datetime.now(timezone.utc) - polled).total_seconds() < 3 and \
            vehicle['navigationState'] == '0' and all(vehicle.get(k) == 0 for k in ('velocityX', 'velocityY', 'angularVelocity')) else None
    save('fresh-stopped-backend.json', wait('fresh stopped backend observation', stopped_backend))
    sent_before_review = sum(1 for line in (root / 'device-events.jsonl').read_text(encoding='utf-8').splitlines()
        if (lambda e: e['kind'] == 'robokit' and e['api'] == 3066 and e['response'].get('ret_code') == 0)(json.loads(line)))
    for attempt in range(8):
        status = api('/api/recovery')
        save(f'review-{attempt}.json', status)
        pending = [item for item in status['items'] if not item['verified']]
        if not pending:
            break
        for item in pending:
            if item['taskId'] not in ('__SITE__', task):
                raise RuntimeError('Unexpected task in recovery; no automatic decision applied.')
            decision = 20 if item['taskId'] == task and item['stepText'] == '前往取料站' else 10
            result = api('/api/recovery/decisions', dict(sessionId=status['sessionId'], ticketId=item['ticketId'], revision=item['revision'],
                decision=decision, materialReference='SIM-ONLY:原任务原车辆已到取货B，原JackLoad完成，原箱体无箱且虚拟车带箱；全场其他工位保持原基线',
                reason='独立回环实例原运输查询及设备快照核对；不构造现场证据', vehicleAndLocationConfirmed=True,
                loadAndOwnershipConfirmed=True, plcCycleConfirmed=True, handshakePendingWriteConfirmed=False))
            save(f'decision-{attempt}-{item["ticketId"]}.json', result)
        time.sleep(.5)
    status = api('/api/recovery')
    if any(not item['verified'] for item in status['items']):
        raise RuntimeError('Recovery remains unverified; no release attempted.')
    sent_after_review = sum(1 for line in (root / 'device-events.jsonl').read_text(encoding='utf-8').splitlines()
        if (lambda e: e['kind'] == 'robokit' and e['api'] == 3066 and e['response'].get('ret_code') == 0)(json.loads(line)))
    if sent_after_review != sent_before_review:
        raise RuntimeError('A new transport was sent while recovery was held.')
    save('release.json', api('/api/recovery/release', dict(sessionId=status['sessionId'], reason='SIM-ONLY:原运输机械结果及下一握手节点已逐项核对，原编号保留')))
    print('Recovery reviewed through normal APIs; no transport resent while held.', flush=True)
    detail = wait('original task completes after review', lambda: (lambda d: d if d['task']['stage'] == 'Completed' else None)(api('/api/tasks/' + task)), 600)
    save('completed.json', detail)
    if sum(s['status'] == 'Completed' for s in detail['steps']) != 9:
        raise RuntimeError('Original nine steps did not complete.')
    save('result.json', dict(taskId=task, crashDuring='original GoToPickup transport', freshBootBarrier=True,
        noNewTransportWhileHeld=True, mechanicalCompletionReviewed=True, originalTaskCompleted=True,
        boundary='synthetic loopback PLC and Robokit twin; no physical commissioning claim'))
    print('PASS active Service restart and original-task recovery. Evidence: ' + str(evidence), flush=True)


if __name__ == '__main__':
    main()
