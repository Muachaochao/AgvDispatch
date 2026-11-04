"""Guard against false passes and destructive cleanup in asynchronous scenario verdicts."""
import copy
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from controller import read, write
from scenarios import complete_command_set, control_applied, execute, validate_scenario


class Event:
    def __init__(self): self.value = False
    def is_set(self): return self.value
    def set(self): self.value = True
    def wait(self, seconds): return self.value


class Child:
    pid = 41
    def terminate(self): pass
    def wait(self, timeout): pass


class Runtime:
    def __init__(self, root, mode='complete'):
        self.root = root
        self.mode = mode
        self.abort = Event()
        self.children = {'service': Child()}
        self.controls = {'sequence': 'original', 'vehicles': {}, 'plc': {}}
        self.settings = {'Dispatching': {'StationPickupSites': {'CNC-001': 'PICKUP'}}, 'Agv': {'Robots': [{'VehicleId': 'car'}]}}
        self.scenario = {'id': 'test', 'status': 'running', 'completedRounds': 0}
        self.calls = []
        self.injected = []
        self.external_input = None
        events = [dict(time=time.time()+1, kind='robokit', api=3066, response={'ret_code': 0}) for _ in range(4)]
        (root/'device-events.jsonl').write_text('\n'.join(json.dumps(e) for e in events), encoding='utf-8')
        write(root/'device-state.json', {'retained': 'original-state'})
    def idle_review(self): pass
    def api(self, path, payload=None):
        self.calls.append((path, payload))
        if path == '/api/task-options':
            return dict(available=True, stations=[dict(stationId='CNC-001', available=True)], mapId='m', mapVersionId='v', parameterRevision=1)
        if path == '/api/tasks':
            if self.mode == 'abort': self.abort.set()
            return dict(taskId='original-task')
        if path == '/api/tasks/original-task':
            completed = self.mode in ('complete', 'abort')
            steps = [dict(status='Completed' if completed else 'Ready', stepId=str(i)) for i in range(9)]
            if not completed: steps[2]['status'] = 'Executing'
            return dict(task=dict(stage='Completed' if completed else 'Executing'), steps=steps, handover=dict(exitPhysicallyVerified=completed))
        if path == '/api/recovery': return dict(released=False, items=[dict(taskId='original-task')])
        raise AssertionError(path)
    def device(self):
        memory = bytearray(611); memory[0] = memory[2] = 10
        input_path = self.root/'device-input.json'
        if input_path.exists():
            event = read(input_path)
            if self.external_input is None or self.external_input['sequence'] != event['sequence']:
                self.injected.append(event)
                self.external_input = dict(sequence=event['sequence'], accepted=self.mode != 'reject-batch', error=None)
        return dict(time=time.time(), memory_hex=memory.hex(), cars={'car': dict(current_task='transport-original', offline=True,
            simulationControl=dict(sequence='not-the-command', offline=True, error=None))}, simulationControl={'sequence': 'old', 'plc': {}},
            externalInput=self.external_input or {})
    def wait(self, label, predicate, timeout=90):
        value = predicate()
        if not value: raise TimeoutError(label)
        return value
    def control(self, payload):
        self.controls.update(sequence='new', **payload)
    def launch(self, name, arguments, cwd): self.children[name] = Child()
    def connected(self): return True


class Observer:
    def __init__(self, runtime, evidence):
        self.runtime = runtime; self.samples = 0; self.minimum = None; self.max_wait = 0; self.violations = []
    def sample(self, details):
        self.samples += 1
        return [dict(ResourceId='bay', TaskId='original-task')]


def commands():
    return [dict(TaskId='original-task', StepId=str(i), CommandId=str(i), Status=30, AttemptCount=1) for i in range(4)]


def query(root, sql, parameters=()):
    if 'select ResourceId from' in sql: return []
    if 'traffic_resource_lease' in sql: return [dict(ResourceId='bay', TaskId='original-task')]
    if 'agv_command_outbox' in sql: return commands()
    raise AssertionError(sql)


