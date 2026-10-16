import copy
import hashlib
import json
import math
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from test_validate_capture import candidate, row, ROOT
from review_map_evidence import review, load_input


def scenario():
    capture = candidate()
    capture['origin'] = 'explicit_synthetic_project_regression'
    capture['capturedAt'] = '2026-10-15T12:00:04+08:00'
    local = json.loads((ROOT/'docs/examples/robokit-minimal/minimal-map.json').read_text(encoding='utf-8-sig'))
    local['name'] = 'SIM-MAP'
    points = [dict(instanceName=n['agvSiteId'], className='LocationMark',
                   pos={'x': n['x']+100, 'y': n['y']-50}, dir=0) for n in local['nodes']]
    by_id = {n['id']: n['agvSiteId'] for n in local['nodes']}
    curves = []
    for e in local['edges']:
        curves.append(dict(instanceName=e['id'], className='StraightPath',
                           startPos={'instanceName': by_id[e['from']]}, endPos={'instanceName': by_id[e['to']]}))
        if e['bidirectional']:
            curves.append(dict(instanceName=e['id']+'-reverse', className='StraightPath',
                               startPos={'instanceName': by_id[e['to']]}, endPos={'instanceName': by_id[e['from']]}))
    smap = dict(header={'mapType': '2D-Map', 'mapName': 'SIM-MAP', 'version': '1.0.6',
                       'minPos': {'x': 99, 'y': -51}, 'maxPos': {'x': 106, 'y': -44}, 'resolution': .02},
                normalPosList=[], advancedPointList=points, advancedCurveList=curves)
    row(capture, 1301)['response']['stations'] = [dict(id=p['instanceName'], type=p['className'], **p['pos'], r=0) for p in points]
    row(capture, 1100)['response'].update(current_station='LM0001', x=100, y=-50)
    row(capture, 1303).update(request={'source_id': 'LM0001', 'target_id': 'LM0101'}, response={'ret_code': 0, 'list': ['LM0001', 'LM0101']})
    paths = [('vehicleToPick', ['N_HOME', 'N_PICK']), ('pickToWait', ['N_PICK', 'N_WAIT']),
             ('waitToDump', ['N_WAIT', 'N_DUMP']), ('dumpToPick', ['N_DUMP', 'N_PICK'])]
    samples = []
    for api in (1300, 1022):
        samples.append(copy.deepcopy(row(capture, api)))
    for role, path in paths:
        a, b = (by_id[p] for p in path)
        samples.append(dict(api=1303, request={'source_id': a, 'target_id': b},
                            response={'ret_code': 0, 'list': [a, b]}, completeResponse=True))
    samples.extend(copy.deepcopy(samples[:2]))
    for i, s in enumerate(samples):
        s['capturedAt'] = f'2026-10-15T12:00:{i+1:02d}+08:00'
    plan = dict(schemaVersion=1, localToVendor={'unit': 'm', 'yawRadians': 0, 'translateX': 100, 'translateY': -50,
                'evidence': {'statement': 'SYNTHETIC translation only', 'source': 'test-only, not device'}},
                xyToleranceM=.02, angleToleranceRadians=.02,
                routes=[dict(role=r, logicalPath=p) for r, p in paths],
                routeObservations={'vehicleAlias': capture['vehicleAlias'], 'mapName': 'SIM-MAP',
                    'mapMd5': row(capture, 1300)['response']['current_map_md5'],
                    'startedAt': '2026-10-15T12:00:00+08:00', 'endedAt': '2026-10-15T12:00:10+08:00', 'samples': samples})
    return [capture, smap, local, plan]


