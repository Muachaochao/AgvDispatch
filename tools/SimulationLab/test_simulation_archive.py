import io
from contextlib import closing
import json
from pathlib import Path
import tempfile
import sqlite3
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile

from simulation_archive import archive_evidence, _task_audit
from test_runtime_lifecycle import fixture


class EvidenceArchiveTests(unittest.TestCase):
    def test_oversize_database_payload_has_explicit_digest_and_keeps_original(self):
        with tempfile.TemporaryDirectory() as name:
            root = fixture(Path(name))
            run = root / 'simulation-run-test'
            run.mkdir()
            (run / 'task-identities.json').write_text('{"TASK-1":{}}', encoding='utf-8')
            payload = '工厂地图' * 20000
            database = root / 'data/agv-dispatch.db'
            with closing(sqlite3.connect(database)) as db:
                db.execute('create table receipt (TaskId text, PayloadJson text)')
                db.execute('insert into receipt values (?, ?)', ('TASK-1', payload))
                db.commit()
            audit = _task_audit(root, run)
            row = audit['tables']['receipt'][0]
            self.assertEqual(row['TaskId'], 'TASK-1')
            self.assertTrue(row['PayloadJson']['archiveOmitted'])
            self.assertEqual(row['PayloadJson']['utf8Bytes'], len(payload.encode('utf-8')))
            self.assertEqual(len(row['PayloadJson']['sha256']), 64)
            with closing(sqlite3.connect(database)) as db:
                self.assertEqual(db.execute('select PayloadJson from receipt').fetchone()[0], payload)

    def test_oversize_observations_are_listed_in_manifest(self):
        with tempfile.TemporaryDirectory() as name:
            root = fixture(Path(name))
            run_id = 'b' * 32
            run = root / ('simulation-run-' + run_id)
            run.mkdir()
            (run / 'report.json').write_text(json.dumps(dict(id=run_id, status='passed')), encoding='utf-8')
            (run / 'observations.jsonl').write_text('x' * 32000, encoding='utf-8')
            with patch('controller.WORKSPACE', Path(name)), patch('simulation_archive.MAX_SOURCE_BYTES', 16000):
                _, payload = archive_evidence(SimpleNamespace(root=root, scheme_store=None), run_id)
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                manifest = json.loads(archive.read('archive-manifest.json'))
                self.assertEqual(manifest['omittedFiles'][0]['path'], run.name + '/observations.jsonl')
                self.assertEqual(manifest['omittedFiles'][0]['bytes'], 32000)
            self.assertEqual((run / 'observations.jsonl').stat().st_size, 32000)

    def test_archive_contains_report_observations_tasks_and_logs(self):
        with tempfile.TemporaryDirectory() as name:
            workspace = Path(name)
            root = fixture(workspace)
            run_id = 'a' * 32
            run = root / ('simulation-run-' + run_id)
            run.mkdir()
            report = dict(id=run_id, name='business-calls', status='passed', message='测试完成')
            calls = [dict(stationId='CNC-001', atSeconds=0, vehicleId=None)]
            plan = dict(seed=123, count=1, intervalSeconds=3, stationIds=['CNC-001'], calls=calls)
            (root / 'business-call-plan.json').write_text(json.dumps(plan), encoding='utf-8')
            (run / 'scenario.json').write_text(json.dumps(dict(name='business-calls', calls=calls)), encoding='utf-8')
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
                self.assertEqual(json.loads(archive.read(run.name + '/business-call-plan.json')), plan)
                self.assertIn(run.name + '/observations.jsonl', names)
                self.assertIn('logs/device-events.jsonl', names)
                self.assertIn('logs/simulation-webhost-stdout.log', names)
                self.assertIn('archive-manifest.json', names)
                manifest = json.loads(archive.read('archive-manifest.json'))
                self.assertEqual(manifest['reportId'], run_id)
                self.assertTrue(manifest['files'])
            # A later, different call list must not acquire this plan's seed.
            plan['calls'][0]['atSeconds'] = 9
            (root / 'business-call-plan.json').write_text(json.dumps(plan), encoding='utf-8')
            with patch('controller.WORKSPACE', workspace):
                _, payload = archive_evidence(SimpleNamespace(root=root, scheme_store=None), run_id)
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                self.assertNotIn(run.name + '/business-call-plan.json', archive.namelist())


if __name__ == '__main__':
    unittest.main()
