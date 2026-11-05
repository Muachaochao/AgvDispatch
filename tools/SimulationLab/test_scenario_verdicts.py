"""Guard against false passes and destructive cleanup in asynchronous scenario verdicts."""
import copy
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from controller import read, write
from scenarios import complete_command_set, control_applied, describe_progress, execute, fresh_device_state, mutual_wait_pairs, submit_original_task, summarize_tasks, validate_scenario


class Event:
    def __init__(self): self.value = False
    def is_set(self): return self.value
    def set(self): self.value = True
    def wait(self, seconds): return self.value


class Child:
    pid = 41
    def poll(self): return None
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
    def test_lost_creation_receipt_retries_only_original_request_and_saves_intent(self):
        with tempfile.TemporaryDirectory() as name:
            root=Path(name)
            class Runtime:
                abort=Event()
                calls=[]
                def api(self, path, payload):
                    self.calls.append(dict(payload))
                    if len(self.calls)==1: raise TimeoutError('lost response after commit')
                    return dict(taskId='original-task',accepted=True,created=False)
            runtime=Runtime()
            payload=dict(requestId='original-request',stationId='CNC-001',requestedVehicleId='car')
            result=submit_original_task(runtime,payload,root)
            self.assertEqual(result['taskId'],'original-task')
            self.assertEqual(runtime.calls,[payload,payload])
            intent=read(root/'create-intent-original-request.json')
            self.assertEqual(intent['request'],payload)
            self.assertEqual(len(intent['attempts']),1)

    def test_progress_names_current_step_and_waiting_reason_in_chinese(self):
        text = describe_progress('车1', dict(task=dict(stage='WaitingForDumper'), steps=[
            dict(sequence=4, stepType='WaitForDumperReady', status='Ready', isCurrent=True,
                 waitingExplanation='倒料机尚未就绪，请检查就绪许可')]))
        self.assertIn('等待倒料机就绪', text)
        self.assertIn('请检查就绪许可', text)
        self.assertNotIn('Ready', text)
    def test_device_observation_retry_absorbs_a_transient_atomic_file_gap(self):
        now = time.time()
        class Runtime:
            abort = threading.Event()
            children = {}
            values = [dict(time=now - 10), dict(time=now)]
            calls = 0
            def device(self):
                value = self.values[min(self.calls, len(self.values) - 1)]
                self.calls += 1
                return value
        runtime = Runtime()
        with patch.object(runtime.abort, 'wait'):
            self.assertEqual(fresh_device_state(runtime), dict(time=now))
        self.assertEqual(runtime.calls, 2)

    def test_device_observation_rejects_future_stamp_and_stops_on_abort_or_dead_process(self):
        class Runtime:
            abort = threading.Event()
            children = {}
            def device(self): return dict(time=time.time() + 100)
        runtime = Runtime()
        with self.assertRaisesRegex(RuntimeError, '过期'):
            fresh_device_state(runtime, timeout=0)
        runtime.abort.set()
        with self.assertRaises(InterruptedError): fresh_device_state(runtime)
        runtime.abort.clear()
        class ExitedChild:
            def poll(self): return 1
        runtime.children['twin'] = ExitedChild()
        with self.assertRaisesRegex(RuntimeError, '子进程已退出'): fresh_device_state(runtime)

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

    def test_mutual_blockers_are_distinguished_from_a_one_way_queue(self):
        self.assertEqual(mutual_wait_pairs({'b': dict(blockedByVehicle='a'), 'a': dict(blockedByVehicle='b')}),
                         [('a', 'b')])
        self.assertEqual(mutual_wait_pairs({'a': dict(blockedByVehicle='b'), 'b': dict()}), [])

    def test_report_task_summary_keeps_assignment_current_step_and_wait_reason(self):
        details = {'TASK-1': dict(
            task=dict(stationId='CNC-001', vehicleId='AGV-01', stage='GoingToDumper'),
            waitingExplanation='等待倒料区资源',
            steps=[dict(sequence=1, stepType='GoToPickup', status='Completed'),
                   dict(sequence=3, stepType='DeliverFullBoxToDumper', status='Executing', isCurrent=True,
                        waitingExplanation='前方车辆占用')])}
        result = summarize_tasks(details)
        self.assertEqual(result[0]['taskId'], 'TASK-1')
        self.assertEqual(result[0]['vehicleId'], 'AGV-01')
        self.assertEqual(result[0]['currentStepNumber'], 3)
        self.assertEqual(result[0]['currentStepName'], '运送满箱到等待位')
        self.assertEqual(result[0]['waitingExplanation'], '等待倒料区资源')
        self.assertEqual(result[0]['steps'][1]['stepName'], '运送满箱到等待位')

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
