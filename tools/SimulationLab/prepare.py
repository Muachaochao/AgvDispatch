"""Build a NEW isolated simulation package and a same-origin control portal."""
import argparse
import copy
import json
import shutil
import socket
import subprocess
import sys
from pathlib import Path
from controller import WORKSPACE, read, write


def prepare(name, vehicles=2, publish=True):
    if Path(name).name != name or not name.startswith('web-single-vehicle-') or len(name) > 80:
        raise ValueError('使用新的 web-single-vehicle-* 实例名。')
    if not 2 <= vehicles <= 8: raise ValueError('车队数量须为2至8。')
    root = WORKSPACE / 'artifacts' / name
    if root.exists(): raise ValueError('实例已存在，不覆盖。请使用新名字以保留故障现场。')
    subprocess.run([sys.executable, str(WORKSPACE / 'tools/WebTwoVehicleLab/prepare.py'), '--name', name], cwd=WORKSPACE, check=True)
    settings = read(root / 'service/appsettings.json'); lab = read(root / 'lab.json')
    ports = {lab[k] for k in ('robotPort', 'navigationPort', 'plcPort', 'webPort')}
    for robot in settings['Agv']['Robots']: ports.update((robot['Port'], robot['NavigationPort']))
    def port():
        while True:
            with socket.socket() as sock:
                sock.bind(('127.0.0.1', 0)); value = sock.getsockname()[1]
            if value not in ports and value not in (5198, 5199): ports.add(value); return value
    origins = ['取货A', '取货B', '停车A', '停车B', '检修', '西下', '西上', '东下']
    navigation = read(root / 'navigation.agvmap.json')
    available = {n['id'] for n in navigation['nodes'] if n.get('enabled', True)}
    # Keep explicit, distinct starts. Extra cars remain monitored fleet capacity;
    # the maintained PLC fixture has two independently modelled call stations.
    origins = origins[:2] + [n['id'] for n in navigation['nodes'] if n['id'] not in ('取货A','取货B','倒料','等待') and n['id'] in available]
    if len(origins) < vehicles: raise ValueError('地图没有足够的独立起点。')
    for index in range(2, vehicles):
        robot = copy.deepcopy(settings['Agv']['Robots'][0]); robot.update(VehicleId=f'AGV-LOCAL-{index+1:02}', DisplayName=f'仿真车{index+1:02}', Port=port(), NavigationPort=port())
        settings['Agv']['Robots'].append(robot)
        profile = copy.deepcopy(settings['HandshakeExecution']['DumperExit']['BodyProfiles'][0])
        profile.update(VehicleId=robot['VehicleId'], ProfileId='SIM-ONLY-' + robot['VehicleId'] + '-0.6m')
        settings['HandshakeExecution']['DumperExit']['BodyProfiles'].append(profile)
    settings['Dispatching']['MaxConcurrentTasks'] = vehicles
    lab.update(multiVehicleSimulation=True, initialSites={r['VehicleId']: origin for r, origin in zip(settings['Agv']['Robots'], origins)})
    write(root / 'lab.json', lab)
    # Publish before restoring runtime configuration; publish must not replace it.
    web = read(root / 'webhost/appsettings.json')
    if publish:
        for project, target in [('AgvDispatch.Service', 'service'), ('AgvDispatch.WebHost', 'webhost')]:
            subprocess.run(['dotnet', 'publish', str(WORKSPACE / project / (project + '.csproj')), '-c', 'Release', '-o', str(root / target)], cwd=WORKSPACE, check=True)
    for path in ('service/appsettings.json', 'service-settings.json'): write(root / path, settings)
    write(root / 'webhost/appsettings.json', web)
    portal = root / 'portal'; portal.mkdir()
    if publish:
        for source in (root / 'webhost').iterdir():
            target = portal / source.name
            if source.is_dir(): shutil.copytree(source, target)
            elif source.name != 'appsettings.json': shutil.copy2(source, target)
    portal_port, control_port = port(), port()
    bridge = dict(Enabled=True, Root=str(root), PythonPath=sys.executable, ControllerPath=str(Path(__file__).with_name('controller.py')), Port=control_port)
    write(portal / 'appsettings.json', dict(Web=dict(Port=portal_port, PipeName='SimulationPortal-NoFieldAccess', EngineeringSimulationOnly=True,
        StaticPath=str(WORKSPACE / 'AgvDispatch.Web/dist'), ProjectStorePath=str(root / 'portal-data/projects'), EngineeringStorePath=str(root / 'portal-data/engineering'), Simulation=bridge)))
    url = f'http://127.0.0.1:{portal_port}/#/simulation-center'
    # Native launchers do not change the user's daily 5199 pointer or settings.
    (root / '启动仿真控制台.cmd').write_text('@echo off\ncd /d "%~dp0portal"\nstart "" "' + url + '"\nstart "AGV仿真控制台" /min "AgvDispatch.WebHost.exe"\n', encoding='gbk')
    write(root / 'simulation-package.json', dict(url=url, root=str(root), vehicles=vehicles, modeledCallStations=2, bridge=bridge))
    print(json.dumps(dict(url=url, root=str(root), vehicles=vehicles, modeledCallStations=2), ensure_ascii=False))
    return root


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--name', required=True); parser.add_argument('--vehicles', type=int, default=2)
    parser.add_argument('--no-publish', action='store_true', help='仅供配置检查；不能当作可运行发布包。')
    args = parser.parse_args(); prepare(args.name, args.vehicles, not args.no_publish)
