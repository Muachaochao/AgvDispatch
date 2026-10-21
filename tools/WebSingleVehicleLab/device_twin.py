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
import time
from pathlib import Path


class DeviceTwin:
    def __init__(self, root):
        self.root = Path(root).resolve()
        settings = self.root / 'service/appsettings.json'
        if not settings.exists():
            settings = self.root / 'service-settings.json'
        self.contract = json.loads(settings.read_text(encoding='utf-8-sig'))['HandshakeExecution']['PlcContractVersion']
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
        self.station = ids['检修']
        self.x, self.y = self.nodes[self.station]['x'], self.nodes[self.station]['y']
        self.angle = 0.0
        self.tasks = {}
        self.queue = asyncio.Queue()
        self.current = None
        self.loaded = False
        self.memory = bytearray(1024)
        self.setbit(0, 1, True)
        self.setbit(0, 3, True)
        self.setbit(2, 1, True)
        self.setbit(2, 3, True)
        self.setbit(610, 2, not self.sequence28)
        self.last_wait = False
        self.dump_started = None
        self.dumper_reserved = False
        self.steps = 0
        self.input_sequence = None
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

    def log(self, kind, **data):
        with (self.root / 'device-events.jsonl').open('a', encoding='utf-8') as f:
            f.write(json.dumps(dict(time=time.time(), kind=kind, **data), ensure_ascii=False) + '\n')

    def bit(self, byte, bit):
        return bool(self.memory[byte] & (1 << bit))

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

    async def drive(self):
        while True:
            item = await self.queue.get()
            tid = item['task_id']
            if self.tasks[tid] == 6:
                self.queue.task_done()
                continue
            self.current = tid
            self.tasks[tid] = 2
            self.log('movement-start', task_id=tid, command=item)
            points = self.path_points(item['source_id'], item['id'])
            for (ax, ay), (bx, by) in zip(points, points[1:]):
                distance = math.hypot(bx - ax, by - ay)
                # Virtual speed in m/s; capped by the actual edited route speed.
                edge = self.edges[item['source_id'], item['id']][0]
                speed = min(1.0, edge.get('speedLimit', 0.5))
                count = max(1, math.ceil(distance / speed / 0.1))
                for k in range(1, count + 1):
                    if self.tasks[tid] == 6:
                        break
                    self.x, self.y = ax + (bx-ax)*k/count, ay + (by-ay)*k/count
                    self.angle = math.atan2(by-ay, bx-ax)
                    await asyncio.sleep(0.1)
                if self.tasks[tid] == 6:
                    break
            if self.tasks[tid] != 6:
                self.station = item['id']
                operation = item.get('operation', 'Wait')
                if operation in ('JackLoad', 'JackUnload'):
                    await asyncio.sleep(2)
                    self.loaded = operation == 'JackLoad'
                    if self.station == self.site('取货A'):
                        self.setbit(0, 1, not self.loaded)
                    elif self.station == self.site('取货B'):
                        self.setbit(2, 1, not self.loaded)
                self.tasks[tid] = 4
                self.steps += 1
                self.log('movement-complete', task_id=tid, station=self.station, operation=operation, loaded=self.loaded)
            self.current = None
            self.queue.task_done()

    def site(self, name):
        return next(n['agvSiteId'] for n in self.map['nodes'] if n['id'] == name)

    def scan_handshakes(self):
        for offset in (0, 2):
            if self.bit(offset, 0) and self.bit(offset, 4):
                self.setbit(offset, 0, False)
        wait = self.bit(610, 0)
        if self.sequence28 and wait and not self.last_wait and not self.dumper_reserved and not any(self.bit(610, b) for b in (1, 3, 4)):
            self.dumper_reserved = True
            self.setbit(610, 2, True)
        if self.last_wait and not wait:
            self.setbit(610, 2, False)
        self.last_wait = wait
        if self.bit(610, 1) and self.station == self.site('倒料'):
            if self.dump_started is None:
                self.dump_started = time.monotonic()
            if time.monotonic() - self.dump_started >= 5:
                self.setbit(610, 3, True)
        if not self.bit(610, 1) and self.dump_started is not None:
            self.setbit(610, 3, False)
            self.setbit(610, 2, not self.sequence28)
            self.dump_started = None
            self.dumper_reserved = False

    async def plc_scan(self):
        while True:
            control = self.root / 'device-input.json'
            if control.exists():
                try:
                    event = json.loads(control.read_text(encoding='utf-8'))
                    if event.get('sequence') != self.input_sequence:
                        self.input_sequence = event['sequence']
                        station = event.get('call')
                        if station in ('CNC-001', 'CNC-002'):
                            offset = 0 if station == 'CNC-001' else 2
                            if self.sequence28 and (not self.bit(offset, 1) or any(self.bit(offset, b) for b in (4, 5, 6, 7))):
                                self.log('virtual-call-rejected', reason='origin cycle not closed', event=event)
                            else:
                                self.setbit(offset, 0, True)
                            self.log('external-virtual-input', event=event)
                except (OSError, ValueError, KeyError):
                    pass
            self.scan_handshakes()
            state = dict(station=self.station, x=self.x, y=self.y, angle=self.angle,
                         loaded=self.loaded, current_task=self.current, tasks=self.tasks,
                         completed_segments=self.steps, db1=list(self.memory[:4]), dumper=list(self.memory[610:611]), memory_hex=self.memory.hex(),
                         contract=self.contract, dumper_reserved=self.dumper_reserved)
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
                        battery_level=0.86, emergency=False, blocked=False,
                        navigation_status=2 if self.current else 0, errors=[], fatals=[], warnings=[],
                        vx=0.5 if self.current else 0, vy=0, w=0, charging=False)
        if api == 1110:
            wanted = request.get('task_ids', list(self.tasks))
            return dict(task_status_package=dict(task_status_list=[dict(task_id=t, status=self.tasks.get(t,404), type=1) for t in wanted]))
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
            if items[0]['source_id'] != self.station or self.current or not self.queue.empty():
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
            for server in servers:server.close()


if __name__ == '__main__':
    import argparse
    parser=argparse.ArgumentParser()
    parser.add_argument('root')
    args=parser.parse_args()
    asyncio.run(DeviceTwin(args.root).run())
