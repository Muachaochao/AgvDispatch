"""Instance stability for the daily 5199 simulation center (实例不再随启动变化).

- The served controller uses one stable scheme-store scope, so restarting it with whichever instance the
  WebHost discovered keeps the operator's selected instance and scheme.
- remember() writes simulation-center/current-instance.json (read by the WebHost and the site assistant).
- Re-applying an identical configuration to a never-started instance keeps that instance.
- Old instances lose only their copied binaries; lab.json, data and reports stay.
"""
import json
import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import time
import unittest
from unittest.mock import patch

import controller
from controller import configuration_fingerprint, overlapping_edges, prune_instance_binaries
from schemes import STABLE_SCOPE, SchemeStore


class InstanceStabilityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.workspace = Path(self.temp.name)
        self.configuration = dict(mapDefinition=dict(name='车间甲', nodes=[], edges=[], resources=[]),
                                  vehicles=[dict(id='车一')], stations=[dict(stationId='CNC-001')])

    def root(self, name, binaries=True, **lab):
        root = self.workspace / 'artifacts' / ('web-single-vehicle-' + name)
        root.mkdir(parents=True)
        body = dict(simulationOnly=True, simulationRate=8.0, simulationConfiguration=dict(
            mapName='车间甲', vehicles=[{}], stations=[{}], mapIdentity=dict(definitionHash='ABC')), **lab)
        (root / 'lab.json').write_text(json.dumps(body), encoding='utf-8')
        if binaries:
            for directory in ('service', 'webhost'):
                (root / directory).mkdir()
                (root / directory / 'payload.bin').write_bytes(b'x' * 16)
        return root

    def validate(self, root):
        if root.parent != self.workspace / 'artifacts' or not (root / 'lab.json').exists():
            raise ValueError('not an isolated instance')

    def test_stable_scope_survives_controller_launched_from_another_instance(self):
        first = self.root('sim-a')
        second = self.root('sim-b')
        store = SchemeStore(self.workspace, first, self.validate, scope=STABLE_SCOPE)
        scheme = store.save('V4五车十单', self.configuration)
        store.remember(first, scheme['id'])
        # The WebHost now launches the controller with the newest directory (second) as --root.
        restarted = SchemeStore(self.workspace, second, self.validate, scope=STABLE_SCOPE)
        self.assertEqual(restarted.restore_root(), (first, None))
        self.assertEqual(restarted.catalog()['selectedSchemeId'], scheme['id'])
        # The historical per-template scope would have forgotten the selection.
        legacy = SchemeStore(self.workspace, second, self.validate)
        self.assertEqual(legacy.restore_root()[0], second)

    def test_pointer_names_current_instance_and_scheme(self):
        root = self.root('sim-a', configurationFingerprint='f' * 64, createdAt='2026-11-23T10:00:00+00:00')
        store = SchemeStore(self.workspace, root, self.validate, scope=STABLE_SCOPE)
        scheme = store.save('V4五车十单', self.configuration)
        store.remember(root, scheme['id'])
        pointer = json.loads((self.workspace / 'artifacts/simulation-center/current-instance.json').read_text(encoding='utf-8'))
        self.assertEqual(pointer['rootName'], root.name)
        self.assertEqual(pointer['selectedSchemeName'], 'V4五车十单')
        self.assertEqual(pointer['mapDefinitionHash'], 'ABC')
        self.assertEqual(pointer['configurationFingerprint'], 'f' * 64)
        self.assertEqual(store.current_pointer()['rootName'], root.name)
        self.assertEqual(list((self.workspace / 'artifacts/simulation-center').glob('*.tmp')), [])

    def test_invalid_scope_rejected(self):
        with self.assertRaises(ValueError):
            SchemeStore(self.workspace, self.root('sim-a'), self.validate, scope='../x')

    def test_fingerprint_tracks_configuration_rate_and_release(self):
        base = configuration_fingerprint(self.configuration, 8, None)
        self.assertEqual(base, configuration_fingerprint(dict(self.configuration), 8.0, None))
        self.assertNotEqual(base, configuration_fingerprint(self.configuration, 4, None))
        self.assertNotEqual(base, configuration_fingerprint(self.configuration, 8, dict(metadata=dict(releaseId='r2'))))
        changed = dict(self.configuration, vehicles=[dict(id='车二')])
        self.assertNotEqual(base, configuration_fingerprint(changed, 8, None))

    def test_reuse_only_for_identical_never_started_instance(self):
        root = self.root('sim-a', configurationFingerprint='f' * 64)
        runtime = SimpleNamespace(root=root, template_root=self.root('template'), lab=dict(configurationFingerprint='f' * 64))
        reusable = controller.Runtime._reusable_instance
        self.assertTrue(reusable(runtime, 'f' * 64))
        self.assertFalse(reusable(runtime, 'e' * 64))
        (root / 'data').mkdir()
        (root / 'data/agv-dispatch.db').write_bytes(b'')
        self.assertFalse(reusable(runtime, 'f' * 64))

    def test_prune_keeps_newest_current_and_live_instances(self):
        roots = []
        for index in range(5):
            root = self.root('sim-%d' % index)
            stamp = time.time() - 1000 + index * 10
            os.utime(root / 'lab.json', (stamp, stamp))
            roots.append(root)
        (roots[0] / 'simulation-report.json').write_text('{}', encoding='utf-8')
        (roots[1] / 'processes.json').write_text(json.dumps([dict(Id=os.getpid(), Path='x')]), encoding='utf-8')
        removed = prune_instance_binaries(self.workspace / 'artifacts', keep=2, protect={roots[2].name})
        self.assertEqual(removed, [roots[0].name])
        self.assertFalse((roots[0] / 'service').exists())
        self.assertTrue((roots[0] / 'simulation-report.json').exists())
        self.assertTrue((roots[0] / 'lab.json').exists())
        self.assertTrue((roots[0] / 'pruned.json').exists())
        for kept in roots[1:]:
            self.assertTrue((kept / 'service').exists(), kept.name)

    def test_prune_survives_windows_pid_probe_systemerror(self):
        roots = []
        for index in range(2):
            root = self.root('probe-%d' % index)
            stamp = time.time() - 100 + index * 10
            os.utime(root / 'lab.json', (stamp, stamp))
            roots.append(root)
        (roots[0] / 'processes.json').write_text(
            json.dumps([dict(Id=999999, Path='x')]), encoding='utf-8')
        with patch.object(controller, '_pid_alive',
                          side_effect=SystemError("<class 'OSError'> returned a result with an exception set")):
            removed = prune_instance_binaries(self.workspace / 'artifacts', keep=1)
        self.assertEqual(removed, [])
        self.assertTrue((roots[0] / 'service').exists())



