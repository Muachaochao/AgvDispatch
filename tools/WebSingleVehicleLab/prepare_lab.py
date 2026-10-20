"""Prepare files for an isolated instance; never reads/copies a live database."""
import csv
import getpass
import hashlib
import json
import os
import socket
from pathlib import Path

workspace=Path(__file__).resolve().parents[2]
root=workspace/'artifacts/web-single-vehicle-20261020'
root.mkdir(parents=True,exist_ok=True)
if (root/'lab.json').exists():
    raise SystemExit('Lab already prepared; reuse its files instead of overwriting.')

def write(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0))
        return sock.getsockname()[1]

ports=[port() for _ in range(4)]
assert len(set(ports))==4
lab=dict(root=str(root),robotPort=ports[0],navigationPort=ports[1],plcPort=ports[2],webPort=ports[3],pipe='AgvSingleVehicleLab20261020',mapName='single_vehicle_lab')
source=workspace.parent/'发布包/示例地图/离线厂区_20261019/离线厂区_12点15路.agvmap.json'
navigation=json.loads(source.read_text(encoding='utf-8-sig'))
navigation['name']='单车闭环联调车间'
for edge in navigation['edges']:
    edge['speedLimit']=1.0
    if edge['id']=='E13_中部连接':edge['enabled']=False
write(root/'navigation.agvmap.json',navigation)
write(root/'source-manifest.json',dict(source=str(source),sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
    editedFile='navigation.agvmap.json',editedSha256=hashlib.sha256((root/'navigation.agvmap.json').read_bytes()).hexdigest(),
    changes=['name','speed limit 1m/s','disable E13 to exercise curved north route'],
    boundary='Existing authored offline map file; not a real vehicle survey. Device endpoints are loopback twins.'))
with (workspace/'AgvDispatch.Service/config/plc-signals.csv').open(encoding='utf-8-sig',newline='') as f:
    rows=list(csv.DictReader(f))
rows=[r for r in rows if r['stationId'] in ['CNC-001','CNC-002','DUMPER-001']]
assert len(rows)==21
signal_path=root/'service/config/plc-signals.csv'
signal_path.parent.mkdir(parents=True,exist_ok=True)
with signal_path.open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
identity=os.environ.get('USERDOMAIN',socket.gethostname())+'\\'+getpass.getuser()
service=dict(Database=dict(Path=str(root/'data/agv-dispatch.db')),
    Security=dict(PipeName=lab['pipe'],Users=[dict(Enabled=True,WindowsIdentity=identity,DisplayName='本机联调工程师',Role='Administrator')]),
    Plc=dict(Enabled=True,AllowWrites=True,SiemensSeries='S1200',IpAddress='127.0.0.1',Port=lab['plcPort'],SignalCsvPath='config/plc-signals.csv',PollIntervalMs=250,ReconnectDelayMs=1000),
    Agv=dict(Enabled=True,AllowCommands=True,ProtocolVersion=2,PollIntervalMs=500,RequestIntervalMs=100,NavigationCommandMode='SpecifiedPath',
        Robots=[dict(VehicleId='AGV-LOCAL-01',DisplayName='单车联调01',Enabled=True,IpAddress='127.0.0.1',Port=lab['robotPort'],NavigationPort=lab['navigationPort'])]),
    Map=dict(Enabled=True,ImportPath=str(root/'navigation.agvmap.json'),TrackSamplingIntervalMs=500),
    TaskCreation=dict(Enabled=True,AutomaticCreationEnabled=False,DumperStationId='DUMPER-001',AcknowledgementRetryIntervalMs=500),
    Dispatching=dict(Enabled=True,PollIntervalMs=500,MaxConcurrentTasks=1,DumperWaitingSite='等待',DumperDumpingSite='倒料',StationPickupSites={'CNC-001':'取货A','CNC-002':'取货B'},
        StepActions={key:dict(Operation=op) for key,op in [('GoToPickup','JackLoad'),('DeliverFullBoxToDumper','Wait'),('EnterDumpingPosition','Wait'),('ReturnEmptyBox','JackUnload')]}),
    AgvCommandOutbox=dict(Enabled=True,PollIntervalMs=250),DispatchTaskSteps=dict(Enabled=True,PollIntervalMs=250),
    HandshakeExecution=dict(Enabled=True,PlcContractVersion='sender-owned-return-confirmed-v1',StateMaxAgeSeconds=5),
    ExecutionIdentityAcquisition=dict(Enabled=False),RecoveryIdentity=dict(RequireMachineIdentity=False))
write(root/'service/appsettings.json',service)
write(root/'service-settings.json',service)
write(root/'webhost/appsettings.json',dict(Web=dict(Port=lab['webPort'],PipeName=lab['pipe'],StaticPath=str(workspace/'AgvDispatch.Web/dist'),ProjectStorePath=str(root/'projects'),EnableTaskSubmission=True,EnableRecoveryActions=True)))
write(root/'lab.json',lab)
print(json.dumps(lab,ensure_ascii=False))
