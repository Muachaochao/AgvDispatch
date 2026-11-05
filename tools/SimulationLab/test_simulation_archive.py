import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile

from simulation_archive import archive_evidence
from test_runtime_lifecycle import fixture


class EvidenceArchiveTests(unittest.TestCase):
    def test_archive_contains_report_observations_tasks_and_logs(self):
        with tempfile.TemporaryDirectory() as name:
            workspace = Path(name)
            root = fixture(workspace)
            run_id = 'a' * 32
            run = root / ('simulation-run-' + run_id)
            run.mkdir()
            report = dict(id=run_id, name='dispatch-backlog', status='passed', message='测试完成')
            (run / 'report.json').write_text(json.dumps(report, ensure_ascii=False), encoding='utf-8')
            (run / 'map.json').write_text('{"nodes":[]}', encoding='utf-8')
            (run / 'observations.jsonl').write_text('{"time":1}\n', encoding='utf-8')
            details = {'TASK-1': dict(task=dict(stationId='CNC-001', vehicleId='AGV-01', stage='Completed'),
                                    waitingExplanation=None)}
            (run / 'all-task-details.json').write_text(json.dumps(details), encoding='utf-8')
            (run / 'task-identities.json').write_text('{"TASK-1":{"vehicle":"AGV-01"}}', encoding='utf-8')
            (root / 'device-events.jsonl').write_text('{"event":"move"}\n', encoding='utf-8')
            (root / 'simulation-webhost-stdout.log').write_text('ready\n', encoding='utf-8')
            robot = root / 'robots/AGV-01'
            robot.mkdir(parents=True)
            (robot / 'device-events.jsonl').write_text('{"event":"robot"}\n', encoding='utf-8')
            service_log = root / 'service/logs/service'
            service_log.mkdir(parents=True)
            (service_log / 'service.log').write_text('task created\n', encoding='utf-8')

            with patch('controller.WORKSPACE', workspace):
                filename, payload = archive_evidence(SimpleNamespace(root=root, scheme_store=None), run_id)
            self.assertEqual(filename, 'simulation-evidence-' + run_id + '.zip')
            self.assertLess(len(payload), 10 * 1024 * 1024)
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                names = set(archive.namelist())
                self.assertIn('README.txt', names)
                self.assertIn('task-audit.json', names)
                self.assertIn('task-summary.csv', names)
                self.assertIn(run.name + '/report.json', names)
                self.assertIn(run.name + '/observations.jsonl', names)
                self.assertIn('logs/device-events.jsonl', names)
                self.assertIn('logs/simulation-webhost-stdout.log', names)
                self.assertIn('archive-manifest.json', names)
                manifest = json.loads(archive.read('archive-manifest.json'))
                self.assertEqual(manifest['reportId'], run_id)
                self.assertTrue(manifest['files'])


if __name__ == '__main__':
    unittest.main()
