"""Exercise preparation in a temporary workspace; never writes a running lab."""
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

workspace = Path(__file__).resolve().parents[2]
source_map = Path('发布包/示例地图/离线厂区_20261019/离线厂区_12点15路.agvmap.json')
with tempfile.TemporaryDirectory(prefix='agv-v2-template-') as temporary:
    home = Path(temporary)
    project = home / 'DispatchingSystem'
    template = project / 'tools/WebSingleVehicleLab/prepare_lab.py'
    template.parent.mkdir(parents=True)
    shutil.copy2(workspace / 'tools/WebSingleVehicleLab/prepare_lab.py', template)
    navigation_source = home / source_map
    navigation_source.parent.mkdir(parents=True)
    shutil.copy2(workspace.parent / source_map, navigation_source)
    csv_path = project / 'AgvDispatch.Service/config/plc-signals.csv'
    csv_path.parent.mkdir(parents=True)
    shutil.copy2(workspace / 'AgvDispatch.Service/config/plc-signals.csv', csv_path)
    args = [sys.executable, str(template), '--name', 'web-single-vehicle-template-check']
    subprocess.run(args, check=True, capture_output=True, text=True)
    root = project / 'artifacts/web-single-vehicle-template-check'
    settings_path = root / 'service-settings.json'
    settings = json.loads(settings_path.read_text(encoding='utf-8'))
    lab = json.loads((root / 'lab.json').read_text(encoding='utf-8'))
    navigation = json.loads((root / 'navigation.agvmap.json').read_text(encoding='utf-8'))
    assert lab['simulationOnly'] and lab['plcContractVersion'] == 'plc-28-stage-v2'
    handshake = settings['HandshakeExecution']
    assert handshake['Enabled'] and handshake['PlcContractVersion'] == 'plc-28-stage-v2'
    exit_config = handshake['DumperExit']
    assert exit_config['ResourceIds'] == ['R2_倒料区']
    body = exit_config['BodyProfiles'][0]
    assert body['ProfileId'].startswith('SIM-ONLY-') and body['VehicleId'] == 'AGV-LOCAL-01'
    assert body['CalibrationVerified'] and body['IncludesMaximumPermittedLoad']
    assert body['RadiusFromReportedReferenceMeters'] == 0.6 and body['MaximumPositionUncertaintyMeters'] == 0.05
    assert settings['Plc']['IpAddress'] == '127.0.0.1' and all(robot['IpAddress'] == '127.0.0.1' for robot in settings['Agv']['Robots'])
    boundary = next(resource for resource in navigation['resources'] if resource['id'] == 'R2_倒料区')['boundary']
    assert boundary == [dict(x=35, y=6), dict(x=41, y=6), dict(x=41, y=14), dict(x=35, y=14)]
    assert 'R2_倒料区' in next(node for node in navigation['nodes'] if node['id'] == '倒料')['resourceIds']
    manifest = json.loads((root / 'source-manifest.json').read_text(encoding='utf-8'))
    assert manifest['repeatPickupRoutes'] == {'CNC-001': ['取货A', '西下', '取货A'], 'CNC-002': ['取货B', '西上', '取货B']}
    assert 'must never be reused as field calibration' in manifest['boundary']
    before = hashlib.sha256(settings_path.read_bytes()).hexdigest()
    repeated = subprocess.run(args, capture_output=True, text=True)
    assert repeated.returncode != 0 and hashlib.sha256(settings_path.read_bytes()).hexdigest() == before
    invalid = subprocess.run([sys.executable, str(template), '--name', '../outside'], capture_output=True, text=True)
    assert invalid.returncode != 0 and not (home / 'outside').exists()
    print('PASS: 13 v2 preparation checks; generated only in a temporary workspace, existing laboratory untouched')
