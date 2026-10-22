"""Cancel a loaded owner, verify follower exclusion and private Service restart hold.

Intentionally leaves cancelled/recovery tasks frozen with original PLC/resource evidence.
Only run after the two normal missions have completed on this synthetic instance.
"""
import argparse
import json
import sqlite3
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
from lab_paths import resolve_lab_root, read_ready
from run_restart_probe import restart_owned_service


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    parser.add_argument('--confirm-synthetic-crash-test', action='store_true')
    args = parser.parse_args()
    root = resolve_lab_root(['--root', args.root])
    lab = json.loads((root/'lab.json').read_text(encoding='utf-8'))
    settings = json.loads((root/'service/appsettings.json').read_text(encoding='utf-8'))
    communication = json.loads((root/'data/communication-settings.json').read_text(encoding='utf-8-sig'))['Configuration']
    if not args.confirm_synthetic_crash_test or not lab.get('simulationOnly') or not lab.get('twoVehicleSimulation'):
        raise RuntimeError('Explicit private synthetic crash-test opt-in required')
    if any(d['IpAddress'] != '127.0.0.1' for d in [settings['Plc'], *settings['Agv']['Robots'], *communication['Plcs'], *communication['Agvs']]):
        raise RuntimeError('Loopback devices only')
    if Path(settings['Database']['Path']).resolve() != root/'data/agv-dispatch.db':
        raise RuntimeError('Wrong private database')
    ready = read_ready(root)
    evidence = root/('cancel-restart-'+time.strftime('%Y%m%d-%H%M%S'))
    evidence.mkdir()
    def save(name, data):
        (evidence/name).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    def api(path, data=None):
        req = urllib.request.Request(ready['url'].rstrip('/')+path,
            data=json.dumps(data).encode() if data is not None else None, headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(req, timeout=30) as response:
            result = json.load(response)
        if data is not None:
            save('operation-'+str(time.time_ns())+'.json',dict(path=path,request=data,response=result))
        if isinstance(result,dict) and result.get('accepted') is False:
            raise RuntimeError(str(result))
        return result
    def state():
        return json.loads((root/'multi-state.json').read_text(encoding='utf-8'))
    def wait(label, fn, timeout=240):
        deadline=time.monotonic()+timeout
        while time.monotonic()<deadline:
            result=fn()
            if result:return result
            time.sleep(.25)
        raise RuntimeError('Timeout: '+label)
    def read_sql(query):
        with sqlite3.connect((root/'data/agv-dispatch.db').as_uri()+'?mode=ro',uri=True) as db:
            db.row_factory=sqlite3.Row
            return [dict(row) for row in db.execute(query)]
    def leases():
        return read_sql('select LeaseId,ResourceId,OwnerStepId,TaskId,VehicleId,MapId,MapVersionId from traffic_resource_lease order by LeaseId')
    initial=state()
    if any(c['loaded'] or c['current_task'] for c in initial['cars'].values()) or any(bytes.fromhex(initial['memory_hex'])[o] != 10 for o in (0,2)) or bytes.fromhex(initial['memory_hex'])[610] != 0 or leases():
        raise RuntimeError('Both previous missions must be physically and logically closed')
    if not api('/api/recovery')['released'] or api('/api/maps/switch')['mode'] != 'Running' or any(t['stage']!='Completed' for t in api('/api/tasks')['items']):
        raise RuntimeError('Unresolved tasks or execution gate')
    snapshot=api('/api/snapshot')
    if snapshot['status']['namedPipe']['pipeName'] != settings['Security']['PipeName'] or settings['Security']['PipeName']!=ready['pipe']:
        raise RuntimeError('Wrong running Service')
    def create(station,car):
        tmp=root/'probe-input.tmp'
        tmp.write_text(json.dumps(dict(sequence=uuid.uuid4().hex,call=station)),encoding='utf-8')
        tmp.replace(root/'device-input.json')
        def options_ready():
            o=api('/api/task-options')
            return o if o['available'] and any(s['stationId']==station and s['available'] for s in o['stations']) else None
        options=wait('new call '+station,options_ready,30)
        return api('/api/tasks',dict(requestId=uuid.uuid4().hex,stationId=station,requestedVehicleId=car,priority=0,
            mapId=options['mapId'],mapVersionId=options['mapVersionId'],parameterRevision=options['parameterRevision']))['taskId']
    owner=create('CNC-001','AGV-LOCAL-01')
    print('FAULT PROBE OWNER '+owner,flush=True)
    wait('owner at waiting bay',lambda:state()['cars']['AGV-LOCAL-01']['station']=='TEST-LM107')
    follower=create('CNC-002','AGV-LOCAL-02')
    save('task-identities.json',dict(owner=owner,follower=follower))
    def entering():
        d=api('/api/tasks/'+owner)
        s=state()['cars']['AGV-LOCAL-01']
        return d if d['steps'][4]['status']=='Executing' and 33.5<=s['x']<=36 and s['current_task'] else None
    detail=wait('owner actually entering with original transport',entering,35)
    before=state()
    save('before-cancel.json',dict(detail=detail,devices=before,leases=leases()))
    request=dict(requestId=uuid.uuid4().hex,taskId=owner,expectedStage=detail['task']['stage'],reason='SIM-ONLY双车载箱进站中取消；保留原PLC及倒料路权，测试车辆确认与重启冻结')
    api('/api/tasks/'+owner+'/cancel',request)
    # Same request identity must replay, never create a second cancellation.
    api('/api/tasks/'+owner+'/cancel',request)
    wait('original vehicle cancellation confirmed',lambda:(lambda d:d if d['task']['stage']=='CancellationReview' else None)(api('/api/tasks/'+owner)),45)
    protected=lab.get('waitingBayProtection') is True
    expected_follower_site='TEST-LM102' if protected else 'TEST-LM107'
    if protected:
        def blocked_at_pickup():
            d=api('/api/tasks/'+follower)
            p=api('/api/traffic/waiting',{})
            return d['steps'][2]['status']=='Ready' and any(r['taskId']==follower and any(b['resourceId']=='R4_候车位' and b['taskId']==owner for b in r['blockers']) for r in p['page']['items'])
        wait('follower loaded and blocked by protected waiting bay',blocked_at_pickup)
    else:
        wait('follower reaches actual single waiting bay',lambda:state()['cars']['AGV-LOCAL-02']['station']==expected_follower_site)
    time.sleep(5)
    held=state();held_leases=leases()
    origin=bytes.fromhex(held['memory_hex'])[0]
    if origin & 96 != 96 or held['cars']['AGV-LOCAL-02']['station']!=expected_follower_site or not any(l['TaskId']==owner and l['ResourceId'].startswith('R2_') for l in held_leases):
        raise RuntimeError('Cancellation lost origin responsibility, owner resource or follower exclusion')
    old_recovery=api('/api/recovery')
    save('cancel-held.json',dict(owner=api('/api/tasks/'+owner),follower=api('/api/tasks/'+follower),devices=held,leases=held_leases))
    commands_before=read_sql('select CommandId,TaskId,StepId,Status,AttemptCount from agv_command_outbox order by CommandId')
    save('owned-crash.json',restart_owned_service(root))
    recovery=wait('new boot recovery barrier',lambda:(lambda r:r if not r['released'] and r['sessionId']!=old_recovery['sessionId'] else None)(api('/api/recovery')),45)
    time.sleep(5)
    after=state()
    if leases()!=held_leases or after['memory_hex']!=held['memory_hex'] or commands_before!=read_sql('select CommandId,TaskId,StepId,Status,AttemptCount from agv_command_outbox order by CommandId'):
        raise RuntimeError('Restart changed held ownership/PLC or dispatched another command')
    if after['cars']['AGV-LOCAL-02']['station']!=expected_follower_site:
        raise RuntimeError('Follower entered during cancellation/recovery hold')
    save('restart-held.json',dict(recovery=recovery,snapshot=api('/api/snapshot'),devices=after,leases=leases()))
    result=dict(passed=True,owner=owner,follower=follower,duplicateRequestReplayed=True,originalVehicleConfirmed=True,
        ownerOriginBits56Held=True,followerExcluded=True,leaseIdentityPreserved=True,plcUnchanged=True,
        outboxUnchanged=True,newBootRecoveryHeld=True,followerHeldAtPickup=protected,finalState='Expected freeze; no recovery approval or automatic PLC reset')
    save('result.json',result)
    print('PASS cancellation/restart HOLD '+str(evidence),flush=True)


if __name__=='__main__':
    main()
