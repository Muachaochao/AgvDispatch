"""Path-only regression; does not launch services or touch a running instance."""
import json
import tempfile
import unittest
from pathlib import Path

from lab_paths import DEFAULT_LAB_NAME, read_ready, resolve_lab_root


class LabPathsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='agv-lab-path-')
        self.workspace = Path(self.temporary.name) / 'DispatchingSystem'
        self.artifacts = self.workspace / 'artifacts'
        self.root = self.artifacts / DEFAULT_LAB_NAME
        self.root.mkdir(parents=True)
        (self.root / 'lab.json').write_text('{}', encoding='utf-8')

    def tearDown(self):
        self.temporary.cleanup()

    def ready(self, **overrides):
        value = dict(root=str(self.root / 'runtime-test'), url='http://127.0.0.1:45678/', port=45678, pipe='isolated')
        value.update(overrides)
        (self.root / 'ready.json').write_text(json.dumps(value), encoding='utf-8')

    def test_new_default(self):
        self.assertEqual(resolve_lab_root([], self.workspace), self.root.resolve())

    def test_explicit_old_root_preserved(self):
        old = self.artifacts / 'web-single-vehicle-20261020'
        old.mkdir()
        (old / 'lab.json').write_text('{}', encoding='utf-8')
        self.assertEqual(resolve_lab_root(['--root', 'artifacts/web-single-vehicle-20261020', '--fresh-devices'], self.workspace), old.resolve())
        self.assertFalse((old / 'ready.json').exists())

    def test_reject_external_root(self):
        with self.assertRaises(RuntimeError):
            resolve_lab_root(['--root', str(self.workspace.parent / 'external')], self.workspace)

    def test_reject_unprepared(self):
        with self.assertRaises(RuntimeError):
            resolve_lab_root(['--root', 'artifacts/web-single-vehicle-missing'], self.workspace)

    def test_read_ready_private_runtime(self):
        self.ready()
        self.assertEqual(Path(read_ready(self.root)['root']), self.root / 'runtime-test')

    def test_reject_ready_from_another_lab(self):
        self.ready(root=str(self.artifacts / 'web-single-vehicle-other'))
        with self.assertRaises(RuntimeError):
            read_ready(self.root)

    def test_reject_nonloopback_ready(self):
        for url in ['http://192.168.1.10:45678/', 'https://127.0.0.1:45678/', 'http://user@127.0.0.1:45678/', 'http://127.0.0.1:45679/']:
            self.ready(url=url)
            with self.subTest(url=url), self.assertRaises(RuntimeError):
                read_ready(self.root)


if __name__ == '__main__':
    unittest.main()
