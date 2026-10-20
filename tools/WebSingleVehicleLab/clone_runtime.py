"""Startable snapshot after normal configuration saves, without stopping old processes."""
import json
import shutil
import socket
import sqlite3
import sys
from pathlib import Path

root=Path(__file__).resolve().parents[2]/'artifacts/web-single-vehicle-20261020'
fresh='--fresh-devices' in sys.argv
name=sys.argv[sys.argv.index('--name')+1] if '--name' in sys.argv else ('runtime-clean' if fresh else 'runtime')
if not name.startswith('runtime') or Path(name).name!=name:raise RuntimeError('Invalid private runtime name')
runtime=root/name
runtime.mkdir(exist_ok=False)
for part in ('service','webhost'):
    source=Path(sys.argv[sys.argv.index('--service-source')+1]).resolve() if part=='service' and '--service-source' in sys.argv else root/part
    if not source.resolve().is_relative_to(root.resolve()):raise RuntimeError('Build source outside private lab')
    shutil.copytree(source,runtime/part,ignore=shutil.ignore_patterns('logs','data','appsettings*.json'))
shutil.copy2(root/'service/config/plc-signals.csv',runtime/'service/config/plc-signals.csv')
data=runtime/'data';data.mkdir()
with sqlite3.connect(f'file:{(root / "data/agv-dispatch.db").as_posix()}?mode=ro',uri=True) as source:
    with sqlite3.connect(data/'agv-dispatch.db') as target:
        source.backup(target)
settings=json.loads((root/'data/communication-settings.json').read_text(encoding='utf-8-sig'))
for plc in settings['Configuration']['Plcs']:
    plc['SignalCsvPath']='config/plc-signals.csv'
service=json.loads((root/'service-settings.json').read_text(encoding='utf-8'))
service['Database']['Path']=str(data/'agv-dispatch.db')
service['Security']['PipeName']='AgvSingleVehicleLab20261020-'+name
if fresh:
    shutil.copy2(root/'navigation.agvmap.json',runtime/'navigation.agvmap.json')
    ports=[]
    probes=[]
    for _ in range(3):
        probe=socket.socket();probe.bind(('127.0.0.1',0));ports.append(probe.getsockname()[1]);probes.append(probe)
    for probe in probes:probe.close()
    twin=dict(robotPort=ports[0],navigationPort=ports[1],plcPort=ports[2])
    (runtime/'lab.json').write_text(json.dumps(twin),encoding='utf-8')
    service['Plc']['Port']=twin['plcPort']
    service['Agv']['Robots'][0].update(Port=twin['robotPort'],NavigationPort=twin['navigationPort'])
    service['Map']['ImportPath']=str(runtime/'navigation.agvmap.json')
    settings['Configuration']['Plcs'][0]['Port']=twin['plcPort']
    settings['Configuration']['Agvs'][0].update(Port=twin['robotPort'],NavigationPort=twin['navigationPort'])
(data/'communication-settings.json').write_text(json.dumps(settings,ensure_ascii=False,indent=2),encoding='utf-8')
(runtime/'service/appsettings.json').write_text(json.dumps(service,ensure_ascii=False,indent=2),encoding='utf-8')
web=json.loads((root/'webhost/appsettings.json').read_text(encoding='utf-8'))
with socket.socket() as sock:
    sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
web['Web']['Port']=port
web['Web']['PipeName']=service['Security']['PipeName']
web['Web']['ProjectStorePath']=str(runtime/'projects')
(runtime/'webhost/appsettings.json').write_text(json.dumps(web,ensure_ascii=False,indent=2),encoding='utf-8')
(root/'ready.json').write_text(json.dumps(dict(url=f'http://127.0.0.1:{port}/',root=str(runtime),port=port,pipe=service['Security']['PipeName']),indent=2),encoding='utf-8')
print(port)
