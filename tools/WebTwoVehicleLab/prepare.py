"""Prepare a fresh, private two-car instance using the maintained single-car template."""
import argparse
import copy
import hashlib
import json
import socket
import subprocess
import sys
from pathlib import Path

workspace = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--name', required=True)
args = parser.parse_args()
subprocess.run([sys.executable, str(workspace / 'tools/WebSingleVehicleLab/prepare_lab.py'), '--name', args.name], check=True)
root = workspace / 'artifacts' / args.name
settings = json.loads((root / 'service/appsettings.json').read_text(encoding='utf-8'))
lab = json.loads((root / 'lab.json').read_text(encoding='utf-8'))
used = {lab[k] for k in ('robotPort', 'navigationPort', 'plcPort', 'webPort')}
def port():
    while True:
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            value = sock.getsockname()[1]
        if value not in used:
            used.add(value)
            return value
second = copy.deepcopy(settings['Agv']['Robots'][0])
second.update(VehicleId='AGV-LOCAL-02', DisplayName='双车联调02', Port=port(), NavigationPort=port())
settings['Agv']['Robots'].append(second)
settings['Agv']['Robots'][0]['DisplayName'] = '双车联调01'
profile = copy.deepcopy(settings['HandshakeExecution']['DumperExit']['BodyProfiles'][0])
profile.update(VehicleId=second['VehicleId'], ProfileId='SIM-ONLY-AGV-LOCAL-02-0.6m')
settings['HandshakeExecution']['DumperExit']['BodyProfiles'].append(profile)
settings['Dispatching']['MaxConcurrentTasks'] = 2
navigation = json.loads((root/'navigation.agvmap.json').read_text(encoding='utf-8'))
navigation['name'] = '28阶段双车闭环联调车间'
(root/'navigation.agvmap.json').write_text(json.dumps(navigation,ensure_ascii=False,indent=2),encoding='utf-8')
manifest = json.loads((root/'source-manifest.json').read_text(encoding='utf-8'))
manifest['editedSha256'] = hashlib.sha256((root/'navigation.agvmap.json').read_bytes()).hexdigest()
manifest['changes'].append('two-car display name; two independent synthetic body profiles')
manifest['simulationBodyProfiles'] = settings['HandshakeExecution']['DumperExit']['BodyProfiles']
(root/'source-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
for path in ('service/appsettings.json', 'service-settings.json'):
    (root / path).write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding='utf-8')
lab['twoVehicleSimulation'] = True
(root / 'lab.json').write_text(json.dumps(lab, ensure_ascii=False, indent=2), encoding='utf-8')
print('Prepared two-car private lab: ' + str(root))