class MapReviewTests(unittest.TestCase):
    def run_case(self, inputs):
        return review(*inputs, synthetic=True)

    def test_synthetic_positive_keeps_all_gates_closed(self):
        inputs = scenario(); before = copy.deepcopy(inputs); result = self.run_case(inputs)
        self.assertTrue(result['offlineComparisonPassed'], result['errors'])
        self.assertEqual(inputs, before)
        self.assertEqual(len(result['stations']), 4); self.assertEqual(len(result['routeSegments']), 4)
        for key in ('realDeviceValidated', 'coordinateAlignmentVerified', 'behaviorVerified', 'canEnableCommands', 'canUploadMap'):
            self.assertIs(result[key], False)

    def test_legacy_workspace_and_disabled_preview_not_enabled(self):
        c, s, local, plan = scenario()
        for n in local['nodes']: n['enabled'] = False
        for e in local['edges']: e['enabled'] = False
        result = self.run_case([c, s, {'draft': {'definition': local}}, plan])
        self.assertTrue(result['offlineComparisonPassed']); self.assertEqual(len(result['disabledLocalNodes']), 4)
        self.assertEqual(len(result['disabledLocalEdges']), 4)

    def test_rotated_explicit_transform(self):
        c, s, local, p = scenario(); p['localToVendor']['yawRadians'] = math.pi/2
        for n in local['nodes']: n['x'], n['y'] = n['y'], -n['x']
        self.assertTrue(self.run_case([c, s, local, p])['offlineComparisonPassed'])

    def test_angle_modulo_two_pi(self):
        inputs = scenario(); row(inputs[0], 1301)['response']['stations'][0]['r'] = 2*math.pi
        self.assertTrue(self.run_case(inputs)['offlineComparisonPassed'])

    def test_missing_heading_is_pending_not_zero(self):
        inputs = scenario(); del row(inputs[0], 1301)['response']['stations'][0]['r']
        result = self.run_case(inputs); self.assertTrue(result['offlineComparisonPassed'])
        self.assertIsNone(result['stations'][0]['angleDeltaRadians']); self.assertTrue(any('朝向未核对' in v for v in result['pending']))

    def test_behavior_document_does_not_auto_approve(self):
        inputs = scenario(); inputs[0]['behaviorConfirmation'] = {'taskHistoryRetention': {'statement': 'test', 'source': 'mock.pdf'}}
        result = self.run_case(inputs); r = next(r for r in result['behaviorEvidence'] if r['rule'] == 'taskHistoryRetention')
        self.assertEqual(r['status'], 'provided_unverified'); self.assertFalse(result['behaviorVerified'])

    def test_synthetic_rejected_without_flag(self):
        self.assertFalse(review(*scenario())['offlineComparisonPassed'])

    def test_csv_formula_ids_escaped(self):
        from review_map_evidence import save_report
        inputs = json.loads(json.dumps(scenario()).replace('LM0001', '=UNTRUSTED'))
        result = self.run_case(inputs); self.assertTrue(result['offlineComparisonPassed'], result['errors'])
        with tempfile.TemporaryDirectory() as t:
            save_report(Path(t), result)
            self.assertIn("'=UNTRUSTED", (Path(t)/'逐站核对.csv').read_text(encoding='utf-8-sig'))

    def test_cli_rejects_and_retains_bad_json(self):
        with tempfile.TemporaryDirectory() as t:
            base = Path(t); bad = base/'bad.json'; bad.write_bytes(b'{"id":1,"id":2}')
            dest = base/'review'; result = subprocess.run([sys.executable, str(Path(__file__).with_name('review_map_evidence.py')),
                '--capture', str(bad), '--smap', str(bad), '--local-map', str(bad), '--plan', str(bad), '--out', str(dest)], capture_output=True)
            self.assertEqual(result.returncode, 2)
            self.assertEqual((dest/'inputs/capture.json').read_bytes(), bad.read_bytes())
            saved = json.loads((dest/'review.json').read_text(encoding='utf-8')); self.assertFalse(saved['offlineComparisonPassed'])
            self.assertEqual(saved['inputs'][0]['sha256'], hashlib.sha256(bad.read_bytes()).hexdigest())

    def test_strict_json_and_budget(self):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t)/'bad.json'
            for raw in (b'{"a":1,"a":2}', b'{"a":NaN}', b'{"a":1e999}', b'PK\x03\x04'):
                p.write_bytes(raw)
                with self.assertRaises((ValueError, UnicodeDecodeError)): load_input(p, 100)
            p.write_bytes(b' '*101)
            with self.assertRaises(ValueError): load_input(p, 100)

    def test_cli_preserves_bytes_and_refuses_existing_output(self):
        with tempfile.TemporaryDirectory() as t:
            base = Path(t); args = []; originals = []
            for key, value in zip(('capture', 'smap', 'local-map', 'plan'), scenario()):
                path = base/(key+'.json'); raw = json.dumps(value, indent=3).encode(); path.write_bytes(raw)
                originals.append((key, path, raw)); args += ['--'+key, str(path)]
            dest = base/'out'; command = [sys.executable, str(Path(__file__).with_name('review_map_evidence.py')), *args, '--out', str(dest), '--synthetic']
            completed = subprocess.run(command, capture_output=True); self.assertEqual(completed.returncode, 0, completed.stderr)
            result = json.loads((dest/'review.json').read_text(encoding='utf-8'))
            for key, path, raw in originals:
                self.assertEqual(path.read_bytes(), raw); self.assertEqual((dest/'inputs'/(key.replace('-', '_')+'.json')).read_bytes(), raw)
                self.assertEqual(next(m for m in result['inputs'] if m['role'] == key.replace('-', '_'))['sha256'], hashlib.sha256(raw).hexdigest())
            prior = (dest/'review.json').read_bytes(); again = subprocess.run(command, capture_output=True)
            self.assertNotEqual(again.returncode, 0); self.assertEqual((dest/'review.json').read_bytes(), prior)
            self.assertTrue((dest/'逐站核对.csv').exists()); self.assertTrue((dest/'四段运输核对.csv').exists()); self.assertTrue((dest/'厂家行为待核定.csv').exists())


