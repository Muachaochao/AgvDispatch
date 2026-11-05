"""Lifecycle regression: keep fault evidence and only stop runtime children we own."""
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch

from controller import CleanupPending, Runtime, cleanup_owned_runtime, read, validate_root, write

SOURCE_PLC_TABLE = Path(__file__).resolve().parents[2] / 'AgvDispatch.Service/config/plc-signals.csv'


class Child:
    def __init__(self, timeout=False):
        self.terminated = self.killed = False
        self.waits = 0
        self.timeout = timeout
    def poll(self): return None
    def terminate(self): self.terminated = True
    def kill(self): self.killed = True
    def wait(self, timeout):
        self.waits += 1
        if self.timeout and not self.killed: raise subprocess.TimeoutExpired('owned-child', timeout)
        return 0


def fixture(workspace):
    root = workspace/'artifacts/web-single-vehicle-source'
    (root/'service/config').mkdir(parents=True)
    (root/'webhost').mkdir()
    (root/'data').mkdir()
    settings = dict(HandshakeExecution=dict(PlcContractVersion='plc-28-stage-v2', DumperExit=dict(
            ResourceIds=['R2_倒料区'], BodyProfiles=[
            dict(VehicleId='car', ProfileId='SIM-ONLY-car', RadiusFromReportedReferenceMeters=0.6)])),
        Database=dict(Path=str(root/'data/agv-dispatch.db')), Security=dict(PipeName='Agv28StageLab-source'),
        Plc=dict(IpAddress='127.0.0.1', Port=19001, SignalCsvPath='config/plc.csv'),
        Agv=dict(Robots=[dict(VehicleId='car', IpAddress='127.0.0.1', Port=19002, NavigationPort=19003)]),
        Map=dict(ImportPath=str(root/'navigation.agvmap.json')), TaskCreation=dict(AutomaticCreationEnabled=False),
        Dispatching=dict(MaxConcurrentTasks=1, StationPickupSites={'CNC-001':'取货A','CNC-002':'取货B'},
                         DumperWaitingSite='等待', DumperDumpingSite='倒料'))
    lab = dict(simulationOnly=True, pipe='Agv28StageLab-source', webPort=19004, plcPort=19001, robotPort=19002, navigationPort=19003)
    web = dict(Port=19004, PipeName=lab['pipe'], ProjectStorePath=str(root/'projects'), EngineeringStorePath=str(root/'engineering'))
    for filename, value in [('lab.json', lab), ('service/appsettings.json', settings), ('service-settings.json', settings),
        ('webhost/appsettings.json', {'Web':web}), ('navigation.agvmap.json', {'nodes':[
            dict(id=label, agvSiteId=str(i), x=i*10, y=0, enabled=True, resourceIds=[])
            for i,label in enumerate(['取货A','取货B','停车A','停车B','停车C','倒料','等待'])],
            'edges':[dict(id='E'+str(i), **{'from':a,'to':b}, bidirectional=True, enabled=True)
                     for i,(a,b) in enumerate((('取货A','停车A'),('停车A','倒料'),('停车B','倒料'),('停车C','等待'),('倒料','等待'),('等待','取货B')))],
            'resources':[dict(id='R2_倒料区', capacity=1, boundary=[dict(x=30,y=-1),dict(x=31,y=-1),dict(x=31,y=1)])]})]:
        write(root/filename, value)
    (root/'service/AgvDispatch.Service.exe').write_bytes(b'synthetic-build')
    (root/'webhost/AgvDispatch.WebHost.exe').write_bytes(b'synthetic-build')
    (root/'service/config/plc.csv').write_bytes(SOURCE_PLC_TABLE.read_bytes())
    return root


