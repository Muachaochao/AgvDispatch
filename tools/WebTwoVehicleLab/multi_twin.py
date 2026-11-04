"""Independent TCP cars and one shared S7 DB. Synthetic loopback devices only."""
import asyncio
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
from device_twin import DeviceTwin


class Car(DeviceTwin):
    def __init__(self, root, memory, initial_site, vehicle_id=None, control_root=None):
        super().__init__(root)
        self.memory = memory
        self.station = initial_site if initial_site in self.nodes else self.site(initial_site)
        self.x, self.y = self.nodes[self.station]['x'], self.nodes[self.station]['y']
        self.at_source = True
        self.vehicle_id = vehicle_id
        self.control_root = Path(control_root).resolve() if control_root else self.root
        self.apply_vehicle_profile()


class SharedPlc(DeviceTwin):
    def __init__(self, root, cars):
        super().__init__(root)
        self.cars = cars

    def scan_handshakes(self):
        # The equipment sees either car's physical arrival, never a command alone.
        dumper_site = self.semantic_site('dumperSite', '倒料')
        inspection_site = self.semantic_site('inspectionSite', '检修')
        present = [c for c in self.cars if c.at_site(c.site(dumper_site))]
        self.station = self.site(dumper_site) if present else self.site(inspection_site)
        self.x, self.y = self.nodes[self.station]['x'], self.nodes[self.station]['y']
        super().scan_handshakes()


def snapshot(car):
    return dict(station=car.station, x=car.x, y=car.y, angle=car.angle,
                loaded=car.loaded, current_task=car.current, tasks=car.tasks,
                completed_segments=car.steps, offline=car.offline,
                paused=car.paused, speedScale=car.speed_scale,
                batteryPercent=car.battery_percent*100, speedMps=car.current_speed_mps,
                distanceMeters=car.distance_meters,
                moving=car.moving, motionEdge=car.motion_edge,
                simulationControl=car.control_snapshot())


async def run(root):
    root = Path(root).resolve()
    lab = json.loads((root / 'lab.json').read_text(encoding='utf-8'))
    settings = json.loads((root / 'service/appsettings.json').read_text(encoding='utf-8'))
    multi = lab.get('multiVehicleSimulation') is True
    if not (lab.get('twoVehicleSimulation') is True or multi) or lab.get('simulationOnly') is not True:
        raise RuntimeError('Explicit multi-car synthetic instance required')
    robots = settings['Agv']['Robots']
    if not 1 <= len(robots) <= 32 or (not multi and len(robots) != 2) or any(r['IpAddress'] != '127.0.0.1' for r in [settings['Plc'], *robots]):
        raise RuntimeError('One to 32 explicit loopback cars and one loopback PLC required')
    vehicle_ids = [robot['VehicleId'] for robot in robots]
    if len(set(vehicle_ids)) != len(vehicle_ids) or any(not isinstance(v, str) or not v or Path(v).name != v or v in ('.', '..') or '/' in v or '\\' in v for v in vehicle_ids):
        raise RuntimeError('Unique path-safe virtual vehicle IDs required')
    sites = lab.get('initialSites')
    if multi and (not isinstance(sites, dict) or set(sites) != set(vehicle_ids)):
        raise RuntimeError('Explicit initialSites for every virtual car required')
    if (root / 'device-state.json').exists():
        raise RuntimeError('Fresh lab required; never silently restart active virtual devices')
    plc = SharedPlc(root, [])
    cars = []
    for index, robot in enumerate(robots):
        origin = sites[robot['VehicleId']] if sites else ['取货A', '取货B'][index]
        child = root / 'robots' / robot['VehicleId']
        child.mkdir(parents=True, exist_ok=False)
        (child / 'lab.json').write_text(json.dumps(lab, ensure_ascii=False), encoding='utf-8')
        (child / 'service-settings.json').write_text(json.dumps(settings), encoding='utf-8')
        (child / 'navigation.agvmap.json').write_bytes((root / 'navigation.agvmap.json').read_bytes())
        cars.append(Car(child, plc.memory, origin, robot['VehicleId'], root))
    plc.cars = cars
    servers = [await asyncio.start_server(plc.s7, '127.0.0.1', lab['plcPort'])]
    for robot, car in zip(robots, cars):
        for port in (robot['Port'], robot['NavigationPort']):
            servers.append(await asyncio.start_server(car.robokit, '127.0.0.1', port))
    workers = [asyncio.create_task(plc.plc_scan()), *[asyncio.create_task(c.drive()) for c in cars]]
    (root / 'device-ready').write_text(f'{len(cars)} TCP cars / one shared S7 PLC', encoding='utf-8')
    last_sequence = None
    try:
        while not (root / 'STOP').exists():
            for worker in workers:
                if worker.done():
                    worker.result()
                    raise RuntimeError('Virtual device worker stopped')
            for car in cars:
                car.refresh_controls()
            control = root / 'network-input.json'
            if control.exists():
                event = json.loads(control.read_text(encoding='utf-8'))
                if event['sequence'] != last_sequence:
                    last_sequence = event['sequence']
                    car = cars[[r['VehicleId'] for r in robots].index(event['vehicleId'])]
                    car.offline = bool(event['offline'])
                    if car.offline:
                        for writer in tuple(car.writers):
                            writer.close()
                    plc.log('network-fault', event=event)
            state = dict(time=time.time(), cars={r['VehicleId']: snapshot(c) for r,c in zip(robots,cars)},
                         memory_hex=plc.memory.hex(), contract=plc.contract,
                         dumper_reserved=plc.dumper_reserved,
                         simulationControl=plc.control_snapshot(),
                         externalInput=dict(sequence=plc.input_sequence, accepted=plc.input_accepted, error=plc.input_error))
            temporary = root / 'multi-state.tmp'
            temporary.write_text(json.dumps(state, ensure_ascii=False), encoding='utf-8')
            try:
                temporary.replace(root / 'multi-state.json')
            except PermissionError:
                pass
            await asyncio.sleep(.1)
    finally:
        for worker in workers:
            worker.cancel()
        await asyncio.gather(*workers, return_exceptions=True)
        for server in servers:
            server.close()
            await server.wait_closed()
        for device in (plc, *cars):
            for writer in tuple(device.writers):
                writer.close()
            device.journal.close()


if __name__ == '__main__':
    asyncio.run(run(sys.argv[1]))
