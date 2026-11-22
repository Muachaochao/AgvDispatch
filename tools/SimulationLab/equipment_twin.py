"""Optional elevator/door and signal-generator simulation; not a dispatch contract."""
import json
import copy
import math
import threading
import time
import uuid
from pathlib import Path

import simulation_clock


class EquipmentModel:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.lock = threading.RLock()
        self.stop_event = threading.Event()
        self.state = dict(running=True, sequence=None, error=None, floors=['1','2'], currentFloor='1', targetFloor=None,
                          direction='Stopped', door='Closed', moving=False, progress=0.0, fault=False,
                          signals={}, signalEvents=[], schedules=[])
        self.travel_seconds = 2.0; self.door_seconds = 1.0
        self.last_tick = simulation_clock.CLOCK.time()
        self._write()
        self.thread = threading.Thread(target=self._run, daemon=True); self.thread.start()

    def _write(self):
        value = dict(time=self.last_tick, **self.state)
        value['error'] = None
        temporary = self.root / 'equipment-state.tmp'
        try:
            temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')
            temporary.replace(self.root / 'equipment-state.json')
        except OSError as exc:
            # Readers can hold the destination briefly on Windows. Retry on the
            # next tick without killing the model or claiming the file was saved.
            self.state['error'] = f'设备状态保存失败，正在重试：{exc}'
        else:
            self.state['error'] = None

    def status(self):
        with self.lock: return dict(copy.deepcopy(self.state), time=self.last_tick)

    def control(self, payload):
        if not isinstance(payload, dict) or set(payload) - {'sequence', 'elevator', 'signals', 'travelSeconds', 'doorSeconds'}:
            raise ValueError('设备控制字段无效。')
        sequence = payload.get('sequence')
        if not isinstance(sequence, str) or not sequence or len(sequence) > 128: raise ValueError('设备控制序号无效。')
        with self.lock:
            # Validate the whole request against an isolated copy. A rejected
            # signal plan must not have already started the elevator or door.
            old_state = self.state
            old_travel, old_door = self.travel_seconds, self.door_seconds
            self.state = copy.deepcopy(old_state)
            try:
                self._apply_control(payload, sequence)
            except (ValueError, TypeError):
                self.state = old_state
                self.travel_seconds, self.door_seconds = old_travel, old_door
                raise
            self._write()
            return self.status()

    def _apply_control(self, payload, sequence):
        if payload.get('travelSeconds') is not None:
            self.travel_seconds = self._bounded(payload['travelSeconds'], .1, 300, '运行时间')
        if payload.get('doorSeconds') is not None:
            self.door_seconds = self._bounded(payload['doorSeconds'], .1, 60, '开关门时间')
        elevator = payload.get('elevator', {})
        if not isinstance(elevator, dict) or set(elevator) - {'targetFloor', 'doorCommand', 'fault', 'reset'}:
            raise ValueError('电梯控制字段无效。')
        for key in ('fault', 'reset'):
            if key in elevator and not isinstance(elevator[key], bool):
                raise ValueError('故障与复位标志必须为布尔值。')
        if elevator.get('targetFloor') is not None and elevator.get('doorCommand') == 'open':
            raise ValueError('不能同时请求电梯运行和开门。')
        if 'targetFloor' in elevator:
            floor = str(elevator['targetFloor'])
            if floor not in self.state['floors']: raise ValueError('目标楼层不存在。')
            if self.state['fault']: raise ValueError('电梯故障状态下禁止运行。')
            if self.state['moving']: raise ValueError('电梯运行中不能更改目标楼层。')
            if self.state['door'] != 'Closed': raise ValueError('门未关闭，禁止电梯运行。')
            self.state['targetFloor'] = floor if floor != self.state['currentFloor'] else None
        if 'doorCommand' in elevator:
            command = elevator['doorCommand']
            if command not in ('open', 'close'): raise ValueError('门控制只支持open或close。')
            if self.state['fault']: raise ValueError('电梯故障状态下请先复位。')
            if self.state['moving']: raise ValueError('电梯运行中禁止开关门。')
            self.state['_opening'] = self.state['_closing'] = 0
            self.state['door'] = 'Opening' if command == 'open' else 'Closing'
        if 'fault' in elevator:
            if not isinstance(elevator['fault'], bool): raise ValueError('故障标志必须为布尔值。')
            self.state['fault'] = elevator['fault']
            if elevator['fault']:
                self.state.update(targetFloor=None, moving=False, direction='Stopped', door='Faulted')
        if elevator.get('reset') is True:
            self.state.update(fault=False, targetFloor=None, moving=False, direction='Stopped', door='Closed', progress=0.0)
            self.state['_opening'] = self.state['_closing'] = 0
        signals = payload.get('signals', {})
        if not isinstance(signals, dict) or set(signals) - {'set', 'schedule', 'clearSchedule'}:
            raise ValueError('信号控制字段无效。')
        if 'clearSchedule' in signals and not isinstance(signals['clearSchedule'], bool):
            raise ValueError('清除信号计划标志必须为布尔值。')
        if 'set' in signals:
            self._set_signals(signals['set'], 'manual')
        if signals.get('clearSchedule') is True:
            self.state['schedules'].clear()
        if 'schedule' in signals:
            entries = signals['schedule']
            if not isinstance(entries, list) or len(entries) > 200: raise ValueError('信号计划须为最多200项数组。')
            if len(entries) + len(self.state['schedules']) > 200:
                raise ValueError('待执行信号计划总数不能超过200项，请先清除旧计划。')
            now = simulation_clock.CLOCK.monotonic()
            for entry in entries:
                if not isinstance(entry, dict) or set(entry) != {'id', 'afterMs', 'set'}:
                    raise ValueError('信号计划条目字段无效。')
                if not isinstance(entry['id'], str) or not entry['id'] or len(entry['id']) > 64:
                    raise ValueError('信号计划编号无效。')
                delay = self._bounded(entry['afterMs'], 0, 3600000, '信号延迟') / 1000
                self.state['schedules'].append(dict(id=entry['id'], due=now + delay, set=self._validate_signals(entry['set'])))
        self.state['sequence'] = sequence; self.state['error'] = None

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
                if isinstance(item, float) and not math.isfinite(item):
                    raise ValueError('信号数字必须是有限值。')
                result[key] = item
            elif isinstance(item, str):
                result[key] = item[:256]
            else: raise ValueError('信号值只支持布尔、数字或短字符串。')
        return result

    def _set_signals(self, value, source):
        validated = self._validate_signals(value)
        self.state['signals'].update(validated)
        self.state['signalEvents'].append(dict(time=simulation_clock.CLOCK.time(), source=source, set=validated))
        self.state['signalEvents'] = self.state['signalEvents'][-200:]

    def _run(self):
        last = simulation_clock.CLOCK.monotonic()
        while not self.stop_event.wait(simulation_clock.CLOCK.business_delay(.1)):
            now = simulation_clock.CLOCK.monotonic(); dt = now - last; last = now
            with self.lock:
                self.last_tick = simulation_clock.CLOCK.time()
                due = sorted((entry for entry in self.state['schedules'] if entry['due'] <= now), key=lambda entry: entry['due'])
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
