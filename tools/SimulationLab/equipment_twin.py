"""Optional elevator/door and signal-generator simulation; not a dispatch contract."""
import json
import threading
import time
import uuid
from pathlib import Path


class EquipmentModel:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.lock = threading.RLock()
        self.stop_event = threading.Event()
        self.state = dict(running=True, sequence=None, error=None, floors=['1','2'], currentFloor='1', targetFloor=None,
                          direction='Stopped', door='Closed', moving=False, progress=0.0, fault=False,
                          signals={}, signalEvents=[], schedules=[])
        self.travel_seconds = 2.0; self.door_seconds = 1.0
        self.thread = threading.Thread(target=self._run, daemon=True); self.thread.start()
        self._write()

    def _write(self):
        value = dict(time=time.time(), **self.state)
        temporary = self.root / 'equipment-state.tmp'
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
        temporary.replace(self.root / 'equipment-state.json')

    def status(self):
        with self.lock: return dict(self.state, time=time.time())

    def control(self, payload):
        if not isinstance(payload, dict) or set(payload) - {'sequence', 'elevator', 'signals', 'travelSeconds', 'doorSeconds'}:
            raise ValueError('设备控制字段无效。')
        sequence = payload.get('sequence')
        if not isinstance(sequence, str) or not sequence or len(sequence) > 128: raise ValueError('设备控制序号无效。')
        with self.lock:
            if payload.get('travelSeconds') is not None:
                self.travel_seconds = self._bounded(payload['travelSeconds'], .1, 300, '运行时间')
            if payload.get('doorSeconds') is not None:
                self.door_seconds = self._bounded(payload['doorSeconds'], .1, 60, '开关门时间')
            elevator = payload.get('elevator', {})
            if not isinstance(elevator, dict) or set(elevator) - {'targetFloor', 'doorCommand', 'fault', 'reset'}:
                raise ValueError('电梯控制字段无效。')
            if 'targetFloor' in elevator:
                floor = str(elevator['targetFloor'])
                if floor not in self.state['floors']: raise ValueError('目标楼层不存在。')
                if self.state['fault']: raise ValueError('电梯故障状态下禁止运行。')
                if self.state['door'] != 'Closed': raise ValueError('门未关闭，禁止电梯运行。')
                self.state['targetFloor'] = floor
            if 'doorCommand' in elevator:
                command = elevator['doorCommand']
                if command not in ('open', 'close'): raise ValueError('门控制只支持open或close。')
                if self.state['moving']: raise ValueError('电梯运行中禁止开关门。')
                self.state['door'] = 'Opening' if command == 'open' else 'Closing'
            if 'fault' in elevator:
                if not isinstance(elevator['fault'], bool): raise ValueError('故障标志必须为布尔值。')
                self.state['fault'] = elevator['fault']
                if elevator['fault']:
                    self.state.update(targetFloor=None, moving=False, direction='Stopped', door='Faulted')
            if elevator.get('reset') is True:
                self.state.update(fault=False, targetFloor=None, moving=False, direction='Stopped', door='Closed', progress=0.0)
            signals = payload.get('signals', {})
            if not isinstance(signals, dict) or set(signals) - {'set', 'schedule', 'clearSchedule'}:
                raise ValueError('信号控制字段无效。')
            if 'set' in signals:
                self._set_signals(signals['set'], 'manual')
            if signals.get('clearSchedule') is True:
                self.state['schedules'].clear()
            if 'schedule' in signals:
                entries = signals['schedule']
                if not isinstance(entries, list) or len(entries) > 200: raise ValueError('信号计划须为最多200项数组。')
                now = time.monotonic()
                for entry in entries:
                    if not isinstance(entry, dict) or set(entry) != {'id', 'afterMs', 'set'}:
                        raise ValueError('信号计划条目字段无效。')
                    if not isinstance(entry['id'], str) or not entry['id'] or len(entry['id']) > 64:
                        raise ValueError('信号计划编号无效。')
                    delay = self._bounded(entry['afterMs'], 0, 3600000, '信号延迟') / 1000
                    self.state['schedules'].append(dict(id=entry['id'], due=now + delay, set=self._validate_signals(entry['set'])))
            self.state['sequence'] = sequence; self.state['error'] = None
            self._write()
            return self.status()

    @staticmethod
    def _bounded(value, low, high, name):
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not low <= value <= high:
            raise ValueError(f'{name}须在{low}至{high}之间。')
        return float(value)

    @staticmethod
    def _validate_signals(value):
        if not isinstance(value, dict) or len(value) > 128: raise ValueError('信号集合必须是对象。')
        result = {}
        for key, item in value.items():
            if not isinstance(key, str) or not key or len(key) > 64: raise ValueError('信号名称无效。')
            if isinstance(item, bool) or (isinstance(item, (int, float)) and not isinstance(item, bool)):
                result[key] = item
            elif isinstance(item, str):
                result[key] = item[:256]
            else: raise ValueError('信号值只支持布尔、数字或短字符串。')
        return result

    def _set_signals(self, value, source):
        validated = self._validate_signals(value)
        self.state['signals'].update(validated)
        self.state['signalEvents'].append(dict(time=time.time(), source=source, set=validated))
        self.state['signalEvents'] = self.state['signalEvents'][-200:]

    def _run(self):
        last = time.monotonic()
        while not self.stop_event.wait(.1):
            now = time.monotonic(); dt = now - last; last = now
            with self.lock:
                due = [entry for entry in self.state['schedules'] if entry['due'] <= now]
                if due:
                    for entry in due:
                        self._set_signals(entry['set'], entry['id'])
                    self.state['schedules'] = [entry for entry in self.state['schedules'] if entry['due'] > now]
                if self.state['door'] in ('Opening', 'Closing'):
                    self.state[f'_{self.state["door"].lower()}'] = self.state.get(f'_{self.state["door"].lower()}', 0) + dt
                    key = f'_{self.state["door"].lower()}'
                    if self.state[key] >= self.door_seconds:
                        self.state['door'] = 'Open' if self.state['door'] == 'Opening' else 'Closed'
                        self.state[key] = 0
                if self.state['targetFloor'] and not self.state['fault']:
                    if not self.state['moving']:
                        if self.state['door'] != 'Closed': self.state['door'] = 'Closing'
                        else:
                            self.state['moving'] = True
                            self.state['direction'] = 'Up' if int(self.state['targetFloor']) > int(self.state['currentFloor']) else 'Down'
                    if self.state['moving']:
                        self.state['progress'] += dt / self.travel_seconds
                        if self.state['progress'] >= 1:
                            self.state['currentFloor'] = self.state['targetFloor']
                            self.state['targetFloor'] = None; self.state['moving'] = False
                            self.state['direction'] = 'Stopped'; self.state['progress'] = 0.0
                self._write()

    def stop(self):
        self.stop_event.set()
        if self.thread.is_alive() and threading.current_thread() is not self.thread: self.thread.join(timeout=2)
        with self.lock:
            self.state['running'] = False
            self._write()
