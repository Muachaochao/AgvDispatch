"""Launch only inspected loopback configurations under this project's artifacts."""
import json
import os
import subprocess
import socket
from pathlib import Path
from lab_paths import read_ready, resolve_lab_root
from lab_logging import rotate_before_launch

workspace=Path(__file__).resolve().parents[2]
labroot=resolve_lab_root()
if (labroot/'ready.json').exists():
    ready=read_ready(labroot)
else:
    lab=json.loads((labroot/'lab.json').read_text(encoding='utf-8-sig'))
    ready=dict(url=f"http://127.0.0.1:{lab['webPort']}/",root=str(labroot),port=lab['webPort'],pipe=lab['pipe'])
root=Path(ready['root']).resolve()
if not root.is_relative_to(labroot.resolve()):
    raise RuntimeError('Runtime must remain inside this lab')
service=json.loads((root/'service/appsettings.json').read_text(encoding='utf-8'))
communication_path=root/'data/communication-settings.json'
communication=json.loads(communication_path.read_text(encoding='utf-8-sig'))['Configuration'] if communication_path.exists() else dict(Plcs=[service['Plc']],Agvs=service['Agv']['Robots'])
for node in [service['Plc'], *service['Agv']['Robots'], *communication['Plcs'], *communication['Agvs']]:
    if node['IpAddress']!='127.0.0.1':raise RuntimeError('Non-loopback device endpoint rejected')
database=Path(service['Database']['Path']).resolve()
if not database.is_relative_to(root):raise RuntimeError('Database outside private runtime')
web=json.loads((root/'webhost/appsettings.json').read_text(encoding='utf-8'))
assert web['Web']['PipeName']==service['Security']['PipeName']==ready['pipe']
assert service['TaskCreation']['AutomaticCreationEnabled'] is False
ports={web['Web']['Port']}
for device in [service['Plc'], *service['Agv']['Robots'], *communication['Plcs'], *communication['Agvs']]:
    for key in ('Port','NavigationPort'):
        if key in device:ports.add(device[key])
if any(not isinstance(port,int) or not 1<=port<=65535 for port in ports):raise RuntimeError('Invalid private runtime port')
for executable in (root/'service/AgvDispatch.Service.exe',root/'webhost/AgvDispatch.WebHost.exe'):
    if not executable.is_file():raise RuntimeError(f'Publish the current application to this private directory first: {executable}')
for port in sorted(ports):
    try:
        with socket.create_connection(('127.0.0.1',port),timeout=0.3):
            raise RuntimeError(f'Port {port} already in use; refusing a duplicate lab instance')
    except (ConnectionRefusedError, TimeoutError):
        pass
env=os.environ.copy()
env['AGV_DISPATCH_DATABASE_PATH']=str(database)
processes=[]
def launch(args,cwd,label):
    for suffix in ('-stdout.log', '-stderr.log'):
        rotate_before_launch(root / (label + suffix))
    out=(root/(label+'-stdout.log')).open('ab')
    err=(root/(label+'-stderr.log')).open('ab')
    try:
        process=subprocess.Popen(args,cwd=cwd,env=env,stdout=out,stderr=err,creationflags=subprocess.CREATE_NO_WINDOW)
    finally:
        out.close();err.close()
    processes.append(dict(Id=process.pid,Path=str(args[0]),Arguments=[str(a) for a in args[1:]]))
    # Preserve ownership even if a later child fails to start; never lose the
    # first PID and then stop unrelated processes by name.
    (root/'processes.json').write_text(json.dumps(processes,indent=2),encoding='utf-8')

import sys
(labroot/'ready.json').write_text(json.dumps(ready,ensure_ascii=False,indent=2),encoding='utf-8')
twin = 'WebTwoVehicleLab/multi_twin.py' if json.loads((root/'lab.json').read_text(encoding='utf-8-sig')).get('twoVehicleSimulation') is True else 'WebSingleVehicleLab/device_twin.py'
launch([sys.executable,str(workspace/'tools'/twin),str(root)],root,'twin')
launch([str(root/'service/AgvDispatch.Service.exe')],root/'service','service')
launch([str(root/'webhost/AgvDispatch.WebHost.exe')],root/'webhost','web')
(root/'processes.json').write_text(json.dumps(processes,indent=2),encoding='utf-8')
print(json.dumps(dict(url=ready['url'],processIds=[p['Id'] for p in processes],devices='loopback-only',database=str(database))))
