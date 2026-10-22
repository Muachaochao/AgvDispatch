"""Read-only cross-check of successful two-car evidence and a consistent SQLite backup."""
import argparse
import hashlib
import json
from datetime import datetime
from pathlib import Path
import sqlite3
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'WebSingleVehicleLab'))
from lab_paths import resolve_lab_root

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--root',required=True)
parser.add_argument('--evidence',required=True)
args=parser.parse_args()
root=resolve_lab_root(['--root',args.root])
evidence=Path(args.evidence).resolve()
if not evidence.is_relative_to(root) or not evidence.name.startswith(('two-car-','waiting-bay-')):
    raise RuntimeError('Only selected private two-car evidence allowed')
def load(path):return json.loads(path.read_text(encoding='utf-8'))
result=load(evidence/'result.json')
task_ids=list(result['tasks'])
if result.get('passed') is not True:raise RuntimeError('No passed original execution result')
details=load(evidence/'completed.json')
cars=[load(evidence/(vehicle+'-accepted-3066.json')) for vehicle in ('AGV-LOCAL-01','AGV-LOCAL-02')]
events=[json.loads(line) for line in (root/'device-events.jsonl').read_text(encoding='utf-8').splitlines()]
owner_index=0 if not result.get('owner') or result['tasks'][result['owner']]['vehicle']=='AGV-LOCAL-01' else 1
return_accepted=cars[owner_index][3]['time']
second_entry=cars[1-owner_index][2]['time']
cycle_low=next(e['time'] for e in events if e['kind']=='plc-bit' and e['address']=='DB1.DBX610.1' and e['value'] is False and e['time']>cars[owner_index][2]['time'])
if not return_accepted<cycle_low<second_entry:
    raise RuntimeError('Return submission, physical exit PLC closure and follower entry order wrong')
if result.get('noFollowerTransportWhileBayHeld'):
    owner=result['owner'];follower=result['follower']
    owner_at=datetime.fromisoformat(details[owner]['task']['updatedAt']).timestamp()
    vehicle=result['tasks'][follower]['vehicle']
    follower_sends=load(evidence/(vehicle+'-accepted-3066.json'))
    if follower_sends[1]['time']<=owner_at:
        raise RuntimeError('Follower delivery was accepted before original waiting-bay owner completed')
for vehicle,task in zip(('AGV-LOCAL-01','AGV-LOCAL-02'),task_ids):
    d=details[task]
    if not d.get('handover',{}).get('exitPhysicallyVerified'):
        raise RuntimeError('Original exit handover proof missing: '+vehicle)
    start=next(e['time'] for e in events if e['kind']=='plc-bit' and e['address']==('DB1.DBX0.5' if vehicle.endswith('01') else 'DB1.DBX2.5') and e['value'])
    address='0' if vehicle.endswith('01') else '2'
    clears={bit:next(e['time'] for e in events if e['kind']=='plc-bit' and e['address']==f'DB1.DBX{address}.{bit}' and not e['value'] and e['time']>start) for bit in (5,6,7)}
    if not clears[5]<clears[7]<clears[6]:
        raise RuntimeError('Origin notification/responsibility closure order wrong')
with sqlite3.connect((root/'data/agv-dispatch.db').as_uri()+'?mode=ro',uri=True) as db:
    db.row_factory=sqlite3.Row
    params=','.join('?' for _ in task_ids)
    command_query=f'select CommandId,TaskId,StepId,Status,AttemptCount from agv_command_outbox where TaskId in ({params}) order by CommandId'
    commands=[dict(r) for r in db.execute(command_query,task_ids)]
    if len(commands)!=8 or any(c['AttemptCount']!=1 or c['Status']!=30 for c in commands):
        raise RuntimeError('Expected eight sent original commands, one attempt each')
    backup=evidence/'completed-database.sqlite'
    if backup.exists():
        with sqlite3.connect(backup.as_uri()+'?mode=ro',uri=True) as existing:
            existing.row_factory=sqlite3.Row
            if [dict(r) for r in existing.execute(command_query,task_ids)]!=commands:
                raise RuntimeError('Existing evidence backup does not match original commands; never overwrite')
    else:
        with sqlite3.connect(backup) as destination:db.backup(destination)
(evidence/'outbox-readonly.json').write_text(json.dumps(commands,indent=2),encoding='utf-8')
verification=dict(passed=True,returnAcceptedAt=return_accepted,firstCycleClosedAt=cycle_low,secondEntryAcceptedAt=second_entry,
    bothOriginalExitProofs=True,bothOriginsCloseInOrder=True,eightOutboxCommandsOneAttemptEach=True,
    waitingBayHeldUntilOwnerCompleted=bool(result.get('noFollowerTransportWhileBayHeld')),
    scope='Historical normal two missions; later fault-probe tasks, if any, are excluded')
(evidence/'cross-verification.json').write_text(json.dumps(verification,indent=2),encoding='utf-8')
files=[]
for path in sorted(evidence.rglob('*')):
    if path.is_file() and path.name!='evidence-sha256.json':
        files.append(dict(path=str(path.relative_to(evidence)),sha256=hashlib.sha256(path.read_bytes()).hexdigest(),bytes=path.stat().st_size))
(evidence/'evidence-sha256.json').write_text(json.dumps(files,indent=2),encoding='utf-8')
print(f'PASS chronological/exit/PLC/8-command verification; {len(files)} hashed evidence files')
