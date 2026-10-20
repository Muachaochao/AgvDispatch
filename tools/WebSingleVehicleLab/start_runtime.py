"""Launch only inspected loopback configurations under this project's artifacts."""
import json
import os
import subprocess
import socket
from pathlib import Path

workspace=Path(__file__).resolve().parents[2]
labroot=workspace/'artifacts/web-single-vehicle-20261020'
ready=json.loads((labroot/'ready.json').read_text(encoding='utf-8'))
root=Path(ready['root']).resolve()
if not root.is_relative_to(labroot.resolve()):
    raise RuntimeError('Runtime must remain inside this lab')
service=json.loads((root/'service/appsettings.json').read_text(encoding='utf-8'))
communication=json.loads((root/'data/communication-settings.json').read_text(encoding='utf-8'))['Configuration']
for node in [service['Plc'], *service['Agv']['Robots'], *communication['Plcs'], *communication['Agvs']]:
    if node['IpAddress']!='127.0.0.1':raise RuntimeError('Non-loopback device endpoint rejected')
database=Path(service['Database']['Path']).resolve()
if not database.is_relative_to(root):raise RuntimeError('Database outside private runtime')
web=json.loads((root/'webhost/appsettings.json').read_text(encoding='utf-8'))
assert web['Web']['PipeName']==service['Security']['PipeName']==ready['pipe']
assert service['TaskCreation']['AutomaticCreationEnabled'] is False
for port in [web['Web']['Port'], service['Plc']['Port'], service['Agv']['Robots'][0]['Port'], service['Agv']['Robots'][0]['NavigationPort']]:
    try:
        with socket.create_connection(('127.0.0.1',port),timeout=0.3):
            raise RuntimeError(f'Port {port} already in use; refusing a duplicate lab instance')
    except (ConnectionRefusedError, TimeoutError):
        pass
env=os.environ.copy()
env['AGV_DISPATCH_DATABASE_PATH']=str(database)
processes=[]
def launch(args,cwd,label):
    out=(root/(label+'-stdout.log')).open('ab')
    err=(root/(label+'-stderr.log')).open('ab')
    process=subprocess.Popen(args,cwd=cwd,env=env,stdout=out,stderr=err,creationflags=subprocess.CREATE_NO_WINDOW)
    processes.append(dict(Id=process.pid,Path=str(args[0]),Arguments=[str(a) for a in args[1:]]))
    out.close();err.close()

import sys
launch([sys.executable,str(workspace/'tools/WebSingleVehicleLab/device_twin.py'),str(root)],root,'twin')
launch([str(root/'service/AgvDispatch.Service.exe')],root/'service','service')
launch([str(root/'webhost/AgvDispatch.WebHost.exe')],root/'webhost','web')
(root/'processes.json').write_text(json.dumps(processes,indent=2),encoding='utf-8')
print(json.dumps(dict(url=ready['url'],processIds=[p['Id'] for p in processes],devices='loopback-only',database=str(database))))