class MapOverlapPreflightTests(unittest.TestCase):
    """B07 V4: a reverse edge turned bidirectional next to the original one-way edge passed the Simulation
    Center preflight but the Service refused the map at startup (EDGE_OVERLAP, 12 errors)."""
    def edge(self, id, a, b, bidirectional, **extra):
        return dict(dict(id=id, bidirectional=bidirectional, geometryKind='Line', **{'from': a, 'to': b}), **extra)

    def test_one_way_plus_bidirectional_duplicate_is_reported(self):
        edges = [self.edge('N', 'J12', 'J13', False), self.edge('X', 'J13', 'J12', True)]
        self.assertEqual(overlapping_edges(edges), [['N', 'X']])

    def test_opposite_one_way_pair_is_allowed(self):
        self.assertEqual(overlapping_edges([self.edge('A', 'S', 'J', False), self.edge('B', 'J', 'S', False)]), [])

    def test_curves_with_different_control_points_are_distinct(self):
        edges = [self.edge('A', 'P', 'Q', True, geometryKind='Bezier', controlPoints=[dict(x=1, y=1)]),
                 self.edge('B', 'P', 'Q', True, geometryKind='Bezier', controlPoints=[dict(x=2, y=1)])]
        self.assertEqual(overlapping_edges(edges), [])


if __name__ == '__main__':
    unittest.main()
