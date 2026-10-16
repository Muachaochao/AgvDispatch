import copy,json,tempfile,unittest,subprocess,sys
from pathlib import Path
from validate_capture import review,load

ROOT=Path(__file__).resolve().parents[2]
def candidate():
    # Test-only declared-capture shape, built from synthetic data. Never imported as real evidence.
    b=json.loads((ROOT/'docs/examples/robokit-regression-v1/simulated-bundle.json').read_text(encoding='utf-8'))
    def s(n,req=None):return {'api':n,'request':req,'response':copy.deepcopy(b['responses'][str(n)]),'completeResponse':True}
    return {'schemaVersion':1,'origin':'real_device_read_only_capture','deviceValidated':False,'capturedAt':'2026-10-14T20:00:00+08:00','vehicleAlias':'TEST-SYNTHETIC','actualRobokitVersion':'SIMULATED-NOT-DEVICE','actualProtocolVersion':'SIMULATED-NOT-DEVICE','anonymizationNotes':'synthetic unit test only','actualStatusPortConfirmed':True,'sameCollectionBatch':True,'samples':[
        s(1000),s(1100,{'return_laser':False,'return_beams3D':False}),s(1300),s(1022),s(1301),s(1302,{'map_names':['SIM-MAP.smap']}),s(1303,{'source_id':'LM1','target_id':'AP1'}),s(1110,{'task_ids':[]}),s(1300),s(1022)],'behaviorConfirmation':{}}
def row(d,n):return next(s for s in d['samples'] if s['api']==n)

class ReviewTests(unittest.TestCase):
    def test_valid_structure_not_authenticity(self):
        r=review(candidate());self.assertTrue(r['schemaReady']);self.assertFalse(r['realDeviceValidated']);self.assertFalse(r['behaviorVerified']);self.assertEqual(len(r['pendingBehaviorEvidence']),7)
    def test_blank_template_rejected(self):
        d=json.loads((ROOT/'docs/examples/robokit-regression-v1/真实样例提交模板.json').read_text(encoding='utf-8'));self.assertFalse(review(d)['schemaReady'])
    def test_unknown_fields_preserved(self):
        d=candidate();row(d,1100)['response']['vendor_extra']={'value':[1,'two']};before=copy.deepcopy(d);self.assertTrue(review(d)['schemaReady']);self.assertEqual(d,before)
    def test_missing_telemetry_stays_unknown(self):
        d=candidate();row(d,1100)['response']={'ret_code':0};r=review(d);self.assertTrue(r['schemaReady']);self.assertTrue(any('1100缺少' in w for w in r['warnings']))
    def test_malformed_json(self):
        for content in ['{"a":1,"a":2}','{"v":NaN}','{"v":1e999}','{']:
            with self.subTest(content=content),tempfile.TemporaryDirectory() as t:
                p=Path(t)/'a.json';p.write_text(content);self.assertRaises(ValueError,load,p)
    def test_cli_pending_template_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            result=subprocess.run([sys.executable,str(Path(__file__).with_name('validate_capture.py')),str(ROOT/'docs/examples/robokit-regression-v1/真实样例提交模板.json'),'--out',str(Path(t)/'review')],capture_output=True)
            self.assertEqual(result.returncode,2)
            self.assertFalse(json.loads((Path(t)/'review/review.json').read_text(encoding='utf-8'))['schemaReady'])
    def test_cli_bytes_preserved_not_device_approved(self):
        with tempfile.TemporaryDirectory() as t:
            p=Path(t)/'synthetic-unit-test.json';raw=json.dumps(candidate(),indent=3).encode('utf-8');p.write_bytes(raw)
            result=subprocess.run([sys.executable,str(Path(__file__).with_name('validate_capture.py')),str(p),'--out',str(Path(t)/'review')],capture_output=True)
            self.assertEqual(result.returncode,0);self.assertEqual((Path(t)/'review/submitted.json').read_bytes(),raw)
            r=json.loads((Path(t)/'review/review.json').read_text(encoding='utf-8'));self.assertTrue(r['schemaReady']);self.assertFalse(r['realDeviceValidated']);self.assertFalse(r['behaviorVerified'])
    def test_cli_existing_destination_never_overwritten(self):
        with tempfile.TemporaryDirectory() as t:
            dest=Path(t)/'existing';dest.mkdir();sentinel=dest/'review.json';sentinel.write_bytes(b'previous-evidence')
            result=subprocess.run([sys.executable,str(Path(__file__).with_name('validate_capture.py')),str(ROOT/'docs/examples/robokit-regression-v1/真实样例提交模板.json'),'--out',str(dest)],capture_output=True)
            self.assertNotEqual(result.returncode,0);self.assertEqual(sentinel.read_bytes(),b'previous-evidence')

