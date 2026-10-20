"""Collect actual task responses/diagnostic archives without changing task state."""
import hashlib
import json
import sqlite3
import urllib.request
from pathlib import Path

root=Path(__file__).resolve().parents[2]/'artifacts/web-single-vehicle-20261020'
ready=json.loads((root/'ready.json').read_text(encoding='utf-8'))
base=ready['url'].rstrip('/')
target=Path(ready['root'])
evidence=root/'final-evidence';evidence.mkdir(exist_ok=True)
def read(url):
    with urllib.request.urlopen(url,timeout=30) as response:return json.load(response)
tasks=read(base+'/api/tasks')['items']
details=[]
for task in tasks:
    detail=read(base+'/api/tasks/'+task['taskId']);details.append(detail)
    (evidence/(task['taskId']+'.json')).write_text(json.dumps(detail,ensure_ascii=False,indent=2),encoding='utf-8')
    with urllib.request.urlopen(base+'/api/tasks/'+task['taskId']+'/diagnostics/export',timeout=30) as response:
        (evidence/(task['taskId']+'.zip')).write_bytes(response.read())
snapshot=read(base+'/api/snapshot')
(evidence/'backend-snapshot.json').write_text(json.dumps(snapshot,ensure_ascii=False,indent=2),encoding='utf-8')
with sqlite3.connect(f'file:{(target/"data/agv-dispatch.db").as_posix()}?mode=ro',uri=True) as source:
    leases=source.execute('select count(*) from traffic_resource_lease').fetchone()[0]
    commands=source.execute('select TaskId,count(*),sum(AttemptCount) from agv_command_outbox group by TaskId').fetchall()
    with sqlite3.connect(evidence/'database-backup.db') as backup:source.backup(backup)
checks=[]
for detail in details:
    tid=detail['task']['taskId']
    checks.append(dict(taskId=tid,stage=detail['task']['stage'],completedSteps=sum(s['status']=='Completed' for s in detail['steps']),
                       errors=[s['errorMessage'] for s in detail['steps'] if s.get('errorMessage')]))
events=[json.loads(line) for line in (target/'device-events.jsonl').read_text(encoding='utf-8').splitlines()]
sends=[e for e in events if e['kind']=='robokit' and e['api']==3066]
summary=dict(url=ready['url'],tasks=checks,leases=leases,commands=commands,
             accepted3066=sum(e['response'].get('ret_code')==0 for e in sends),
             rawSource='backend',deviceBoundary='loopback virtual Robokit/S7, not physical acceptance')
(root/'final-result.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
manifest=[dict(file=str(p.relative_to(root)),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in evidence.iterdir() if p.is_file()]
(root/'final-evidence-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
assert checks and all(c['stage']=='Completed' and c['completedSteps']==9 and not c['errors'] for c in checks),summary
assert leases==0 and all(count==4 and attempts==4 for _,count,attempts in commands),summary
assert summary['accepted3066']==len(checks)*4,summary
print(json.dumps(summary,ensure_ascii=False))
