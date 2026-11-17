import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from controller import Runtime, write
from test_runtime_lifecycle import fixture
from runtime_release import current_release, verify_release


class RuntimeReleaseTests(unittest.TestCase):
    def publish(self, parent):
        workspace = parent / 'DispatchingSystem'
        root = fixture(workspace)
        publish = parent / '发布包'
        for folder, name in [('Service', 'AgvDispatch.Service'), ('Web/host', 'AgvDispatch.WebHost')]:
            directory = publish / 'current' / folder
            directory.mkdir(parents=True)
            (directory / (name + '.exe')).write_bytes(b'current-exe')
            (directory / (name + '.dll')).write_bytes(b'current-dll')
        write(publish / '最新版Web.json', dict(url='http://127.0.0.1:5199/', releaseId='current', directory='current'))
        options = dict(Mode='Global', IntervalMs=3000, WindowSize=80, WaitPriorityPerMinute=1)
        (publish / '运行数据/后台5199').mkdir(parents=True)
        write(publish / '运行数据/后台5199/dispatch-optimization.json', dict(Options=options))
        return workspace, root, publish, options

    def test_clone_uses_current_binaries_options_and_records_provenance(self):
        with tempfile.TemporaryDirectory() as name:
            workspace, root, _, options = self.publish(Path(name))
            with patch('controller.WORKSPACE', workspace):
                runtime = Runtime(root)
                runtime.new_instance(dict(vehicles=2))
                self.assertEqual((runtime.root / 'service/AgvDispatch.Service.dll').read_bytes(), b'current-dll')
                self.assertEqual(runtime.settings['DispatchOptimization'], options)
                self.assertEqual(runtime.status()['runtimeRelease']['algorithmMode'], 'Global')
                verify_release(runtime.root, current_release(workspace))
            self.assertEqual((root / 'service/AgvDispatch.Service.exe').read_bytes(), b'synthetic-build')

    def test_old_template_cannot_start_as_current_release(self):
        with tempfile.TemporaryDirectory() as name:
            workspace, root, _, _ = self.publish(Path(name))
            with patch('controller.WORKSPACE', workspace):
                with self.assertRaisesRegex(ValueError, '运行包'):
                    Runtime(root).start()

    def test_altered_options_or_binary_fail_startup_verification(self):
        with tempfile.TemporaryDirectory() as name:
            workspace, root, _, _ = self.publish(Path(name))
            with patch('controller.WORKSPACE', workspace):
                runtime = Runtime(root)
                runtime.new_instance(dict(vehicles=2))
                release = current_release(workspace)
                settings = json.loads((runtime.root / 'service-settings.json').read_text(encoding='utf-8-sig'))
                settings['DispatchOptimization']['Mode'] = 'Disabled'
                write(runtime.root / 'service-settings.json', settings)
                with self.assertRaisesRegex(ValueError, '算法'):
                    verify_release(runtime.root, release)
                write(runtime.root / 'service-settings.json', runtime.settings)
                (runtime.root / 'service/AgvDispatch.Service.dll').write_bytes(b'changed')
                with self.assertRaisesRegex(ValueError, '运行包'):
                    verify_release(runtime.root, release)

    def test_invalid_manifest_cannot_fall_back_to_old_template(self):
        with tempfile.TemporaryDirectory() as name:
            workspace, _, publish, _ = self.publish(Path(name))
            write(publish / '最新版Web.json', dict(url='http://127.0.0.1:5199/', releaseId='old', directory='../outside'))
            with self.assertRaises(ValueError): current_release(workspace)
            write(publish / '最新版Web.json', dict(url='http://127.0.0.1:5199/', releaseId='current', directory='current'))
            (publish / 'current/Service/AgvDispatch.Service.dll').unlink()
            with self.assertRaises(ValueError): current_release(workspace)

    def test_unbound_development_fixture_remains_explicitly_unbound(self):
        with tempfile.TemporaryDirectory() as name:
            self.assertIsNone(current_release(Path(name) / 'DispatchingSystem'))