def add_case(name,mutate):
    def test(self):
        d=candidate();mutate(d);self.assertFalse(review(d)['schemaReady'],name)
    setattr(ReviewTests,'test_'+name,test)

cases={
 'simulated_origin':lambda d:d.update(origin='explicit_synthetic_project_regression'),
 'self_approved':lambda d:d.update(deviceValidated=True),
 'missing_version_metadata':lambda d:d.update(actualRobokitVersion=None),
 'timestamp_without_zone':lambda d:d.update(capturedAt='2026-10-14T20:00:00'),
 'unconfirmed_port':lambda d:d.update(actualStatusPortConfirmed=False),
 'different_batch':lambda d:d.update(sameCollectionBatch=False),
 'partial_response':lambda d:row(d,1100).update(completeResponse=False),
 'missing_response':lambda d:row(d,1100).update(response=None),
 'write_api':lambda d:d['samples'].append({'api':3003,'request':{},'response':{'ret_code':0}}),
 'missing_api':lambda d:d['samples'].remove(row(d,1301)),
 'version_mismatch':lambda d:row(d,1000)['response'].update(version='OTHER'),
 'only_one_map_read':lambda d:d['samples'].pop(8),
 'changed_map':lambda d:d['samples'][8]['response'].update(current_map='OTHER'),
 'load_not_ready':lambda d:d['samples'][9]['response'].update(loadmap_status=0),
 'wrong_capture_order':lambda d:d['samples'].insert(0,d['samples'].pop(6)),
 'short_md5':lambda d:row(d,1300)['response'].update(current_map_md5='a'*31),
 'hash_mismatch':lambda d:row(d,1302)['response']['map_info'][0].update(md5='f'*32),
 'duplicate_hash_row':lambda d:row(d,1302)['response']['map_info'].append(copy.deepcopy(row(d,1302)['response']['map_info'][0])),
 'wrong_map_request':lambda d:row(d,1302)['request'].update(map_names=['OTHER.smap']),
 'duplicate_station':lambda d:row(d,1301)['response']['stations'][1].update(id='LM1'),
 'wrong_path_end':lambda d:row(d,1303)['response'].update(list=['AP1','LM2','LM1']),
 'unknown_path_site':lambda d:row(d,1303)['response'].update(list=['LM1','LM999','AP1']),
 'numeric_string':lambda d:row(d,1100)['response'].update(x='1.25'),
 'bool_as_number':lambda d:row(d,1100)['response'].update(x=True),
 'invalid_boolean':lambda d:row(d,1100)['response'].update(emergency=0),
 'nonzero_ret_code':lambda d:row(d,1301)['response'].update(ret_code=40002),
 'empty_request_extra_tasks':lambda d:row(d,1110)['response']['task_status_package'].update(task_status_list=[{'task_id':'OTHER','status':2}]),
 'invalid_task_status':lambda d:(row(d,1110)['request'].update(task_ids=['TASK']),row(d,1110)['response']['task_status_package'].update(task_status_list=[{'task_id':'TASK','status':True}])),
 'duplicate_api':lambda d:d['samples'].append(copy.deepcopy(row(d,1100))),
 'invalid_no_param_request':lambda d:row(d,1000).update(request={})}
for name,mutate in cases.items():add_case(name,mutate)
if __name__=='__main__':unittest.main(verbosity=2)
