import copy
import datetime
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import threading
import time
import unittest

from schemes import SchemeStore
from versions import VersionComparison, _assemblies


class SchemeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.workspace = Path(self.temp.name)
        self.template = self.root('template')
        self.configuration = dict(mapDefinition=dict(name='车间甲', nodes=[], edges=[], resources=[]),
                                  vehicles=[dict(id='车一')], stations=[dict(stationId='CNC-001')])

    def root(self, name):
        root = self.workspace / 'artifacts' / ('web-single-vehicle-' + name)
        root.mkdir(parents=True)
        (root / 'lab.json').write_text(json.dumps(dict(simulationOnly=True, simulationConfiguration=dict(
            mapName='车间甲', vehicles=[{}], stations=[{}]))), encoding='utf-8')
        return root

    def validate(self, root):
        if root.parent != self.workspace / 'artifacts' or not (root / 'lab.json').exists():
            raise ValueError('not an isolated instance')

    def store(self):
        return SchemeStore(self.workspace, self.template, self.validate)

    def test_selection_and_recipe_survive_new_controller_without_starting_runtime(self):
        store = self.store()
        scheme = store.save('双车排队方案', self.configuration, dict(name='queued-calls'))
        root = self.root('new')
        store.remember(root, scheme['id'])
        restored = self.store()
        self.assertEqual(restored.restore_root(), (root, None))
        self.assertEqual(restored.catalog()['selectedSchemeId'], scheme['id'])
        self.assertEqual(restored.get(scheme['id'])['configuration'], self.configuration)
        self.assertEqual(restored.get(scheme['id'])['scenario']['name'], 'queued-calls')
        self.assertFalse((root / 'STOP').exists())
        self.assertFalse((root / 'processes.json').exists())

    def test_missing_recent_root_falls_back_preserving_recipes_and_notices(self):
        store = self.store()
        scheme = store.save('日常仿真', self.configuration)
        root = self.root('removed')
        store.remember(root, scheme['id'])
        (root / 'lab.json').unlink()
        restored = self.store()
        selected, notice = restored.restore_root()
        self.assertEqual(selected, self.template)
        self.assertIn('上次实例已不可用', notice)
        self.assertEqual(len(restored.catalog()['schemes']), 1)

    def test_corrupted_catalogue_preserved_instead_of_overwritten(self):
        store = self.store()
        store.path.write_text('{bad', encoding='utf-8')
        restored = self.store()
        self.assertIn('已保留原文件', restored.notice)
        with self.assertRaisesRegex(ValueError, '备份'):
            restored.save('新方案', self.configuration)
        self.assertEqual(store.path.read_text(), '{bad')

    def test_outside_roots_and_path_identifiers_are_rejected(self):
        store = self.store()
        for invalid in ('../../field', '/absolute', 'web-single-vehicle-ok/nested'):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                store._root(invalid)
        outside = self.workspace / 'field' / self.template.name
        with self.assertRaisesRegex(ValueError, '当前项目'):
            store.remember(outside)
        with self.assertRaises(ValueError):
            store.get('../../field')

    def test_overwriting_recipe_retains_identity_and_catalogue_is_detached(self):
        store = self.store()
        first = store.save('原方案', self.configuration)
        updated = store.save('更新后', self.configuration, dict(name='normal-cycle'), first['id'])
        self.assertEqual(first['id'], updated['id'])
        updated['configuration']['vehicles'].clear()
        catalogue = store.catalog()
        catalogue['schemes'].clear()
        self.assertEqual(store.catalog()['schemes'][0]['vehicleCount'], 1)
        self.assertEqual(len(store.catalog()['schemes']), 1)
        self.assertEqual(list(store.path.parent.glob('*.tmp')), [])

    def test_template_namespaces_do_not_replace_each_others_last_selection(self):
        one = self.store()
        selected = self.root('chosen')
        one.remember(selected)
        other = self.root('other-template')
        two = SchemeStore(self.workspace, other, self.validate)
        self.assertEqual(two.restore_root(), (other, None))
        self.assertNotEqual(one.path, two.path)

    def test_catalogue_recovers_recipes_saved_under_other_template_namespaces(self):
        current = self.store()
        first = current.save('当前方案', self.configuration)
        other_template = self.root('older-template')
        older = SchemeStore(self.workspace, other_template, self.validate)
        changed = copy.deepcopy(self.configuration)
        changed['vehicles'].append(dict(id='车二'))
        second = older.save('旧实例中保留的最新方案', changed)

        catalogue = current.catalog()
        self.assertEqual({row['id'] for row in catalogue['schemes']},
                         {first['id'], second['id']})
        self.assertEqual(current.get(second['id'])['configuration'], changed)
        self.assertEqual(catalogue['schemes'][0]['vehicleCount'], 2)

    def test_delete_hides_indirect_recipe_without_rewriting_its_source_store(self):
        current = self.store()
        other_template = self.root('older-template')
        older = SchemeStore(self.workspace, other_template, self.validate)
        value = older.save('旧命名空间方案', self.configuration)

        remaining = current.delete(value['id'])
        self.assertNotIn(value['id'], [row['id'] for row in remaining['schemes']])
        self.assertIn(value['id'], [row['id'] for row in older.catalog()['schemes']])
        self.assertEqual(older.get(value['id'])['name'], '旧命名空间方案')


class VersionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.private = root / 'web-single-vehicle-private'
        self.field = root / 'daily'
        (self.private / 'service').mkdir(parents=True)
        self.field.mkdir()
        for directory in (self.private / 'service', self.field):
            (directory / 'AgvDispatch.Service.dll').write_bytes(b'actual-service')
            (directory / 'AgvDispatch.Application.dll').write_bytes(b'actual-application')
        self.hash = _assemblies(self.field)[-1]['sha256']
        self.runtime = SimpleNamespace(root=self.private, phase='running', web=dict(Port=12345),
            children=dict(service=SimpleNamespace(pid=42)),
            settings=dict(Dispatching=dict(StationPickupSites={'CNC-001': 'PICK'}, DumperWaitingSite='WAIT',
                                          DumperWaitingSites=['WAIT'], DumperDumpingSite='DUMP'),
                          TaskCreation=dict(DumperStationId='DUMPER-001')))
        now = datetime.datetime.now(datetime.timezone.utc).isoformat()
        self.snapshot = dict(receivedAt=now, connected=True, map=dict(definitionHash='map-definition-sha'), status=dict(
            service=dict(processId=42, startedAt=now), execution=dict(buildId=self.hash, informationalVersion='release+commit'),
            safety=dict(activeMapId='map', activeMapVersionId='version')))
        self.parameters = dict(mapId='map', mapVersionId='version', revision='revision', configuration=dict(
            pickups=[dict(stationId='CNC-001', pickupSite='PICK', vehiclePoolId=None, allowedVehicleIds=None)],
            waitingSites=['WAIT'], dumpingSite='DUMP', dumperStationId='DUMPER-001', actions=[], stepTimeouts=[]))
        self.requests = []

    def fetch(self, port, path):
        self.requests.append((port, path))
        return copy.deepcopy(self.snapshot if path.endswith('/snapshot') else self.parameters)

    def comparison(self, **kwargs):
        return VersionComparison(self.runtime, fetch=kwargs.get('fetch', self.fetch), process_image=lambda pid: self.field / 'AgvDispatch.Service.exe')

    def test_actual_assembly_hashes_and_effective_parameters_required_for_match(self):
        result = self.comparison().refresh()
        self.assertEqual(result['status'], 'matched', result)
        self.assertEqual(result['differences'], [])
        self.assertEqual(result['field']['assemblies'][0]['path'], str(self.field / 'AgvDispatch.Application.dll'))
        self.assertEqual(result['simulation']['parametersSource'], '当前仿真后台有效参数')
        self.assertTrue(all(path in ('/api/snapshot', '/api/task-parameters') for _, path in self.requests))

    def test_dependency_difference_detected_even_when_service_report_version_matches(self):
        (self.private / 'service/AgvDispatch.Application.dll').write_bytes(b'changed-dependency')
        result = self.comparison().refresh()
        self.assertEqual(result['status'], 'different')
        self.assertEqual(result['differences'][0]['key'], 'assembly:AgvDispatch.Application.dll')

    def test_reported_hash_cannot_substitute_for_actual_field_process_files(self):
        self.snapshot['status']['execution']['buildId'] = 'f' * 64
        result = self.comparison().refresh()
        self.assertEqual(result['status'], 'unknown')
        self.assertIn('哈希不同', result['message'])

    def test_torn_map_or_old_snapshot_never_claims_a_match(self):
        self.parameters['mapVersionId'] = 'other'
        result = self.comparison().refresh()
        self.assertEqual(result['status'], 'unknown')
        self.assertIn('已切换', result['message'])
        self.parameters['mapVersionId'] = 'version'
        self.snapshot['receivedAt'] = '2020-01-01T00:00:00Z'
        self.assertEqual(self.comparison().refresh()['status'], 'unknown')

    def test_effective_station_diff_is_visible_and_offline_settings_unverified(self):
        def fetch(port, path):
            value = self.fetch(port, path)
            if port == 12345 and path.endswith('/task-parameters'):
                value['configuration']['pickups'][0]['pickupSite'] = 'OTHER'
            return value
        result = self.comparison(fetch=fetch).refresh()
        self.assertEqual(result['status'], 'different')
        self.assertEqual(result['differences'][0]['label'], '工位与取箱位置')
        self.runtime.phase = 'stopped'
        result = self.comparison().refresh()
        self.assertNotEqual(result['status'], 'matched')
        self.assertTrue(any(row['kind'] == 'unverified' for row in result['differences']))

    def test_status_getter_does_not_wait_for_field_api_and_stale_match_is_downgraded(self):
        release = threading.Event()
        def blocked(port, path):
            release.wait(2)
            return self.fetch(port, path)
        comparison = self.comparison(fetch=blocked)
        started = time.monotonic()
        result = comparison.snapshot()
        self.assertLess(time.monotonic() - started, .2)
        self.assertEqual(result['status'], 'unknown')
        release.set()
        while comparison.working and time.monotonic() - started < 2:
            time.sleep(.01)
        comparison.value['observedAt'] = time.time() - 60
        comparison.value['status'] = 'matched'
        comparison.last_attempt = time.time()
        self.assertEqual(comparison.snapshot()['status'], 'unknown')


if __name__ == '__main__':
    unittest.main()
