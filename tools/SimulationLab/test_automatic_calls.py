import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from automatic_calls import run_backlog
from controller import read
from scenarios import validate_scenario
from simulation_reports import guidance


class Clock:
    def __init__(self): self.now = 0
    def monotonic(self): return self.now
    def time(self): return 1800000000 + self.now


class BacklogTests(unittest.TestCase):
    def test_admission_queue_is_distinct_from_all_fleet_busy(self):
        self.test_calls_continue_while_car_is_busy_and_repeated_station_waits_its_own_cycle(admission=True)

    def test_calls_continue_while_car_is_busy_and_repeated_station_waits_its_own_cycle(self, admission=False):
        with tempfile.TemporaryDirectory() as name:
            clock = Clock()
            tasks, completed, original, events = {}, set(), {}, []
            root = Path(name)
            class Abort:
                def is_set(self): return False
                def wait(self, seconds): clock.now += seconds; return False
            class Runtime:
                settings = dict(TaskCreation=dict(AutomaticCreationEnabled=True), Agv=dict(Robots=[dict(VehicleId='car')]))
                lab = dict(simulationTopology=dict(stations=[dict(stationId='CNC-001',offset=0),dict(stationId='CNC-002',offset=2)]))
                scenario = {}
                children = {}
                abort = Abort()
                sequence = None
                memory = bytearray(611)
                def device(self):
                    path = root/'device-input.json'
                    if path.exists():
                        value = read(path)
                        if value['sequence'] != self.sequence:
                            self.sequence = value['sequence']
                            for station in value['calls']:
                                offset = 0 if station=='CNC-001' else 2
                                if self.memory[offset] != 10: raise AssertionError('same station reissued too early')
                                self.memory[offset] = 11
                                tid = str(len(original)+1)
                                original[tid] = dict(station=station, emitted=clock.now,
                                    start=max(clock.now, max((item['end'] for item in original.values()),default=clock.now)))
                                original[tid]['end'] = original[tid]['start'] + 3
                                events.append((station,clock.now))
                    return dict(time=time.time(),memory_hex=self.memory.hex(), externalInput=dict(sequence=self.sequence,accepted=True))
                def api(self, path, data=None):
                    if data is not None: raise AssertionError('scenario must not POST task or choose car')
                    tid = path.removeprefix('/api/tasks/')
                    item = original[tid]
                    done = clock.now >= item['end']
                    vehicle = 'car' if clock.now >= item['start'] else None
                    if done: self.memory[0 if item['station']=='CNC-001' else 2] = 10
                    return dict(task=dict(vehicleId=vehicle,stage='Completed' if done else 'Executing' if vehicle else 'WaitingForVehicle'),
                        steps=[dict(status='Completed' if done else 'Ready') for _ in range(9)],
                        handover=dict(exitPhysicallyVerified=done))
                def wait(self, label, predicate, timeout):
                    value = predicate()
                    if not value: raise AssertionError(label)
                    return value
            runtime = Runtime(); runtime.root=root
            if admission:
                runtime.settings = dict(runtime.settings, DownstreamAdmission=dict(Enabled=True, Capacity=1),
                    Agv=dict(Robots=[dict(VehicleId='car'),dict(VehicleId='idle-car')]))
            runtime.memory[0] = runtime.memory[2] = 10
            class Observer:
                def sample(self, details): pass
            def discover(station):
                for tid,item in original.items():
                    if item['station']==station and tid not in tasks:
                        tasks[tid] = dict(vehicle=None,station=station)
                        return tid
            checks = []
            def check(name, passed, detail):
                checks.append((name,passed))
                if not passed: raise AssertionError(detail)
            request=validate_scenario(dict(name='dispatch-backlog',timeoutSeconds=60,calls=[
                dict(stationId='CNC-001',atSeconds=0),dict(stationId='CNC-002',atSeconds=0),
                dict(stationId='CNC-001',atSeconds=.5)]))
            with patch('automatic_calls.time',clock):
                run_backlog(runtime,request,root,Observer(),tasks,completed,discover,check)
            self.assertEqual(len(completed),3)
            self.assertEqual(events[:2],[('CNC-001',0),('CNC-002',0)])
            self.assertGreaterEqual(events[2][1],4.5)
            self.assertLess(events[2][1],6, 'repeat station may continue while the other station is executing')
            summary=read(root/'queue-summary.json')
            if admission:
                self.assertEqual(summary['maximumBusyWaitingTasks'],0)
                self.assertGreater(summary['maximumAdmissionWaitingTasks'],0)
                self.assertEqual(summary['maximumActiveTasks'],1)
                self.assertTrue(any(name=='下游准入满额时呼叫保持未取箱排队' for name,_ in checks))
            else:
                self.assertGreater(summary['maximumBusyWaitingTasks'],0)
            self.assertTrue(all(passed for _,passed in checks))
            accepted=[json.loads(line) for line in (root/'call-queue.jsonl').read_text(encoding='utf-8').splitlines()]
            self.assertEqual(sum(item['kind']=='assigned' for item in accepted),3)

    def test_backlog_rejects_script_selected_vehicle_and_unknown_station_shape(self):
        for call in [dict(stationId='CNC-001',atSeconds=0,vehicleId='car'),dict(stationId='CNC-306',atSeconds=0)]:
            with self.assertRaises(ValueError): validate_scenario(dict(name='dispatch-backlog',calls=[call]))

    def test_recovery_names_step_signal_action_and_expected_feedback_without_forcing_high(self):
        value=guidance({'task':dict(task=dict(stationId='CNC-001',vehicleId='car',stage='WaitingForPlcConfirmation'),
            steps=[dict(sequence=9,isCurrent=True,stepType='CompletePlcHandshake',progressConditions=[
                dict(stationId='CNC-001',variableName='EmptyBoxPlaced',satisfied=False,expectedValue=False,quality='Good')])])},
            dict(cars={'car':dict(site='BUS-LM4',x=1,y=2)}))[0]
        self.assertEqual(value['stepNumber'],9)
        self.assertIn('空箱已放到位需复位',value['restoreAction'])
        self.assertIn('不额外置位',value['expectedFeedback'])
        self.assertNotIn('EmptyBoxPlaced',value['blockedBy'])


if __name__ == '__main__': unittest.main()
