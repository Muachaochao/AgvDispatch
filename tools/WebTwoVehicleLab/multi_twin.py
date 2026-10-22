"""Two independent TCP cars, one shared S7 DB. Synthetic loopback devices only."""
import asyncio
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'WebSingleVehicleLab'))
from device_twin import DeviceTwin


class Car(DeviceTwin):
    def __init__(self, root, memory, initial_site):
        super().__init__(root)
        self.memory = memory
        self.station = self.site(initial_site)
        self.x, self.y = self.nodes[self.station]['x'], self.nodes[self.station]['y']
        self.offline = False
        self.writers = set()

    async def robokit(self, reader, writer):
        if self.offline:
            writer.close()
            return
        self.writers.add(writer)
        try:
            await super().robokit(reader, writer)
        finally:
            self.writers.discard(writer)


class SharedPlc(DeviceTwin):
    def __init__(self, root, cars):
        super().__init__(root)
        self.cars = cars

    def scan_handshakes(self):
        # The equipment sees either car's physical arrival, never a command alone.
        present = [c for c in self.cars if c.station == c.site('倒料')]
        self.station = self.site('倒料') if present else self.site('检修')
        super().scan_handshakes()


def snapshot(car):
    return dict(station=car.station, x=car.x, y=car.y, angle=car.angle,
                loaded=car.loaded, current_task=car.current, tasks=car.tasks,
                completed_segments=car.steps, offline=car.offline)


async def run(root):
    root = Path(root).resolve()
    lab = json.loads((root / 'lab.json').read_text(encoding='utf-8'))
    settings = json.loads((root / 'service/appsettings.json').read_text(encoding='utf-8'))
    if lab.get('twoVehicleSimulation') is not True or not lab.get('simulationOnly'):
        raise RuntimeError('Explicit two-car synthetic instance required')
    robots = settings['Agv']['Robots']
    if len(robots) != 2 or any(r['IpAddress'] != '127.0.0.1' for r in [settings['Plc'], *robots]):
        raise RuntimeError('Exactly two loopback cars and one loopback PLC required')
    if (root / 'device-state.json').exists():
        raise RuntimeError('Fresh lab required; never silently restart active virtual devices')
    plc = SharedPlc(root, [])
    cars = []
    for robot, origin in zip(robots, ['取货A', '取货B']):
        child = root / 'robots' / robot['VehicleId']
        child.mkdir(parents=True, exist_ok=False)
        (child / 'service-settings.json').write_text(json.dumps(settings), encoding='utf-8')
        (child / 'navigation.agvmap.json').write_bytes((root / 'navigation.agvmap.json').read_bytes())
        cars.append(Car(child, plc.memory, origin))
    plc.cars = cars
    servers = [await asyncio.start_server(plc.s7, '127.0.0.1', lab['plcPort'])]
    for robot, car in zip(robots, cars):
        for port in (robot['Port'], robot['NavigationPort']):
            servers.append(await asyncio.start_server(car.robokit, '127.0.0.1', port))
    workers = [asyncio.create_task(plc.plc_scan()), *[asyncio.create_task(c.drive()) for c in cars]]
    (root / 'device-ready').write_text('two TCP cars / one shared S7 PLC', encoding='utf-8')
    last_sequence = None
    try:
        while not (root / 'STOP').exists():
            for worker in workers:
                if worker.done():
                    worker.result()
                    raise RuntimeError('Virtual device worker stopped')
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
                         dumper_reserved=plc.dumper_reserved)
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
        for server in servers:
            server.close()


if __name__ == '__main__':
    asyncio.run(run(sys.argv[1]))
