"""Probe boundaries: never contact non-loopback saved endpoints or terminate a reused PID."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import run_rounds
import run_restart_probe


class ProbeGuards(unittest.TestCase):
    def configure(self, root):
        (root / 'service').mkdir()
        (root / 'data').mkdir()
        (root / 'lab.json').write_text(json.dumps(dict(simulationOnly=True)), encoding='utf-8')
        settings = dict(HandshakeExecution=dict(PlcContractVersion='plc-28-stage-v2'),
            Plc=dict(IpAddress='127.0.0.1'), Agv=dict(Robots=[dict(IpAddress='127.0.0.1')]))
        (root / 'service/appsettings.json').write_text(json.dumps(settings), encoding='utf-8')
        (root / 'data/communication-settings.json').write_text(json.dumps(dict(Configuration=dict(
            Plcs=[dict(IpAddress='192.0.2.10')], Agvs=[dict(IpAddress='127.0.0.1')]))), encoding='utf-8')

    def test_saved_non_loopback_rejected_before_http_rounds(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.configure(root)
            with patch.object(run_rounds, 'resolve_lab_root', return_value=root), \
                 patch.object(run_rounds, 'read_ready', return_value=dict(root=str(root))), \
                 patch('sys.argv', ['run_rounds.py', '--root', str(root)]), \
                 patch('urllib.request.urlopen') as http:
                with self.assertRaisesRegex(RuntimeError, 'non-loopback'):
                    run_rounds.main()
                http.assert_not_called()

    @unittest.skipUnless(run_restart_probe.os.name == 'nt', 'Windows crash probe')
    def test_saved_non_loopback_rejected_before_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.configure(root)
            with patch.object(run_restart_probe, 'resolve_lab_root', return_value=root), \
                 patch.object(run_restart_probe, 'read_ready', return_value=dict(root=str(root))), \
                 patch('sys.argv', ['run_restart_probe.py', '--root', str(root), '--confirm-synthetic-crash-test']), \
                 patch.object(run_restart_probe, 'restart_owned_service') as restart, patch('urllib.request.urlopen') as http:
                with self.assertRaisesRegex(RuntimeError, 'not loopback-only'):
                    run_restart_probe.main()
                restart.assert_not_called()
                http.assert_not_called()

    @unittest.skipUnless(run_restart_probe.os.name == 'nt', 'Windows image ownership check')
    def test_reused_pid_image_mismatch_never_terminated(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            expected = root / 'service/AgvDispatch.Service.exe'
            (root / 'processes.json').write_text(json.dumps([dict(Id=123, Path=str(expected))]), encoding='utf-8')
            kernel = Mock()
            kernel.OpenProcess.return_value = 1
            def image(handle, flags, buffer, length):
                buffer.value = str(root / 'unrelated.exe')
                return 1
            kernel.QueryFullProcessImageNameW.side_effect = image
            with patch.object(run_restart_probe.ctypes, 'WinDLL', return_value=kernel), patch.object(run_restart_probe.subprocess, 'Popen') as launch:
                with self.assertRaisesRegex(RuntimeError, 'PID image'):
                    run_restart_probe.restart_owned_service(root)
                kernel.TerminateProcess.assert_not_called()
                kernel.CloseHandle.assert_called_once()
                launch.assert_not_called()

    def test_ambiguous_ownership_manifest_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            expected = root / 'service/AgvDispatch.Service.exe'
            (root / 'processes.json').write_text(json.dumps([dict(Id=1, Path=str(expected)), dict(Id=2, Path=str(expected))]), encoding='utf-8')
            with self.assertRaisesRegex(RuntimeError, 'unique owned Service'):
                run_restart_probe.restart_owned_service(root)


if __name__ == '__main__':
    unittest.main()
