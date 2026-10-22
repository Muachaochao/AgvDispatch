"""Two near-simultaneous Web tasks compete for one persisted waiting-bay capacity."""
import argparse
import json
import sqlite3
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'WebSingleVehicleLab'))
from lab_paths import resolve_lab_root,read_ready


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',required=True)
    args=parser.parse_args()
    root=resolve_lab_root(['--root',args.root]);ready=read_ready(root)
    lab=json.loads((root/'lab.json').read_text(encoding='utf-8'))
    settings=json.loads((root/'service/appsettings.json').read_text(encoding='utf-8'))
    if not all(lab.get(k) is True for k in ('simulationOnly','twoVehicleSimulation','waitingBayProtection')):
        raise RuntimeError('Explicit protected two-car synthetic fixture required')
    devices=[settings['Plc'],*settings['Agv']['Robots']]
    communication=root/'data/communication-settings.json'
    if communication.exists():
        saved=json.loads(communication.read_text(encoding='utf-8-sig'))['Configuration'];devices += [*saved['Plcs'],*saved['Agvs']]
    if any(d['IpAddress']!='127.0.0.1' for d in devices) or Path(settings['Database']['Path']).resolve()!=root/'data/agv-dispatch.db':
        raise RuntimeError('Only private loopback devices and database allowed')
    evidence=root/('waiting-bay-'+time.strftime('%Y%m%d-%H%M%S'));evidence.mkdir()
    def save(name,value):(evidence/name).write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
    def api(path,data=None):
        req=urllib.request.Request(ready['url'].rstrip('/')+path,data=json.dumps(data).encode() if data is not None else None,headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(req,timeout=25) as response:value=json.load(response)
        if data is not None and path!='/api/traffic/waiting':save('operation-'+str(time.time_ns())+'.json',dict(path=path,request=data,response=value))
        if isinstance(value,dict) and value.get('accepted') is False:raise RuntimeError(str(value))
        return value
    def state():return json.loads((root/'multi-state.json').read_text(encoding='utf-8'))
    def sql(query,parameters=()):
        with sqlite3.connect((root/'data/agv-dispatch.db').as_uri()+'?mode=ro',uri=True) as db:
            db.row_factory=sqlite3.Row;return [dict(row) for row in db.execute(query,parameters)]
    def event(name,**data):
        temp=root/('input-'+uuid.uuid4().hex+'.tmp');temp.write_text(json.dumps(dict(sequence=uuid.uuid4().hex,**data)),encoding='utf-8');temp.replace(root/name)
    def wait(label,predicate,timeout=180):
        end=time.monotonic()+timeout
        while time.monotonic()<end:
            value=predicate()
            if value:return value
            time.sleep(.3)
        raise RuntimeError('Timeout: '+label)
    initial=api('/api/snapshot')
    if initial['status']['namedPipe']['pipeName']!=ready['pipe'] or ready['pipe']!=settings['Security']['PipeName']:
        raise RuntimeError('Wrong running backend')
    if not api('/api/recovery')['released'] or api('/api/maps/switch')['mode']!='Running' or api('/api/tasks')['items'] or sql('select * from traffic_resource_lease'):
        raise RuntimeError('Fresh configured/reviewed instance with no tasks required')
    memory=bytes.fromhex(state()['memory_hex'])
    if any(memory[o]!=10 for o in (0,2)) or memory[610]!=0 or any(c['loaded'] or c['current_task'] for c in state()['cars'].values()):
        raise RuntimeError('Virtual device baseline is not closed/empty/stationary')
    started=time.time();tasks={}
    for station,vehicle in [('CNC-001','AGV-LOCAL-01'),('CNC-002','AGV-LOCAL-02')]:
        event('device-input.json',call=station)
        def available():
            o=api('/api/task-options');return o if o['available'] and any(s['stationId']==station and s['available'] for s in o['stations']) else None
        options=wait('call '+station,available,30)
        task=api('/api/tasks',dict(requestId=uuid.uuid4().hex,stationId=station,requestedVehicleId=vehicle,priority=0,
            mapId=options['mapId'],mapVersionId=options['mapVersionId'],parameterRevision=options['parameterRevision']))['taskId']
        tasks[task]=dict(station=station,vehicle=vehicle)
        print('SUBMITTED '+vehicle+' '+task,flush=True)
    save('task-identities.json',dict(tasks=tasks,started=started))
    owner=None;follower=None;offline_at=None;both_waiting_at=None;reconnected=False;contended=False;final={};signatures={}
    deadline=time.monotonic()+720
    while time.monotonic()<deadline:
        s=state();details={t:api('/api/tasks/'+t) for t in tasks}
        leases=sql('select LeaseId,ResourceId,OwnerStepId,TaskId,VehicleId from traffic_resource_lease')
        bay=[l for l in leases if l['ResourceId']=='R4_候车位']
        if len({l['TaskId'] for l in bay})>1:raise RuntimeError('Single waiting bay overbooked')
        page=api('/api/traffic/waiting',{})
        with (evidence/'observations.jsonl').open('a',encoding='utf-8') as stream:
            stream.write(json.dumps(dict(time=time.time(),devices=s,leases=leases,waiting=page,stages={t:d['task']['stage'] for t,d in details.items()}),ensure_ascii=False)+'\n')
        for t,d in details.items():
            signature=(d['task']['stage'],tuple(x['status'] for x in d['steps']))
            if signatures.get(t)!=signature:
                signatures[t]=signature;save('progress-'+str(time.time_ns())+'.json',dict(detail=d,devices=s,leases=leases))
                print(tasks[t]['vehicle']+' '+str(signature),flush=True)
            if d['task']['stage'] in ('Faulted','Cancelled'):raise RuntimeError('Unexpected terminal state')
        if owner is None and bay:
            candidate=bay[0]['TaskId']
            if details[candidate]['steps'][2]['status']=='Executing':
                owner=candidate;follower=next(t for t in tasks if t!=owner)
                event('network-input.json',vehicleId=tasks[owner]['vehicle'],offline=True)
                offline_at=time.time();save('owner-disconnect.json',dict(owner=owner,follower=follower,devices=s,leases=leases))
                print('FAULT owner network offline; accepted original movement continues',flush=True)
        if owner and not reconnected:
            f=details[follower];car=s['cars'][tasks[follower]['vehicle']]
            rows=[r for r in page['page']['items'] if r['taskId']==follower and any(b['resourceId']=='R4_候车位' and b['taskId']==owner for b in r['blockers'])]
            if rows and f['steps'][2]['status']=='Ready':
                pickup='TEST-LM101' if tasks[follower]['station']=='CNC-001' else 'TEST-LM102'
                if car['station']!=pickup or car['current_task'] or not car['loaded']:
                    raise RuntimeError('Follower must retain load at original pickup; no wait-bay transport may run')
                if sql('select CommandId from agv_command_outbox where TaskId=? and StepId=?',(follower,f['steps'][2]['stepId'])):
                    raise RuntimeError('Follower got an Outbox transport before winning bay capacity')
                contended=True
                if s['cars'][tasks[owner]['vehicle']]['station']=='TEST-LM107' and both_waiting_at is None:
                    both_waiting_at=time.time();save('capacity-blocked.json',dict(owner=details[owner],follower=f,devices=s,leases=leases,waiting=page))
                    print('OWNER at waiting bay; FOLLOWER loaded and held at pickup',flush=True)
            if both_waiting_at and time.time()-both_waiting_at>=8:
                if not bay or any(l['TaskId']!=owner for l in bay):raise RuntimeError('Offline owner lost waiting-bay ownership')
                event('network-input.json',vehicleId=tasks[owner]['vehicle'],offline=False);reconnected=True
                save('reconnect.json',dict(at=time.time(),devices=s,leases=leases,waiting=page))
                print('NETWORK restored; continue the same two missions',flush=True)
        if all(d['task']['stage']=='Completed' for d in details.values()):
            final=details;break
        time.sleep(.7)
    if not final or not contended or not reconnected:raise RuntimeError('Complete simultaneous-capacity/disconnect evidence missing')
    save('completed.json',final)
    end=wait('both PLC origin cycles and equipment closed',lambda:(lambda s:s if all(bytes.fromhex(s['memory_hex'])[o]==10 for o in (0,2)) and bytes.fromhex(s['memory_hex'])[610]==0 else None)(state()),30)
    if sql('select * from traffic_resource_lease'):raise RuntimeError('Unexplained final leases')
    commands=sql('select TaskId,StepId,CommandId,Status,AttemptCount from agv_command_outbox order by CommandId')
    if len(commands)!=8 or any(c['Status']!=30 or c['AttemptCount']!=1 for c in commands) or any(len(d['steps'])!=9 or any(x['status']!='Completed' for x in d['steps']) for d in final.values()):
        raise RuntimeError('Expected eight original commands once and 18 completed steps')
    for t,identity in tasks.items():
        events=[json.loads(line) for line in (root/'robots'/identity['vehicle']/'device-events.jsonl').read_text(encoding='utf-8').splitlines()]
        sends=[e for e in events if e['time']>=started and e['kind']=='robokit' and e['api']==3066 and e['response'].get('ret_code')==0]
        if len(sends)!=4:raise RuntimeError('Unexpected duplicate/missing 3066')
        save(identity['vehicle']+'-accepted-3066.json',sends)
    save('outbox-readonly.json',commands);save('final-devices.json',end)
    result=dict(passed=True,tasks=tasks,owner=owner,follower=follower,completedSteps=18,accepted3066=8,finalLeases=0,
        noFollowerTransportWhileBayHeld=True,offlineOwnerKeptCapacity=True,originalCommandsOnce=True,
        policy='Conservative whole-task waiting-bay reservation; no geometric collision or field certification')
    save('result.json',result)
    with sqlite3.connect((root/'data/agv-dispatch.db').as_uri()+'?mode=ro',uri=True) as source:
        with sqlite3.connect(evidence/'completed-database.sqlite') as target:source.backup(target)
    print('PASS '+str(evidence),flush=True)


if __name__=='__main__':main()