def add_negative(name, mutate):
    def test(self):
        inputs = scenario(); mutate(inputs)
        try: result = self.run_case(inputs)
        except (ValueError, TypeError, KeyError): return
        self.assertFalse(result['offlineComparisonPassed'], name)
    setattr(MapReviewTests, 'test_reject_'+name, test)


CASES = {
    'wrong_map': lambda a: a[1]['header'].update(mapName='OTHER'),
    'device_xy': lambda a: row(a[0], 1301)['response']['stations'][0].update(x=200),
    'wrong_transform': lambda a: a[3]['localToVendor'].update(translateX=0),
    'unit_mm': lambda a: a[2].update(unit='mm'),
    'duplicate_mapping': lambda a: a[2]['nodes'][1].update(agvSiteId='LM0001'),
    'missing_mapping': lambda a: a[2]['nodes'][0].pop('agvSiteId'),
    'case_changed': lambda a: a[2]['nodes'][0].update(agvSiteId='lm0001'),
    'native_missing': lambda a: a[1]['advancedPointList'].pop(),
    'native_duplicate_case': lambda a: a[1]['advancedPointList'].append(dict(a[1]['advancedPointList'][0], instanceName='lm0001')),
    'angle_mismatch': lambda a: row(a[0], 1301)['response']['stations'][0].update(r=1),
    'missing_1301_xy': lambda a: row(a[0], 1301)['response']['stations'][0].pop('x'),
    'local_reverse_only': lambda a: a[2]['edges'][1].update(**{'from': 'N_WAIT', 'to': 'N_PICK'}),
    'native_reverse_only': lambda a: a[1]['advancedCurveList'][2].update(startPos={'instanceName': 'LM0201'}, endPos={'instanceName': 'LM0101'}),
    'unknown_arc': lambda a: a[1]['advancedCurveList'][2].update(className='ArcPath'),
    'embedded_endpoint': lambda a: a[1]['advancedCurveList'][0]['startPos'].update(pos={'x': 99, 'y': -50}),
    'path_is_multihop': lambda a: a[3]['routeObservations']['samples'][2]['response'].update(list=['LM0001', 'LM0201', 'LM0101']),
    'partial_routes': lambda a: a[3]['routes'].pop(),
    'duplicate_role': lambda a: a[3]['routes'][1].update(role='vehicleToPick'),
    'route_chain': lambda a: a[3]['routes'][0].update(logicalPath=['N_HOME', 'N_PICK', 'N_WAIT']),
    'first_vehicle_station': lambda a: row(a[0], 1100)['response'].update(current_station='LM0202'),
    'missing_current_station': lambda a: row(a[0], 1100)['response'].pop('current_station'),
    'wrong_route_vehicle': lambda a: a[3]['routeObservations'].update(vehicleAlias='OTHER'),
    'changed_route_map': lambda a: a[3]['routeObservations']['samples'][-2]['response'].update(current_map='OTHER'),
    'loading_incomplete': lambda a: a[3]['routeObservations']['samples'][-1]['response'].update(loadmap_status=0),
    'missing_route_bracket': lambda a: a[3]['routeObservations']['samples'].pop(),
    'no_item_timestamp': lambda a: a[3]['routeObservations']['samples'][2].pop('capturedAt'),
    'out_of_window': lambda a: a[3]['routeObservations']['samples'][2].update(capturedAt='2026-10-15T13:00:00+08:00'),
    'reversed_time': lambda a: a[3]['routeObservations']['samples'][3].update(capturedAt='2026-10-15T12:00:01+08:00'),
    'negative_return_code': lambda a: a[3]['routeObservations']['samples'][2]['response'].update(ret_code=1),
    'unknown_format': lambda a: a[1]['header'].update(version='9.0.0'),
    'non_readonly_api': lambda a: a[3]['routeObservations']['samples'][2].update(api=3066),
    'too_loose_tolerance': lambda a: a[3].update(xyToleranceM=1),
    'invalid_transform': lambda a: a[3]['localToVendor'].update(yawRadians=True),
    'invalid_source_heading': lambda a: a[1]['advancedPointList'][0].update(dir='0'),
    'invalid_device_heading': lambda a: row(a[0], 1301)['response']['stations'][0].update(r=True),
    'invalid_ignore_direction': lambda a: a[1]['advancedPointList'][0].update(ignoreDir=1),
    'cross_namespace_collision': lambda a: a[2]['nodes'][0].update(id='LM0101'),
    'loop_transport': lambda a: a[3]['routes'][0].update(logicalPath=['N_HOME','N_PICK','N_HOME']),
}
for name, mutate in CASES.items(): add_negative(name, mutate)


if __name__ == '__main__': unittest.main(verbosity=2)
