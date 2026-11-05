"""Loopback-only Robokit/S7 device twins for the unchanged production Service.

No task/database mutation, browser animation or production simulator switch.
The private instance selects its explicit PLC contract from its own settings.
New instances use plc-28-stage-v2; old isolated evidence is never migrated.
"""
import asyncio
import hashlib
import json
import math
import struct
import sys
import time
from pathlib import Path
from lab_logging import DeviceJournal
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'SimulationLab'))
from device_controls import PLC_DEFAULTS, VEHICLE_DEFAULTS, read_controls
from footprint import swept_rectangles_overlap, vehicle_extents


class DeviceTwin:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.journal = DeviceJournal(self.root)
        lab_path = self.root / 'lab.json'
        self.lab = json.loads(lab_path.read_text(encoding='utf-8-sig')) if lab_path.exists() else {}
        self.topology = self.lab.get('simulationTopology', {})
        self.vehicle_profiles = self.lab.get('vehicleProfiles', {})
        settings = self.root / 'service/appsettings.json'
        if not settings.exists():
            settings = self.root / 'service-settings.json'
        settings = json.loads(settings.read_text(encoding='utf-8-sig'))
        self.contract = settings['HandshakeExecution']['PlcContractVersion']
        self.vehicle_ids = {robot['VehicleId'] for robot in settings.get('Agv', {}).get('Robots', [])}
        self.vehicle_id = next(iter(self.vehicle_ids)) if len(self.vehicle_ids) == 1 else None
        self.body_profiles = settings['HandshakeExecution'].get('DumperExit', {}).get('BodyProfiles', [])
        self.peers = []
        self.blocked_by_vehicle = None
        self.control_root = self.root
        self.control_sequence = None
        self.control_error = None
        self.control_error_fingerprint = None
        self.speed_scale = 1.0
        self.battery_percent = 0.86
        self.voltage_curve = [dict(percent=0, voltage=21), dict(percent=100, voltage=26)]
        self.charging_gain_percent_per_hour = 20.0
        self.charging = False
        self.acceleration = 0.6
        self.operation_seconds = 2.0
        self.travel_cost_percent_per_meter = 0.01
        self.idle_cost_percent_per_hour = 0.1
        self.current_speed_mps = 0.0
        self.distance_meters = 0.0
        self.paused = self.offline = self.drop_next_response = False
        self.blocked_edges = set()
        self.writers = set()
        self.plc_controls = dict(PLC_DEFAULTS)
        self.ready_requested_at = None
        self.motion_edge = None
        self.motion_source = self.motion_target = None
        self.motion_percentage = 0.0
        self.moving = False
        self.at_source = True
        if self.contract not in ('plc-28-stage-v2', 'sender-owned-return-confirmed-v1'):
            raise RuntimeError('Unknown explicit PLC contract; refusing to infer a sequence')
        self.sequence28 = self.contract == 'plc-28-stage-v2'
        self.map_bytes = (self.root / 'navigation.agvmap.json').read_bytes()
        self.map = json.loads(self.map_bytes)
        self.md5 = hashlib.md5(self.map_bytes).hexdigest()
        self.nodes = {n['agvSiteId']: n for n in self.map['nodes']}
        self.edges = {}
        ids = {n['id']: n['agvSiteId'] for n in self.map['nodes']}
        for edge in self.map['edges']:
            a, b = ids[edge['from']], ids[edge['to']]
            self.edges[a, b] = (edge, False)
            if edge['bidirectional']:
                self.edges[b, a] = (edge, True)
        inspection = self.topology.get('inspectionSite', '检修')
        self.station = ids.get(inspection, next((value for value in ids.values() if value in self.nodes), None))
        if self.station is None:
            raise RuntimeError('Map contains no usable nodes')
        self.x, self.y = self.nodes[self.station]['x'], self.nodes[self.station]['y']
        self.angle = 0.0
        self.tasks = {}
        self.queue = asyncio.Queue()
        self.current = None
        self.loaded = False
        self.memory = bytearray(1024)
        for station in self.station_definitions():
            offset = station['offset']
            self.setbit(offset, 1, True)
            self.setbit(offset, 3, True)
        self.setbit(610, 2, not self.sequence28)
        self.last_wait = False
        self.dump_started = None
        self.dumper_reserved = False
        self.steps = 0
        self.input_sequence = None
        self.input_accepted = None
        self.input_error = None
        previous = self.root / 'device-state.json'
        if previous.exists():
            state = json.loads(previous.read_text(encoding='utf-8'))
            if any(value in (1, 2, 3) for value in state['tasks'].values()):
                raise RuntimeError('Virtual transport still active; preserve evidence and perform recovery before restarting twin.')
            self.station = state['station']
            self.x, self.y, self.angle = state['x'], state['y'], state['angle']
            self.loaded, self.tasks, self.steps = state['loaded'], state['tasks'], state['completed_segments']
            if self.sequence28 and 'contract' not in state:
                raise RuntimeError('Saved PLC cycle has no explicit contract; do not migrate old evidence')
            if state.get('contract', self.contract) != self.contract:
                raise RuntimeError('Saved PLC contract differs; preserve this instance and create a fresh lab')
            self.dumper_reserved = state.get('dumper_reserved', False)
            if 'memory_hex' in state:
                self.memory[:] = bytes.fromhex(state['memory_hex'])
            else:
                self.memory[:4] = bytes(state['db1'])
                self.memory[610:611] = bytes(state['dumper'])
            node = self.nodes.get(self.station)
            self.at_source = bool(node and math.hypot(self.x-node['x'], self.y-node['y']) <= 0.02)

    def log(self, kind, **data):
        self.journal.write(kind, **data)

    def bit(self, byte, bit):
        return bool(self.memory[byte] & (1 << bit))

    def station_definitions(self):
        configured = self.topology.get('stations')
        if isinstance(configured, list) and configured:
            return configured
        return [dict(stationId='CNC-001', pickupSite='取货A', offset=0),
                dict(stationId='CNC-002', pickupSite='取货B', offset=2)]

    def semantic_site(self, key, fallback):
        return self.topology.get(key, fallback)

    def apply_vehicle_profile(self):
        profile = self.vehicle_profiles.get(self.vehicle_id, {})
        self.loaded = bool(profile.get('initialLoaded', False))
        self.battery_percent = float(profile.get('batteryPercent', 86)) / 100.0
        self.voltage_curve = sorted(profile.get('voltageCurve', self.voltage_curve), key=lambda point: point['percent'])
        self.charging_gain_percent_per_hour = float(profile.get('chargingGainPercentPerHour', 20))
        self.speed_scale = float(profile.get('speedScale', 1.0))
        self.acceleration = float(profile.get('accelerationMetersPerSecondSquared', .6))
        self.operation_seconds = float(profile.get('operationSeconds', 2.0))
        self.travel_cost_percent_per_meter = float(profile.get('travelCostPercentPerMeter', .01))
        self.idle_cost_percent_per_hour = float(profile.get('idleCostPercentPerHour', .1))

    def consume_battery(self, distance=0.0, seconds=0.0):
        if self.charging:
            self.battery_percent = min(1.0, self.battery_percent + self.charging_gain_percent_per_hour / 100.0 * seconds / 3600.0)
            return
        cost_percent = distance * self.travel_cost_percent_per_meter + seconds * self.idle_cost_percent_per_hour / 3600.0
        self.battery_percent = max(0.0, self.battery_percent - cost_percent / 100.0)

    def battery_voltage(self):
        percent = self.battery_percent * 100
        if percent <= self.voltage_curve[0]['percent']: return self.voltage_curve[0]['voltage']
        for left, right in zip(self.voltage_curve, self.voltage_curve[1:]):
            if percent <= right['percent']:
                ratio = (percent - left['percent']) / (right['percent'] - left['percent'])
                return left['voltage'] + (right['voltage'] - left['voltage']) * ratio
        return self.voltage_curve[-1]['voltage']

    def setbit(self, byte, bit, value):
        before = self.bit(byte, bit)
        if value:
            self.memory[byte] |= 1 << bit
        else:
            self.memory[byte] &= ~(1 << bit)
        if before != bool(value):
            self.log('plc-bit', address=f'DB1.DBX{byte}.{bit}', before=before, value=bool(value))

    def path_points(self, a, b):
        edge, reverse = self.edges[a, b]
        start, end = (self.nodes[b], self.nodes[a]) if reverse else (self.nodes[a], self.nodes[b])
        points = [(start['x'], start['y'])]
        controls = edge.get('controlPoints') or []
        if edge.get('geometryKind') == 'CubicBezier' and len(controls) == 2:
            p0, p1, p2, p3 = start, controls[0], controls[1], end
            points = []
            for i in range(65):
                t, u = i / 64, 1 - i / 64
                points.append(tuple(u**3*p0[k] + 3*u*u*t*p1[k] + 3*u*t*t*p2[k] + t**3*p3[k] for k in ('x', 'y')))
        else:
            points.append((end['x'], end['y']))
        return list(reversed(points)) if reverse else points

    def refresh_controls(self):
        control = self.control_root / 'simulation-control.json'
        if not control.exists():
            return
        try:
            value = read_controls(control, self.vehicle_ids or None,
                                  {edge.get('id') for edge, _ in self.edges.values()})
            if value['sequence'] == self.control_sequence:
                self.control_error = None
                return
            car = value['vehicles'].get(self.vehicle_id, dict(VEHICLE_DEFAULTS))
            self.speed_scale, self.paused = car['speedScale'], car['paused']
            self.charging = car['charging']
            self.offline, self.drop_next_response = car['offline'], car['dropNextResponse']
            self.blocked_edges = set(car['blockedEdges'])
            self.plc_controls = value['plc']
            self.control_sequence, self.control_error = value['sequence'], None
            if self.offline:
                for writer in tuple(self.writers):
                    writer.close()
            self.log('simulation-control-applied', sequence=self.control_sequence,
                     vehicle_id=self.vehicle_id, vehicle=car, plc=self.plc_controls)
        except (OSError, ValueError, TypeError, KeyError) as ex:
            self.control_error = str(ex)
            if self.control_error != self.control_error_fingerprint:
                self.log('simulation-control-rejected', error=self.control_error)
                self.control_error_fingerprint = self.control_error

    def control_snapshot(self):
        return dict(sequence=self.control_sequence, error=self.control_error,
                    speedScale=self.speed_scale, paused=self.paused,
                    offline=self.offline, responseDropPending=self.drop_next_response,
                    blockedEdges=sorted(self.blocked_edges), charging=self.charging, plc=self.plc_controls)

    def at_site(self, site):
        node = self.nodes[site]
        return self.station == site and self.at_source and not self.moving and math.hypot(self.x-node['x'], self.y-node['y']) <= 0.02

    async def wait_motion(self, tid, seconds, edge_id=None):
        """Physical time stops on pause/block, but networking and PLC keep scanning."""
        elapsed = 0.0
        while elapsed < seconds:
            if self.tasks[tid] == 6:
                return False
            if not self.paused and edge_id not in self.blocked_edges:
                elapsed += 0.1 * self.speed_scale
                self.consume_battery(seconds=0.1 * self.speed_scale)
            await asyncio.sleep(0.1)
        return self.tasks[tid] != 6

    async def drive(self):
        while True:
            item = await self.queue.get()
            tid = item['task_id']
            if self.tasks[tid] == 6:
                self.queue.task_done()
                continue
            if not self.at_site(item['source_id']):
                # A cancelled preceding segment may leave the virtual robot
                # between nodes. Never jump back to the next command's source.
                self.tasks[tid] = 5
                self.log('movement-rejected', task_id=tid, reason='physical source mismatch',
                         station=self.station, x=self.x, y=self.y, source_id=item['source_id'])
                self.queue.task_done()
                continue
            self.current = tid
            self.tasks[tid] = 2
            self.log('movement-start', task_id=tid, command=item)
            points = self.path_points(item['source_id'], item['id'])
            edge = self.edges[item['source_id'], item['id']][0]
            self.motion_edge = edge.get('id')
            self.motion_source, self.motion_target = item['source_id'], item['id']
            self.motion_percentage = 0.0
            route_length = sum(math.hypot(bx-ax, by-ay)
                               for (ax, ay), (bx, by) in zip(points, points[1:]))
            # Carry distance across curve samples within each tick. Waiting one
            # tick per Bezier sample would make acceleration depend on geometry.
            speed = min(1.0, edge.get('speedLimit', 0.5))
            if speed <= 0:
                raise ValueError('Virtual route speed must be positive')
            segment, progress = 0, 0.0
            moved_meters = 0.0
            while segment < len(points) - 1:
                if self.tasks[tid] == 6:
                    break
                self.moving = False
                prior_blocker = next((p for p in self.peers if p.vehicle_id == self.blocked_by_vehicle), None)
                if prior_blocker is not None and math.hypot(self.x-prior_blocker.x, self.y-prior_blocker.y) < self.body_radius()+prior_blocker.body_radius()+0.15:
                    self.current_speed_mps = 0.0
                    self.consume_battery(seconds=0.1)
                    await asyncio.sleep(0.1)
                    continue
                self.blocked_by_vehicle = None
                if not self.paused and self.motion_edge not in self.blocked_edges:
                    target_speed = speed * self.speed_scale
                    self.current_speed_mps = min(target_speed, self.current_speed_mps + self.acceleration * 0.1)
                    budget = self.current_speed_mps * 0.1
                    while segment < len(points) - 1:
                        (ax, ay), (bx, by) = points[segment:segment+2]
                        distance = math.hypot(bx-ax, by-ay)
                        advance = min(budget, distance-progress)
                        nx, ny = ((ax+(bx-ax)*(progress+advance)/distance,
                                   ay+(by-ay)*(progress+advance)/distance) if distance else (bx, by))
                        blocker = self.movement_blocker(nx, ny)
                        if blocker is not None:
                            self.blocked_by_vehicle = blocker
                            self.current_speed_mps = 0.0
                            break
                        moved_meters += advance
                        if advance > 0:
                            self.at_source = False
                            self.moving = True
                        progress += advance
                        budget -= advance
                        if distance:
                            self.x, self.y = ax+(bx-ax)*progress/distance, ay+(by-ay)*progress/distance
                            self.angle = math.atan2(by-ay, bx-ax)
                        if progress >= distance - 1e-10:
                            segment, progress = segment+1, 0.0
                        if budget <= 1e-10:
                            break
                    self.motion_percentage = min(1.0, moved_meters / route_length) if route_length else 1.0
                await asyncio.sleep(0.1)
                if not self.moving:
                    self.current_speed_mps = max(0.0, self.current_speed_mps - self.acceleration * 0.1)
                    self.consume_battery(seconds=0.1)
            self.distance_meters += moved_meters
            self.consume_battery(distance=moved_meters)
            self.current_speed_mps = 0.0
            if self.tasks[tid] != 6:
                self.station = item['id']
                self.moving = False
                self.at_source = True
                operation = item.get('operation', 'Wait')
                if operation in ('JackLoad', 'JackUnload'):
                    if not await self.wait_motion(tid, self.operation_seconds):
                        self.current = None
                        self.motion_edge = None
                        self.queue.task_done()
                        continue
                    self.loaded = operation == 'JackLoad'
                    for station in self.station_definitions():
                        if self.station == self.site(station['pickupSite']):
                            self.setbit(station['offset'], 1, not self.loaded)
                self.tasks[tid] = 4
                self.steps += 1
                self.log('movement-complete', task_id=tid, station=self.station, operation=operation, loaded=self.loaded)
            self.current = None
            self.motion_edge = None
            self.moving = False
            self.blocked_by_vehicle = None
            self.queue.task_done()

    def body_radius(self):
        profiles = [p for p in self.body_profiles if p.get('VehicleId') == self.vehicle_id]
        source = profiles[0] if len(profiles) == 1 else (self.vehicle_profiles.get(self.vehicle_id) or {})
        radius = source.get('RadiusFromReportedReferenceMeters', source.get('radiusMeters'))
        if radius is not None:
            return float(radius)
        length, width = vehicle_extents(source)
        return max(length, width) / 2

    def body_extents(self):
        profiles = [p for p in self.body_profiles if p.get('VehicleId') == self.vehicle_id]
        source = profiles[0] if len(profiles) == 1 else (self.vehicle_profiles.get(self.vehicle_id) or {})
        return vehicle_extents(source)

    def movement_blocker(self, nx, ny):
        """Synthetic near-obstacle stop; independent collision verdict stays enabled."""
        dx, dy = nx-self.x, ny-self.y
        length2 = dx*dx+dy*dy
        if length2 <= 1e-20:
            return None
        angle = math.atan2(dy, dx)
        length, width = self.body_extents()
        candidate = dict(x=nx, y=ny, angle=angle, lengthMeters=length, widthMeters=width)
        current = dict(x=self.x, y=self.y, angle=self.angle, lengthMeters=length, widthMeters=width)
        for peer in self.peers:
            peer_length, peer_width = peer.body_extents()
            peer_footprint = dict(x=peer.x, y=peer.y, angle=peer.angle,
                                  lengthMeters=peer_length, widthMeters=peer_width)
            if swept_rectangles_overlap(current, candidate, peer_footprint, peer_footprint, margin=.05):
                return peer.vehicle_id
        return None

    def site(self, name):
        return next(n['agvSiteId'] for n in self.map['nodes'] if n['id'] == name)

    def scan_handshakes(self):
        if self.control_sequence is not None:
            self.setbit(610, 4, self.plc_controls['fault'])
        for offset in (station['offset'] for station in self.station_definitions()):
            if self.bit(offset, 0) and self.bit(offset, 4):
                self.setbit(offset, 0, False)
        wait = self.bit(610, 0)
        if self.sequence28 and wait and not self.last_wait and not self.dumper_reserved and not any(self.bit(610, b) for b in (1, 3, 4)):
            self.dumper_reserved = True
            self.ready_requested_at = time.monotonic()
        if self.dumper_reserved and wait and self.ready_requested_at is not None:
            ready = time.monotonic() - self.ready_requested_at >= self.plc_controls['readyDelaySeconds']
            self.setbit(610, 2, ready and not self.plc_controls['suppressReady'] and not self.bit(610, 4))
        if self.last_wait and not wait:
            self.setbit(610, 2, False)
        self.last_wait = wait
        if self.bit(610, 1) and self.at_site(self.site(self.semantic_site('dumperSite', '倒料'))):
            if self.dump_started is None:
                self.dump_started = time.monotonic()
            if time.monotonic() - self.dump_started >= self.plc_controls['doneDelaySeconds'] and not self.plc_controls['suppressDone'] and not self.bit(610, 4):
                self.setbit(610, 3, True)
        if self.plc_controls['suppressDone'] or self.bit(610, 4):
            self.setbit(610, 3, False)
        if not self.bit(610, 1) and self.dump_started is not None:
            self.setbit(610, 3, False)
            self.setbit(610, 2, not self.sequence28)
            self.dump_started = None
            self.dumper_reserved = False
            self.ready_requested_at = None

    def apply_external_input(self, event):
        """Publish all requested P-side calls in one scan, or publish none."""
        if not isinstance(event, dict):
            raise ValueError('External device input must be a JSON object')
        if event.get('sequence') == self.input_sequence:
            return
        self.input_sequence = event['sequence']
        self.input_accepted, self.input_error = False, None
        try:
            if 'calls' in event:
                stations = event['calls']
                if 'call' in event or not isinstance(stations, list) or not 1 <= len(stations) <= 2 or any(not isinstance(s, str) for s in stations) or len(set(stations)) != len(stations):
                    raise ValueError('calls must contain one or two unique supported station IDs')
            else:
                stations = [event.get('call')]
            configured = {station['stationId']: station for station in self.station_definitions()}
            if any(station not in configured for station in stations):
                raise ValueError('Unsupported virtual station call')
            offsets = [configured[station]['offset'] for station in stations]
            if self.sequence28 and any(not self.bit(offset, 1) or any(self.bit(offset, b) for b in (4, 5, 6, 7)) for offset in offsets):
                raise ValueError('origin cycle not closed')
            for offset in offsets:
                self.setbit(offset, 0, True)
            self.input_accepted = True
        except ValueError as error:
            self.input_error = str(error)
            self.log('virtual-call-rejected', reason=self.input_error, event=event)
        self.log('external-virtual-input', accepted=self.input_accepted, event=event)

    async def plc_scan(self):
        while True:
            self.refresh_controls()
            control = self.root / 'device-input.json'
            if control.exists():
                try:
                    event = json.loads(control.read_text(encoding='utf-8'))
                    self.apply_external_input(event)
                except (OSError, ValueError, KeyError):
                    pass
            self.scan_handshakes()
            state = dict(station=self.station, x=self.x, y=self.y, angle=self.angle,
                         loaded=self.loaded, current_task=self.current, tasks=self.tasks,
                         completed_segments=self.steps, db1=list(self.memory[:4]), dumper=list(self.memory[610:611]), memory_hex=self.memory.hex(),
                         contract=self.contract, dumper_reserved=self.dumper_reserved,
                         batteryPercent=self.battery_percent * 100, batteryVoltage=self.battery_voltage(), charging=self.charging,
                         speedMps=self.current_speed_mps,
                         distanceMeters=self.distance_meters, simulationControl=self.control_snapshot(), time=time.time(),
                         externalInput=dict(sequence=self.input_sequence, accepted=self.input_accepted, error=self.input_error))
            temp = self.root / 'device-state.tmp'
            temp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding='utf-8')
            try:
                temp.replace(self.root / 'device-state.json')
            except PermissionError:
                # Windows readers may briefly hold the previous snapshot. Never
                # stop virtual PLC scanning because a diagnostic file is busy.
                pass
            await asyncio.sleep(0.1)

    def robokit_reply(self, api, request):
        if api == 1100:
            return dict(x=self.x, y=self.y, angle=self.angle, confidence=0.99,
                        current_station=self.station, last_station=self.station,
                        battery_level=self.battery_percent, battery_voltage=self.battery_voltage(), emergency=False,
                        blocked=self.blocked_by_vehicle is not None or self.motion_edge in self.blocked_edges,
                        navigation_status=2 if self.current else 0, errors=[], fatals=[], warnings=[],
                        vx=self.current_speed_mps if self.current and not self.paused else 0, vy=0, w=0, charging=self.charging)
        if api == 1110:
            wanted = request.get('task_ids', list(self.tasks))
            result = dict(task_status_package=dict(task_status_list=[dict(task_id=t, status=self.tasks.get(t,404), type=1) for t in wanted]))
            if self.current is not None and self.motion_source is not None:
                result.update(source_name=self.motion_source, target_name=self.motion_target,
                              percentage=self.motion_percentage)
            return result
        if api == 1000:
            return dict(version='3.5-local-twin', protocol_version='2', model='LocalTcpTwin')
        if api == 1300:
            return dict(current_map='single_vehicle_lab', current_map_md5=self.md5)
        if api == 1022:
            return dict(loadmap_status=1)
        if api == 1060:
            return dict(locked=False, nick_name='local-lab')
        if api == 1301:
            return dict(stations=[dict(id=k,x=n['x'],y=n['y'],r=0,type='LocationMark',desc=n['id']) for k,n in self.nodes.items()])
        if api == 1302:
            return dict(map_info=[dict(name='single_vehicle_lab.smap',md5=self.md5)])
        if api == 1303:
            a,b=request.get('source_id'),request.get('target_id')
            return dict(list=[a,b]) if (a,b) in self.edges else dict(ret_code=400,err_msg='Not a direct map edge')
        if api == 3066:
            items=request['move_task_list']
            if not isinstance(items,list) or not items or len(items)>2048:
                return dict(ret_code=400,err_msg='Invalid task chain length')
            if len({item['task_id'] for item in items})!=len(items) or any(a['id']!=b['source_id'] for a,b in zip(items,items[1:])):
                return dict(ret_code=400,err_msg='Duplicate ids or discontinuous task chain')
            if any((item['source_id'],item['id']) not in self.edges for item in items):
                return dict(ret_code=400,err_msg='Unknown or reverse-only edge')
            if not self.at_site(items[0]['source_id']) or self.current or not self.queue.empty():
                return dict(ret_code=400,err_msg='Vehicle busy or source mismatch')
            for item in items:
                if item['task_id'] in self.tasks:
                    return dict(ret_code=400,err_msg='Duplicate id')
            for item in items:
                self.tasks[item['task_id']]=1
                self.queue.put_nowait(item)
            return dict(ret_code=0)
        if api == 3003:
            tid=request.get('taskId')
            if tid not in self.tasks:
                return dict(ret_code=400,err_msg='Task absent')
            self.tasks[tid]=6
            return dict(ret_code=0)
        return dict(ret_code=400,err_msg=f'Unsupported twin API {api}')

    async def robokit(self, reader, writer):
        self.refresh_controls()
        if self.offline:
            writer.close()
            return
        self.writers.add(writer)
        try:
            while True:
                header=await reader.readexactly(16)
                if header[0] != 0x5a:
                    raise ValueError('Invalid sync')
                length=struct.unpack_from('>I',header,4)[0]
                if length > 1024*1024:
                    raise ValueError('Frame budget')
                payload=await reader.readexactly(length)
                api=struct.unpack_from('>H',header,8)[0]
                request=json.loads(payload) if payload else {}
                response=self.robokit_reply(api,request)
                self.log('robokit', api=api, sequence=struct.unpack_from('>H',header,2)[0], request=request,response=response)
                if api == 3066 and response.get('ret_code') == 0 and self.drop_next_response:
                    self.drop_next_response = False
                    self.log('simulation-response-dropped', api=api, sequence=self.control_sequence)
                    return
                data=json.dumps(response,ensure_ascii=False).encode()
                result=bytearray(header)
                struct.pack_into('>I',result,4,len(data))
                struct.pack_into('>H',result,8,api+10000)
                writer.write(result+data)
                await writer.drain()
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        except Exception as ex:
            self.log('robokit-error', error=str(ex))
        finally:
            self.writers.discard(writer)
            writer.close()

    async def s7(self, reader, writer):
        try:
            while True:
                head=await reader.readexactly(4)
                size=struct.unpack_from('>H',head,2)[0]
                if size < 7 or size > 65535:
                    raise ValueError('S7 frame length')
                packet=head+await reader.readexactly(size-4)
                if packet[5] == 0xe0:
                    response=bytearray(packet);response[5]=0xd0
                    writer.write(response);await writer.drain();continue
                if packet[7] != 0x32:
                    raise ValueError('S7 protocol')
                plen,dlen=struct.unpack_from('>HH',packet,13)
                param=packet[17:17+plen]
                func=param[0]
                data=bytearray()
                if func == 0xf0:
                    answer_param=b'\xf0\x00\x00\x01\x00\x01\x03\xc0'
                elif func in (4,5):
                    answer_param=bytes([func,param[1]])
                    cursor=17+plen
                    for index in range(param[1]):
                        var=param[2+index*12:14+index*12]
                        kind=var[3]; count=int.from_bytes(var[4:6],'big')
                        db=int.from_bytes(var[6:8],'big');area=var[8]
                        bit_address=int.from_bytes(var[9:12],'big')
                        byte_address,bit=divmod(bit_address,8)
                        if db != 1 or area != 0x84 or byte_address+count > len(self.memory):
                            raise ValueError('Only bounded DB1 supported')
                        if func == 4:
                            raw=bytes([int(self.bit(byte_address,bit))]) if kind==1 else bytes(self.memory[byte_address:byte_address+count])
                            data.extend(bytes([0xff,3 if kind==1 else 4])+struct.pack('>H',count if kind==1 else len(raw)*8)+raw)
                            if len(raw)%2 and index < param[1]-1:data.append(0)
                        else:
                            transport=packet[cursor+1];bits=int.from_bytes(packet[cursor+2:cursor+4],'big')
                            n=(bits+7)//8 if transport!=3 else (bits+7)//8
                            raw=packet[cursor+4:cursor+4+n]
                            if kind==1:self.setbit(byte_address,bit,raw[0]!=0)
                            else:self.memory[byte_address:byte_address+len(raw)]=raw
                            self.log('s7-write',byte=byte_address,bit=bit,variable_kind=kind,hex=raw.hex())
                            cursor+=4+n+(n%2 if index<param[1]-1 else 0)
                            data.append(0xff)
                else:
                    raise ValueError(f'S7 function {func}')
                s7=b'\x32\x03\x00\x00'+packet[11:13]+struct.pack('>HH',len(answer_param),len(data))+b'\x00\x00'+answer_param+data
                reply=b'\x02\xf0\x80'+s7
                writer.write(b'\x03\x00'+struct.pack('>H',len(reply)+4)+reply)
                await writer.drain()
        except (asyncio.IncompleteReadError,ConnectionError):
            pass
        except Exception as ex:
            self.log('s7-error',error=str(ex),packet=packet.hex() if 'packet' in locals() else '')
        finally:
            writer.close()

    async def run(self):
        config=json.loads((self.root/'lab.json').read_text(encoding='utf-8'))
        servers=[]
        for port,handler in [(config['robotPort'],self.robokit),(config['navigationPort'],self.robokit),(config['plcPort'],self.s7)]:
            servers.append(await asyncio.start_server(handler,'127.0.0.1',port))
        self.log('started', ports=config, map_sha256=hashlib.sha256(self.map_bytes).hexdigest())
        workers=[asyncio.create_task(self.drive()),asyncio.create_task(self.plc_scan())]
        (self.root/'device-ready').write_text('loopback-only',encoding='utf-8')
        try:
            while not (self.root/'STOP').exists():
                for worker in workers:
                    if worker.done():
                        worker.result()
                        raise RuntimeError('Device worker stopped unexpectedly')
                await asyncio.sleep(0.2)
        finally:
            for worker in workers:worker.cancel()
            await asyncio.gather(*workers, return_exceptions=True)
            for server in servers:
                server.close()
                await server.wait_closed()
            for writer in tuple(self.writers):writer.close()
            self.journal.close()


if __name__ == '__main__':
    import argparse
    parser=argparse.ArgumentParser()
    parser.add_argument('root')
    args=parser.parse_args()
    asyncio.run(DeviceTwin(args.root).run())
