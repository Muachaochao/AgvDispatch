import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from controller import Runtime, read, validate_root, write
from schemes import SchemeStore
from simulation_reports import history, report, replay, saved_task_detail
from test_runtime_lifecycle import fixture


class ControllerSchemeTests(unittest.TestCase):
    def test_current_plan_is_visible_after_controller_restart(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            root = fixture(Path(name))
            self.assertIsNone(Runtime(root).status()['businessPlan'])
            plan = dict(seed=123, count=2, intervalSeconds=3, stationIds=['CNC-001'],
                        calls=[dict(stationId='CNC-001', atSeconds=0, vehicleId=None),
                               dict(stationId='CNC-001', atSeconds=3, vehicleId=None)])
            write(root / 'business-call-plan.json', plan)
            self.assertEqual(Runtime(root).status()['businessPlan'], plan)

    def test_saved_configuration_recreates_clean_instance_and_survives_selection_restart(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            workspace = Path(name)
            source = fixture(workspace)
            store = SchemeStore(workspace, source, validate_root)
            runtime = Runtime(source, scheme_store=store)
            configuration = runtime._default_configuration(2)
            configuration['plcSignalTable'].update(sourceSha256='a' * 64,
                sourceFile='config/plc-lab-nine-cnc-signals.csv',
                sourceDeviceId='PLC-MAIN', sourceDeviceName='总控PLC')
            result = runtime.save_scheme(dict(name='双车原图方案', configuration=configuration,
                                             scenario=dict(name='normal-cycle', rounds=1)))
            scheme_id = result['selectedSchemeId']
            write(source / 'multi-state.json', dict(retained='原任务与设备故障证据'))
            runtime.select_scheme(dict(id=scheme_id))
            created = runtime.root
            self.assertNotEqual(source, created)
            self.assertFalse((created / 'multi-state.json').exists())
            self.assertEqual(json.loads((source / 'multi-state.json').read_text(encoding='utf-8'))['retained'], '原任务与设备故障证据')
            restored = SchemeStore(workspace, source, validate_root)
            self.assertEqual(restored.restore_root(), (created, None))
            self.assertEqual(restored.catalog()['selectedSchemeId'], scheme_id)
            saved = restored.get(scheme_id)['configuration']
            self.assertEqual(saved['mapDefinition'], configuration['mapDefinition'])
            self.assertEqual(saved['plcSignalTable']['sha256'], configuration['plcSignalTable']['sha256'])
            self.assertEqual(saved['plcSignalTable']['sourceSha256'], 'a' * 64)
            self.assertEqual(saved['plcSignalTable']['sourceFile'], 'config/plc-lab-nine-cnc-signals.csv')
            self.assertEqual(restored.catalog()['schemes'][0]['plcTableSha256'], configuration['plcSignalTable']['sha256'])
            self.assertEqual(restored.catalog()['schemes'][0]['plcSourceSha256'], 'a' * 64)
            self.assertTrue((created / 'plc-signals.source.csv').is_file())
            self.assertTrue((created / 'service/config/simulation-signals.csv').is_file())
            self.assertEqual(read(created / 'lab.json')['plcSignalTable']['sha256'], configuration['plcSignalTable']['sha256'])
            self.assertEqual(read(created / 'lab.json')['plcSignalTable']['sourceSha256'], 'a' * 64)
            self.assertEqual(read(created / 'lab.json')['plcSignalTable']['sourceFile'],
                             'config/plc-lab-nine-cnc-signals.csv')
            self.assertEqual(read(created / 'lab.json')['plcSignalTable']['privateFile'], 'plc-signals.source.csv')

    def test_incompatible_saved_scenario_is_rejected_before_creating_any_instance(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            source = fixture(Path(name))
            store = SchemeStore(Path(name), source, validate_root)
            runtime = Runtime(source, scheme_store=store)
            configuration = runtime._default_configuration(2)
            value = store.save('不匹配场景', configuration, dict(name='custom-calls', calls=[dict(stationId='CNC-009', atSeconds=0)]))
            with self.assertRaisesRegex(ValueError, '不匹配'):
                runtime.select_scheme(dict(id=value['id']))
            self.assertEqual(list(source.parent.glob('web-single-vehicle-*')), [source])

    def test_scheme_delete_clears_selection_and_preserves_instance_history(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            source = fixture(Path(name))
            store = SchemeStore(Path(name), source, validate_root)
            runtime = Runtime(source, scheme_store=store)
            result = runtime.save_scheme(dict(name='可删除方案', configuration=runtime._default_configuration(2),
                                             scenario=dict(name='normal-cycle', rounds=1)))
            scheme_id = result['selectedSchemeId']
            write(source / 'simulation-report.json', dict(id='retained', status='failed'))
            deleted = runtime.delete_scheme(dict(id=scheme_id))
            self.assertIsNone(deleted['selectedSchemeId'])
            self.assertNotIn(scheme_id, [row['id'] for row in deleted['schemes']])
            self.assertTrue((source / 'simulation-report.json').exists())

    def test_startup_cannot_clear_stop_arriving_during_telemetry_check(self):
        runtime = object.__new__(Runtime)
        runtime.lock = threading.RLock()
        runtime.abort = threading.Event()
        runtime.worker = None
        def fresh_telemetry():
            runtime.abort.set()
            return True
        runtime._fresh_telemetry = fresh_telemetry
        with patch('controller.threading.Thread') as thread:
            with self.assertRaises(InterruptedError):
                runtime.run_scenario(dict(name='normal-cycle'), clear_abort=False)
            thread.assert_not_called()
        self.assertTrue(runtime.abort.is_set())

    def test_stop_waits_for_automatic_startup_before_terminating_owned_runtime(self):
        runtime = object.__new__(Runtime)
        runtime.abort = threading.Event()
        runtime.worker = None
        runtime.protocol_runtime = runtime.equipment_model = None
        order = []
        class Startup:
            alive = True
            def is_alive(self): return self.alive
            def join(self, timeout):
                self.alive = False
                order.append('startup-stopped' if runtime.abort.is_set() else 'bad')
        runtime.startup_thread = Startup()
        runtime._stop_children = lambda: order.append('children-stopped')
        runtime.status = lambda: dict(phase=runtime.phase)
        runtime.stop()
        self.assertEqual(order, ['startup-stopped', 'children-stopped'])


class ReportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.workspace = Path(self.temp.name)
        self.root = fixture(self.workspace)
        self.runtime = SimpleNamespace(root=self.root, scheme_store=None, report=None)
        self.identity = 'a' * 32
        self.run = self.root / ('simulation-run-' + self.identity)
        self.run.mkdir()
        write(self.run / 'report.json', dict(id=self.identity, name='test', status='passed', finishedAt=time.time()))
        write(self.run / 'map.json', dict(name='当前地图', nodes=[]))
        self.patch = patch('controller.WORKSPACE', self.workspace)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def observations(self, number=5, text='等待前车'):
        with (self.run / 'observations.jsonl').open('w', encoding='utf-8') as stream:
            for index in range(number):
                stream.write(json.dumps(dict(time=index, device=dict(cars={'car':dict(x=index, y=0, site='S', waitReason=text)}),
                    tasks={}, leases=[]), ensure_ascii=False) + '\n')

    def test_replay_uses_own_run_map_and_retains_final_sample(self):
        self.observations(2010)
        value = replay(self.runtime, self.identity)
        self.assertLessEqual(len(value['timeline']), 2000)
        self.assertEqual(value['timeline'][-1]['time'], 2009)
        self.assertEqual(value['map']['name'], '当前地图')
        self.assertTrue(value['sampled'])

    def test_replay_enforces_total_byte_limit_in_addition_to_line_and_response_limits(self):
        self.observations(2)
        with patch('simulation_reports.MAX_REPLAY_BYTES', 1):
            with self.assertRaisesRegex(ValueError, '读取边界'):
                replay(self.runtime, self.identity)

    def test_terminal_history_label_is_corrected_without_rewriting_original_evidence(self):
        original = dict(id=self.identity, taskSummary=[dict(stage='Completed',
            currentStepName='等待派车', currentStepNumber=1, currentStepType='GoToPickup')])
        write(self.run / 'report.json', original)
        self.assertEqual(report(self.runtime, self.identity)['taskSummary'][0]['currentStepName'], '已完成')
        self.assertIsNone(report(self.runtime, self.identity)['taskSummary'][0]['currentStepNumber'])
        self.assertEqual(read(self.run / 'report.json'), original)
        self.runtime.report = original
        self.assertEqual(report(self.runtime)['taskSummary'][0]['currentStepName'], '已完成')
        self.assertEqual(self.runtime.report, original)

    def test_saved_task_detail_preserves_route_and_rejects_mismatched_task(self):
        route = {'nodeIds': ['A', 'B'], 'edgeIds': ['AB']}
        detail = dict(task=dict(taskId='TASK-1'), commands=[dict(route=route)])
        write(self.run / 'all-task-details.json', {'TASK-1': detail, 'TASK-2': detail})
        archived = saved_task_detail(self.runtime, self.identity, 'TASK-1')
        self.assertEqual(archived['commands'][0]['route'], route)
        self.assertEqual(archived['evidenceSource'], 'saved-report-detail')
        self.assertIsNone(saved_task_detail(self.runtime, self.identity, 'TASK-2'))
        with patch('simulation_reports.MAX_TASK_DETAILS_BYTES', 1):
            self.assertIsNone(saved_task_detail(self.runtime, self.identity, 'TASK-1'))

    def test_replay_retains_actual_station_load_and_obstacle_evidence(self):
        car = dict(x=1, y=2, angle=1.5707963267948966, station='CNC-024',
                   loaded=True, blocked=True, blockedByVehicle='AGV-02', paused=False, offline=False)
        (self.run / 'observations.jsonl').write_text(json.dumps(
            dict(time=1, device=dict(cars={'AGV-01': car}), tasks={}, leases=[])) + '\n', encoding='utf-8')
        value = replay(self.runtime, self.identity)['timeline'][0]['vehicles'][0]
        self.assertEqual(value['site'], 'CNC-024')
        self.assertTrue(value['loaded'])
        self.assertTrue(value['blocked'])
        self.assertEqual(value['blockedByVehicle'], 'AGV-02')
        self.assertEqual(value['angle'], car['angle'])

    def test_older_replay_keeps_site_and_does_not_invent_load_evidence(self):
        self.observations(1)
        car = replay(self.runtime, self.identity)['timeline'][0]['vehicles'][0]
        self.assertEqual(car['site'], 'S')
        self.assertIsNone(car['loaded'])

    def test_replay_response_size_is_bounded_even_for_large_repeated_waiting_chains(self):
        self.observations(90, '等待' * 2500)
        with patch('simulation_reports.MAX_RESPONSE_BYTES', 50000):
            value = replay(self.runtime, self.identity)
        self.assertLess(len(json.dumps(value, ensure_ascii=False).encode('utf-8')), 50000)
        self.assertEqual(value['timeline'][-1]['time'], 89)
        self.assertTrue(value['sampled'])

    def test_invalid_id_and_mismatched_report_are_rejected(self):
        with self.assertRaises(ValueError): replay(self.runtime, '../field')
        write(self.run / 'report.json', dict(id='b' * 32))
        with self.assertRaisesRegex(ValueError, '编号'):
            report(self.runtime, self.identity)
        with self.assertRaisesRegex(ValueError, '编号'):
            replay(self.runtime, self.identity)

    def test_history_ignores_malformed_reports_and_sorts_non_numeric_timestamps_safely(self):
        for identity, value in [('b' * 32, dict(id='b' * 32, finishedAt='wrong')), ('c' * 32, [])]:
            directory = self.root / ('simulation-run-' + identity)
            directory.mkdir()
            write(directory / 'report.json', value)
        records = history(self.runtime)
        self.assertEqual([row['id'] for row in records], [self.identity, 'b' * 32])

    def test_replay_does_not_read_oversized_map_or_single_line(self):
        self.observations(1)
        with patch('simulation_reports.MAX_LINE_BYTES', 10):
            with self.assertRaisesRegex(ValueError, '单次采样'):
                replay(self.runtime, self.identity)
        with patch('simulation_reports._linked', side_effect=lambda path: path.name == 'map.json'):
            value = replay(self.runtime, self.identity)
        self.assertIsNone(value['map'])
        self.assertIn('边界', value['mapMessage'])


if __name__ == '__main__':
    unittest.main()
