"""Protect evidence consumers while bounding the repetitive query stream."""
import json
from pathlib import Path
import tempfile
import unittest
from lab_logging import DeviceJournal, rotate_before_launch


class JournalTests(unittest.TestCase):
    def test_rotation_keeps_commands_edges_and_faults_across_restart(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            journal = DeviceJournal(root, max_bytes=300, backups=2)
            journal.write('robokit', api=3066, response={'ret_code': 0})
            journal.write('plc-bit', address='DB1.DBX610.1', value=True)
            for index in range(100):
                journal.write('robokit', api=1100, response={'x': index})
            journal.write('robokit', api=1100, response={'errors': ['fault']})
            journal.write('robokit', api=1303, response={'ret_code': 400})
            journal.close()
            # A new writer must append to the original command history.
            journal = DeviceJournal(root, max_bytes=300, backups=2)
            journal.write('robokit', api=3003, response={'ret_code': 0})
            journal.close()
            events = [json.loads(line) for line in (root/'device-events.jsonl').read_text().splitlines()]
            self.assertEqual([e.get('api') for e in events], [3066, None, 1100, 1303, 3003])
            queries = list(root.glob('device-queries.jsonl*'))
            self.assertEqual(len(queries), 3)
            self.assertTrue(all(p.stat().st_size <= 400 for p in queries))
            self.assertTrue(any(json.loads(line)['response']['x'] == 99 for p in queries for line in p.read_text().splitlines()))

    def test_startup_rotation_preserves_recent_output_and_bounds_history(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)/'service-stdout.log'
            path.write_text('small')
            rotate_before_launch(path, max_bytes=10, backups=2)
            self.assertEqual(path.read_text(), 'small')
            for value in ('a', 'b', 'c'):
                path.write_text(value * 20)
                rotate_before_launch(path, max_bytes=10, backups=2)
            self.assertEqual(path.with_name(path.name+'.1').read_text(), 'c'*20)
            self.assertEqual(path.with_name(path.name+'.2').read_text(), 'b'*20)
            self.assertFalse(path.exists())
            self.assertEqual(len(list(Path(temporary).iterdir())), 2)


if __name__ == '__main__':
    unittest.main()