class LifecycleTests(unittest.TestCase):
    def test_failed_start_waits_for_children_and_keeps_handles_if_cleanup_is_pending(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            root = fixture(Path(name)); runtime = Runtime(root)
            class StuckChild(Child):
                def wait(self, timeout):
                    raise subprocess.TimeoutExpired('owned-child', timeout)
            child = StuckChild()
            def launch(*args):
                runtime.children['twin'] = child
                raise RuntimeError('startup failure')
            runtime.launch = launch
            runtime._start()
            self.assertEqual(runtime.phase, 'failed')
            self.assertIn('startup failure', runtime.error)
            self.assertIn('停止并清理', runtime.error)
            self.assertIs(runtime.children['twin'], child)
            self.assertTrue(child.killed)
            child.wait = lambda timeout: 0
            runtime.stop()
            self.assertFalse(runtime.children)

    def test_new_instance_does_not_orphan_optional_models(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            root = fixture(Path(name)); runtime = Runtime(root)
            runtime.equipment_model = object()
            with self.assertRaisesRegex(ValueError, '设备模型'): runtime.new_instance()
            self.assertEqual(runtime.root, root)
            runtime.equipment_model = None; runtime.protocol_runtime = object()
            with self.assertRaisesRegex(ValueError, '协议服务'): runtime.new_instance()

    def test_shutdown_retries_finishing_worker_without_losing_owned_child(self):
        runtime = object.__new__(Runtime)
        owned = Child()
        runtime.children = {'service': owned}
        class Worker:
            def is_alive(self): return True
        runtime.worker = Worker()
        attempts = []
        def stop():
            attempts.append(True)
            if len(attempts) == 1: raise CleanupPending('操作仍在收尾')
            self.assertIs(runtime.children['service'], owned)
            owned.terminate(); runtime.children.clear()
            return {'phase':'stopped'}
        runtime.stop = stop
        with patch('controller.time.sleep') as sleep:
            self.assertEqual(cleanup_owned_runtime(runtime), {'phase':'stopped'})
            sleep.assert_called_once()
        self.assertEqual(len(attempts), 2)
        self.assertTrue(owned.terminated)
        runtime.worker = None
        runtime.stop = lambda: (_ for _ in ()).throw(RuntimeError('unrelated failure'))
        with self.assertRaisesRegex(RuntimeError, 'unrelated failure'): cleanup_owned_runtime(runtime)

    def test_abort_blocks_followup_backend_calls_during_shutdown(self):
        runtime = object.__new__(Runtime)
        runtime.abort = threading.Event(); runtime.abort.set()
        with patch('controller.urlopen') as open_request:
            with self.assertRaises(InterruptedError): runtime.api('/api/tasks', {'requestId':'late-request'})
            open_request.assert_not_called()

    def test_idle_review_uses_all_task_summary_not_first_page(self):
        runtime = object.__new__(Runtime)
        memory = bytearray(611); memory[0] = memory[2] = 10
        runtime.device = lambda: dict(memory_hex=memory.hex(), cars={'car':dict(loaded=False, current_task=None, tasks={})})
        calls = []
        def api(path):
            calls.append(path)
            return dict(items=[dict(stage='Completed')]*50, totalCount=51, summary=dict(total=51, completed=50))
        runtime.api = api
        with self.assertRaisesRegex(RuntimeError, '未闭合任务'): runtime.idle_review()
        self.assertEqual(calls, ['/api/tasks'], 'Must reject before any recovery approval')
        runtime.api = lambda path: dict(items=[], totalCount=0)
        with self.assertRaisesRegex(RuntimeError, '完整任务汇总'): runtime.idle_review()

    def test_raw_symlink_is_rejected_before_resolving_target(self):
        with tempfile.TemporaryDirectory() as name:
            workspace = Path(name)
            root = workspace/'artifacts/web-single-vehicle-link'
            with patch('controller.WORKSPACE', workspace), patch.object(Path, 'is_symlink', lambda p: p == root):
                with self.assertRaisesRegex(ValueError, '链接重定向'): validate_root(root)

    def test_clone_retains_source_history_and_creates_clean_isolated_identity(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            source = fixture(Path(name))
            write(source/'multi-state.json', {'fault':'retain'})
            write(source/'simulation-report.json', {'status':'aborted','id':'original-report'})
            (source/'data/agv-dispatch.db').write_bytes(b'original-db')
            (source/'service/data').mkdir()
            (source/'service/data/hidden.db').write_bytes(b'must-not-copy')
            (source/'service/appsettings.Production.json').write_text('{"private":"must-not-copy"}', encoding='utf-8')
            runtime = Runtime(source)
            runtime.settings['Dispatching']['DumperWaitingSites'] = ['等待', '停车C']
            result = runtime.new_instance()
            target = runtime.root
            self.assertNotEqual(source, target)
            self.assertEqual(target.parent, source.parent)
            self.assertFalse(result['running'])
            self.assertIsNone(result['report'])
            self.assertEqual(read(source/'multi-state.json'), {'fault':'retain'})
            self.assertEqual((source/'data/agv-dispatch.db').read_bytes(), b'original-db')
            self.assertFalse((target/'data/agv-dispatch.db').exists())
            self.assertFalse((target/'service/data').exists())
            self.assertFalse((target/'service/appsettings.Production.json').exists())
            self.assertFalse((target/'multi-state.json').exists())
            self.assertEqual((target/'service/config/plc.csv').read_bytes(), SOURCE_PLC_TABLE.read_bytes())
            self.assertEqual(runtime.settings['Database']['Path'], str(target/'data/agv-dispatch.db'))
            self.assertNotEqual(runtime.lab['pipe'], 'Agv28StageLab-source')
            self.assertEqual(len(runtime.ports), len(set(runtime.ports)))
            self.assertFalse(set(runtime.ports) & {5198,5199})
            self.assertEqual(runtime.settings['Dispatching']['DumperWaitingSites'], ['等待','停车C'])
            self.assertEqual(runtime.lab['simulationConfiguration']['dumper']['waitingSites'], ['等待','停车C'])
            self.assertTrue(runtime.settings['TrafficSegmentRelease']['Enabled'])
            self.assertTrue(runtime.settings['TrafficSegmentRelease']['CommitEnabled'])
            self.assertAlmostEqual(runtime.settings['Dispatching']['DumperTransitClearanceMeters'], 1.35)

    def test_clone_rejects_running_instance_and_nonloopback_saved_endpoint(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            root = fixture(Path(name)); runtime = Runtime(root)
            runtime.phase='running'
            with self.assertRaises(ValueError): runtime.new_instance()
            runtime.phase='stopped'
            write(root/'data/communication-settings.json', {'Configuration':{'Plcs':[dict(IpAddress='192.0.2.1')], 'Agvs':[]}})
            with self.assertRaisesRegex(ValueError, '回环设备'): runtime.new_instance()

    def test_three_vehicle_clone_has_matched_profiles_ports_and_distinct_origins(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            source = fixture(Path(name))
            write(source/'multi-state.json', {'original':'retained'})
            (source/'data/agv-dispatch.db').write_bytes(b'original-db')
            before = {p.relative_to(source):p.read_bytes() for p in source.rglob('*') if p.is_file()}
            runtime = Runtime(source)
            runtime.new_instance({'vehicles':3})
            self.assertEqual({p.relative_to(source):p.read_bytes() for p in source.rglob('*') if p.is_file()}, before)
            robots = runtime.settings['Agv']['Robots']
            expected = ['AGV-LOCAL-01','AGV-LOCAL-02','AGV-LOCAL-03']
            self.assertEqual([r['VehicleId'] for r in robots], expected)
            self.assertEqual([p['VehicleId'] for p in runtime.settings['HandshakeExecution']['DumperExit']['BodyProfiles']], expected)
            self.assertEqual(len(set(p['ProfileId'] for p in runtime.settings['HandshakeExecution']['DumperExit']['BodyProfiles'])), 3)
            self.assertTrue(all(p['RadiusFromReportedReferenceMeters']==0.6 for p in runtime.settings['HandshakeExecution']['DumperExit']['BodyProfiles']))
            self.assertEqual(runtime.settings['Dispatching']['MaxConcurrentTasks'], 3)
            self.assertTrue(runtime.lab['multiVehicleSimulation'])
            self.assertEqual(set(runtime.lab['initialSites']), set(expected))
            self.assertEqual(set(runtime.lab['initialSites'].values()), {'停车A','停车B','停车C'})
            self.assertEqual(len(runtime.ports), 8)
            self.assertEqual(len(set(runtime.ports)), 8)
            self.assertFalse(set(runtime.ports) & {5198,5199})
            self.assertTrue(all(r['IpAddress']=='127.0.0.1' for r in robots))
            self.assertFalse((runtime.root/'multi-state.json').exists())
            self.assertFalse((runtime.root/'data/agv-dispatch.db').exists())

    def test_stop_only_owned_children_and_preserves_original_files(self):
        with tempfile.TemporaryDirectory() as name, patch('controller.WORKSPACE', Path(name)):
            root = fixture(Path(name)); runtime = Runtime(root)
            write(root/'multi-state.json', {'original':'task-and-load'})
            write(root/'simulation-report.json', {'status':'aborted'})
            owned = {'twin':Child(), 'service':Child(timeout=True), 'webhost':Child()}
            outsider = Child()
            runtime.children = dict(owned)
            result = runtime.stop()
            self.assertEqual(result['phase'], 'stopped')
            self.assertTrue(runtime.abort.is_set())
            self.assertTrue((root/'STOP').exists())
            self.assertEqual(read(root/'multi-state.json'), {'original':'task-and-load'})
            self.assertEqual(read(root/'simulation-report.json'), {'status':'aborted'})
            self.assertFalse(owned['twin'].terminated, 'Device first receives its own STOP marker')
            self.assertTrue(owned['service'].terminated)
            self.assertTrue(owned['service'].killed)
            self.assertFalse(outsider.terminated or outsider.killed)
            self.assertEqual(runtime.children, {})


if __name__ == '__main__': unittest.main()