class VerdictTests(unittest.TestCase):
    def test_control_requires_fresh_applied_fields_and_sequence(self):
        controls = dict(sequence='new', plc=dict(readyDelaySeconds=3))
        state = dict(time=time.time(), simulationControl=dict(sequence='new', error=None, plc=dict(readyDelaySeconds=0)))
        self.assertIsNone(control_applied(state, controls), 'same sequence with wrong effective delay is not acknowledgement')
        state['simulationControl']['plc']['readyDelaySeconds'] = 3
        self.assertIsNotNone(control_applied(state, controls))
        state['time'] -= 10
        self.assertIsNone(control_applied(state, controls))
        state['time'] = time.time(); state['simulationControl']['error'] = 'rejected'
        self.assertIsNone(control_applied(state, controls))

    def test_total_commands_cannot_hide_missing_task_or_duplicate_step(self):
        rows = commands()
        self.assertTrue(complete_command_set(rows, {'original-task': {}}))
        rows[0]['StepId'] = rows[1]['StepId']
        self.assertFalse(complete_command_set(rows, {'original-task': {}}))
        rows = commands() + [dict(c, CommandId='other-'+c['CommandId']) for c in commands()]
        self.assertFalse(complete_command_set(rows, {'original-task': {}, 'missing-task': {}}))

    def run_case(self, root, scenario, mode='complete', **parameters):
        runtime = Runtime(root, mode)
        with patch('scenarios.Observer', Observer), patch('scenarios.query', query):
            execute(runtime, validate_scenario({'name': scenario, **parameters}))
        return runtime

    def test_completed_tasks_do_not_pass_fault_scenario_without_injection(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = self.run_case(Path(name), 'disconnect-reconnect')
            self.assertEqual(runtime.report['status'], 'failed')
            self.assertFalse(runtime.report['faultApplied'])
            self.assertFalse(runtime.report['completedBusinessCycle'])
            self.assertTrue(any(c['name']=='同一扫描周期批量呼叫已应用' and c['status']=='passed' for c in runtime.report['checks']))
            self.assertEqual(len(runtime.injected), 1)
            self.assertEqual(runtime.injected[0]['calls'], ['CNC-001'])
            self.assertNotIn('call', runtime.injected[0], 'Batch trigger must not be replaced by a later individual call')
            self.assertTrue(any(c['name']=='指定阶段故障实际触发' and c['status']=='failed' for c in runtime.report['checks']))

    def test_rejected_batch_call_cannot_create_tasks_or_pass(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = self.run_case(Path(name), 'disconnect-reconnect', 'reject-batch')
            self.assertEqual(runtime.report['status'], 'failed')
            self.assertFalse(any(path == '/api/tasks' for path, _ in runtime.calls))
            self.assertTrue(any(c['name']=='同一扫描周期批量呼叫已应用' and c['status']=='failed' for c in runtime.report['checks']))

    def test_wrong_device_ack_fails_delay_before_any_task_is_created(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = self.run_case(Path(name), 'plc-delay')
            self.assertEqual(runtime.report['status'], 'failed')
            self.assertFalse(any(path == '/api/tasks' for path, _ in runtime.calls))

    def test_restart_barrier_pass_is_not_completed_business_round(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = self.run_case(Path(name), 'service-restart', 'restart', restartStage='delivery')
            self.assertEqual(runtime.report['status'], 'passed', runtime.report['message'])
            self.assertTrue(runtime.report['faultApplied'])
            self.assertTrue(runtime.report['recoveryHold'])
            self.assertFalse(runtime.report['completedBusinessCycle'])
            self.assertEqual(runtime.report['metrics']['completedRounds'], 0)
            self.assertFalse(any('/recovery/decisions' in path or '/recovery/release' in path for path, _ in runtime.calls))

    def test_abort_preserves_original_identity_and_device_history(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            runtime = self.run_case(root, 'normal-cycle', 'abort')
            self.assertEqual(runtime.report['status'], 'aborted')
            self.assertFalse(runtime.report['completedBusinessCycle'])
            self.assertEqual(read(root/'device-state.json'), {'retained': 'original-state'})
            self.assertEqual(read(root/'simulation-run-test/task-identities.json'), {'original-task': {'vehicle': 'car', 'station': 'CNC-001'}})
            self.assertEqual(runtime.report['metrics']['completedRounds'], 0)
            self.assertFalse(any('cancel' in path or 'delete' in path or 'recovery' in path for path, _ in runtime.calls))

    def test_missing_observer_input_still_finishes_failed_report(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = Runtime(Path(name))
            with patch('scenarios.Observer', side_effect=ValueError('missing body profile')):
                execute(runtime, validate_scenario({'name':'normal-cycle'}))
            self.assertEqual(runtime.report['status'], 'failed')
            self.assertEqual(runtime.scenario['status'], 'failed')
            self.assertEqual(runtime.report['metrics']['samples'], 0)


if __name__ == '__main__': unittest.main()
