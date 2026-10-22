"""Bootstrap and refusal checks with mocked Popen: no real process is launched."""
import contextlib
import io
import json
import runpy
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


class StartRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='agv-lab-start-mock-')
        self.root = Path(self.temporary.name) / 'web-single-vehicle-test'
        self.root.mkdir()
        self.write('lab.json', dict(webPort=45000, pipe='private-test'))
        self.service = dict(Database=dict(Path=str(self.root / 'data/test.db')), Security=dict(PipeName='private-test'),
                            Plc=dict(IpAddress='127.0.0.1', Port=45001), Agv=dict(Robots=[dict(IpAddress='127.0.0.1', Port=45002, NavigationPort=45003)]),
                            TaskCreation=dict(AutomaticCreationEnabled=False))
        self.write('service/appsettings.json', self.service)
        self.write('webhost/appsettings.json', dict(Web=dict(Port=45000, PipeName='private-test')))
        for name in ['service/AgvDispatch.Service.exe', 'webhost/AgvDispatch.WebHost.exe']:
            (self.root / name).write_bytes(b'test placeholder, never executed')

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, name, value):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value), encoding='utf-8')

    def run_start(self, popen):
        with patch('lab_paths.resolve_lab_root', return_value=self.root), patch('socket.create_connection', side_effect=ConnectionRefusedError), \
             patch('subprocess.Popen', popen), contextlib.redirect_stdout(io.StringIO()):
            runpy.run_path(str(Path(__file__).with_name('start_runtime.py')), run_name='__main__')

    def test_first_boot_without_saved_communication(self):
        from unittest.mock import Mock
        popen = Mock(side_effect=[SimpleNamespace(pid=1001), SimpleNamespace(pid=1002), SimpleNamespace(pid=1003)])
        self.run_start(popen)
        self.assertEqual(popen.call_count, 3)
        self.assertEqual(json.loads((self.root / 'ready.json').read_text(encoding='utf-8'))['root'], str(self.root))
        ledger = json.loads((self.root / 'processes.json').read_text(encoding='utf-8'))
        self.assertEqual([item['Id'] for item in ledger], [1001, 1002, 1003])
        self.assertTrue(all(call.kwargs['env']['AGV_DISPATCH_DATABASE_PATH'] == str(self.root / 'data/test.db') for call in popen.call_args_list))

    def test_saved_nonloopback_connection_blocks_all_children(self):
        from unittest.mock import Mock
        self.write('data/communication-settings.json', dict(Configuration=dict(Plcs=[dict(IpAddress='192.168.1.10', Port=102)], Agvs=[])))
        popen = Mock()
        with self.assertRaisesRegex(RuntimeError, 'Non-loopback'):
            self.run_start(popen)
        popen.assert_not_called()

    def test_two_vehicle_marker_selects_shared_plc_twin(self):
        from unittest.mock import Mock
        self.write('lab.json', dict(webPort=45000, pipe='private-test', twoVehicleSimulation=True))
        popen = Mock(side_effect=[SimpleNamespace(pid=1001), SimpleNamespace(pid=1002), SimpleNamespace(pid=1003)])
        self.run_start(popen)
        self.assertTrue(popen.call_args_list[0].args[0][1].endswith('WebTwoVehicleLab\\multi_twin.py'))

    def test_later_failure_preserves_first_child_ownership(self):
        from unittest.mock import Mock
        popen = Mock(side_effect=[SimpleNamespace(pid=2001), OSError('isolated startup failure')])
        with self.assertRaises(OSError):
            self.run_start(popen)
        self.assertEqual(json.loads((self.root / 'processes.json').read_text(encoding='utf-8'))[0]['Id'], 2001)

    def test_database_outside_private_root_is_rejected(self):
        from unittest.mock import Mock
        self.service['Database']['Path'] = str(self.root.parent / 'external.db')
        self.write('service/appsettings.json', self.service)
        popen = Mock()
        with self.assertRaisesRegex(RuntimeError, 'Database outside'):
            self.run_start(popen)
        popen.assert_not_called()


if __name__ == '__main__':
    unittest.main()
