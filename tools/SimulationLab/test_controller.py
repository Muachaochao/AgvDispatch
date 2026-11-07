import copy
import importlib.util
import json
import math
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from controller import Runtime, WORKSPACE, validate_root, write
from scenarios import Observer, separation, validate_scenario
from test_runtime_lifecycle import SOURCE_PLC_TABLE


class SimulationTests(unittest.TestCase):
    def custom_payload(self):
        nodes = [dict(id=name, agvSiteId='LM'+str(index), x=index, y=index % 2, enabled=True, resourceIds=[])
                 for index,name in enumerate(('P1','P2','WAIT','DUMP','PARK1','PARK2'))]
        edges = [dict(id='E'+str(index), **{'from':a,'to':b}, enabled=True, bidirectional=True)
                 for index,(a,b) in enumerate((('P1','WAIT'),('P2','WAIT'),('WAIT','DUMP'),('DUMP','PARK1'),('PARK1','PARK2')))]
        return dict(name='custom', inspectionSite='PARK1', stations=[
            dict(stationId='CNC-001', pickupSite='P1'), dict(stationId='CNC-002', pickupSite='P2')],
            dumper=dict(dumpingSite='DUMP', waitingSite='WAIT', resourceId='R-DUMP'),
            vehicles=[dict(id='CAR-1', initialSite='PARK1', radiusMeters=.5), dict(id='CAR-2', initialSite='PARK2', radiusMeters=.5)],
            plcSignalTable=dict(fileName='plc-signals.csv', csvText=SOURCE_PLC_TABLE.read_text(encoding='utf-8-sig')),
            mapDefinition=dict(name='custom', nodes=nodes, edges=edges, resources=[
                dict(id='R-DUMP', capacity=1, boundary=[dict(x=2,y=0),dict(x=3,y=0),dict(x=3,y=1)])]))

    def test_custom_map_vehicles_stations_and_resource_are_validated(self):
        runtime = object.__new__(Runtime)
        value = runtime._validate_configuration(self.custom_payload())
        self.assertEqual([s['stationId'] for s in value['stations']], ['CNC-001','CNC-002'])
        self.assertEqual([v['id'] for v in value['vehicles']], ['CAR-1','CAR-2'])
        self.assertRegex(value['mapIdentity']['definitionHash'], r'^[A-F0-9]{64}$')
        self.assertEqual(value['plcSignalTable']['mapDefinitionHash'], value['mapIdentity']['definitionHash'])
        field_identity = self.custom_payload()
        field_identity['mapIdentity'] = dict(mapId='MAP-V13', mapVersionId='MAPV-V13', sourceName='ZM-AGVPro',
                                            definitionHash='26e5d66dab3ac5162f60e725fba09a1cfee fcb4eb1dcfc6c171fc7801be3b37e'.replace(' ', ''))
        identified = runtime._validate_configuration(field_identity)
        self.assertEqual(identified['mapIdentity']['definitionHash'], field_identity['mapIdentity']['definitionHash'].upper())
        self.assertEqual(identified['plcSignalTable']['mapDefinitionHash'], identified['mapIdentity']['definitionHash'])
        self.assertEqual((value['vehicles'][0]['lengthMeters'], value['vehicles'][0]['widthMeters']), (1.0, 1.0))
        self.assertEqual(value['dumper']['resourceId'], 'R-DUMP')
        sized = self.custom_payload(); sized['vehicles'][0].update(lengthMeters=.8, widthMeters=.52)
        normalized = runtime._validate_configuration(sized)['vehicles'][0]
        self.assertEqual((normalized['lengthMeters'], normalized['widthMeters']), (.8, .52))
        self.assertAlmostEqual(normalized['radiusMeters'], .5)
        broken = self.custom_payload(); broken['vehicles'][0].update(lengthMeters=.52, widthMeters=.8)
        with self.assertRaisesRegex(ValueError, '车辆长宽'): runtime._validate_configuration(broken)
        broken = self.custom_payload(); broken['vehicles'][0]['initialSite'] = 'MISSING'
        with self.assertRaises(ValueError): runtime._validate_configuration(broken)
        broken = self.custom_payload(); broken['vehicles'][0]['voltageCurve'] = [dict(percent=0, voltage=21)]
        with self.assertRaisesRegex(ValueError, '电压曲线'): runtime._validate_configuration(broken)
        disconnected = self.custom_payload(); disconnected['mapDefinition']['edges'] = disconnected['mapDefinition']['edges'][:-1]
        with self.assertRaisesRegex(ValueError, '不连通'): runtime._validate_configuration(disconnected)
        missing_table = self.custom_payload(); missing_table.pop('plcSignalTable')
        with self.assertRaisesRegex(ValueError, '必须明确绑定PLC点表'):
            runtime._validate_configuration(missing_table)

    def test_multiple_waiting_bays_survive_configuration_and_reject_duplicates(self):
        runtime = object.__new__(Runtime)
        payload = self.custom_payload()
        payload['dumper']['waitingSites'] = ['WAIT', 'PARK1']
        self.assertEqual(runtime._validate_configuration(payload)['dumper']['waitingSites'], ['WAIT', 'PARK1'])
        for sites in (['WAIT','WAIT'], ['PARK1','WAIT'], ['WAIT','DUMP'], ['WAIT','MISSING']):
            payload = self.custom_payload(); payload['dumper']['waitingSites'] = sites
            with self.assertRaises(ValueError): runtime._validate_configuration(payload)

    def test_map_activation_bootstrap_uses_typed_safe_stops_before_field_waypoints(self):
        runtime = object.__new__(Runtime)
        payload = self.custom_payload()
        for node in payload['mapDefinition']['nodes']:
            node['type'] = {'P1': 'Station', 'P2': 'Station', 'WAIT': 'WaitingPoint',
                            'DUMP': 'Station', 'PARK1': 'Station', 'PARK2': 'Waypoint'}[node['id']]
        configuration = runtime._validate_configuration(payload)
        sites = runtime._bootstrap_sites(configuration['mapDefinition'], configuration)
        typed_safe = {node['id'] for node in configuration['mapDefinition']['nodes']
                      if node.get('type') in ('Station', 'WaitingPoint', 'ChargingPoint')}
        self.assertEqual(set(sites.values()) - typed_safe, set())
        self.assertNotEqual(set(sites.values()), {'PARK1', 'PARK2'})
        self.assertEqual({v['id']: v['initialSite'] for v in configuration['vehicles']},
                         {'CAR-1': 'PARK1', 'CAR-2': 'PARK2'})

    def test_custom_call_plan_limits_duplicate_station_batches(self):
        value = validate_scenario(dict(name='custom-calls', rounds=1, intervalSeconds=0, timeoutSeconds=90,
            calls=[dict(stationId='CNC-001', atSeconds=0), dict(stationId='CNC-002', atSeconds=0),
                   dict(stationId='CNC-001', atSeconds=2, vehicleId='CAR-1')]))
        self.assertEqual(len(value['calls']), 3)
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='custom-calls', calls=[
                dict(stationId='CNC-001', atSeconds=0), dict(stationId='CNC-001', atSeconds=0)]))
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='normal-cycle', calls=[dict(stationId='CNC-001', atSeconds=0)]))
        self.assertEqual(validate_scenario(dict(name='plc-recovery', rounds=1, plcFault='done', holdSeconds=12))['plcFault'], 'done')
        self.assertEqual(validate_scenario(dict(name='service-restart', rounds=1, restartStage='pickup'))['restartStage'], 'pickup')
        self.assertEqual(validate_scenario(dict(name='vendor-cancel-return', rounds=1))['name'], 'vendor-cancel-return')
        self.assertEqual(validate_scenario(dict(name='response-loss', rounds=1))['name'], 'response-loss')
        self.assertEqual(validate_scenario(dict(name='road-block-recovery', rounds=1, holdSeconds=7))['holdSeconds'], 7)
        self.assertEqual(validate_scenario(dict(name='obstacle-auto-detour', rounds=1, holdSeconds=8))['holdSeconds'], 8)
        self.assertEqual(validate_scenario(dict(name='obstacle-timeout-pickup', rounds=1,
                                                holdSeconds=3))['holdSeconds'], 3)
        self.assertEqual(validate_scenario(dict(name='obstacle-timeout-fullbox', rounds=1))['name'],
                         'obstacle-timeout-fullbox')
        self.assertEqual(validate_scenario(dict(name='obstacle-timeout-return', rounds=1))['name'],
                         'obstacle-timeout-return')
        self.assertEqual(validate_scenario(dict(name='obstacle-timeout-midroad', rounds=1))['name'],
                         'obstacle-timeout-midroad')
        self.assertEqual(validate_scenario(dict(name='obstacle-timeout-safe-site', rounds=1))['name'],
                         'obstacle-timeout-safe-site')
        self.assertEqual(validate_scenario(dict(name='obstacle-timeout-manual', rounds=1))['name'],
                         'obstacle-timeout-manual')
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='plc-recovery', rounds=1, plcFault='unknown'))
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='vendor-cancel-return', rounds=2))
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='road-block-recovery', rounds=1, holdSeconds=0))
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='obstacle-auto-detour', rounds=2))
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='obstacle-timeout-pickup', rounds=2))

    def test_scenario_rejects_nonfinite_and_invalid_inputs(self):
        for value in ({'rounds': True}, {'rounds': 101}, {'timeoutSeconds': math.nan}, {'intervalSeconds': math.inf}, {'name':'arbitrary-process'}, {'name':'service-restart','rounds':2}, {'script':'bad'}):
            with self.subTest(value=value), self.assertRaises(ValueError): validate_scenario(value)
        self.assertEqual(validate_scenario({'name':'simultaneous-calls','rounds':100})['rounds'], 100)
        self.assertEqual(
            validate_scenario({'name':'dispatch-backlog','timeoutSeconds':86400,
                               'calls':[{'stationId':'CNC-001','atSeconds':0}]})['timeoutSeconds'],
            86400)
        with self.assertRaises(ValueError):
            validate_scenario({'name':'normal-cycle','timeoutSeconds':86401})

    def test_continuous_sample_collision_detects_crossing_not_just_endpoints(self):
        point = lambda x,y: dict(x=x,y=y)
        self.assertEqual(separation(point(-2,0),point(2,0),point(0,-2),point(0,2)),0)
        self.assertEqual(separation(point(-2,0),point(2,0),point(-2,3),point(2,3)),3)

    def test_external_and_production_roots_are_rejected(self):
        for root in (WORKSPACE, WORKSPACE.parent / '发布包/运行数据/后台5199', WORKSPACE / 'artifacts'):
            with self.assertRaises(ValueError): validate_root(root)

    def test_control_patches_do_not_rearm_response_loss(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = object.__new__(Runtime)
            runtime.root = Path(name); runtime.lock = threading.RLock()
            runtime.settings = {'Agv':{'Robots':[{'VehicleId':'a'}]}}
            runtime.controls = dict(sequence='old',vehicles={'a':dict(dropNextResponse=True,paused=False)},plc={})
            runtime.status = lambda: dict(fresh=True)
            write(runtime.root / 'navigation.agvmap.json', {'edges':[{'id':'road'}]})
            runtime.control(dict(vehicles={'a':dict(paused=True)}))
            self.assertFalse(runtime.controls['vehicles']['a']['dropNextResponse'])
            self.assertTrue(runtime.controls['vehicles']['a']['paused'])
            with self.assertRaises(ValueError): runtime.control(dict(vehicles={'a':dict(blockedEdges=['unknown'])}))
            self.assertEqual(runtime.controls['vehicles']['a'].get('blockedEdges'),None)

    def test_recovery_decision_requires_fresh_explicit_review_and_current_revision(self):
        runtime = object.__new__(Runtime)
        runtime.status = lambda: dict(fresh=True)
        recovery = dict(sessionId='session', released=False, items=[dict(ticketId='ticket', revision=4, verified=False)])
        calls = []
        def api(path, payload=None):
            calls.append((path, payload))
            return dict(recovery) if path == '/api/recovery' else dict(Accepted=True)
        runtime.api = api
        payload = dict(sessionId='session', ticketId='ticket', revision=4, decision=10, materialReference='box-and-task-reviewed',
                       reason='fresh evidence reviewed', vehicleAndLocationConfirmed=True, loadAndOwnershipConfirmed=True,
                       plcCycleConfirmed=True)
        result = runtime.decide_recovery(payload)
        self.assertTrue(result['result']['Accepted'])
        for changed in (dict(payload, revision=3), dict(payload, vehicleAndLocationConfirmed=False)):
            with self.assertRaises(ValueError): runtime.decide_recovery(changed)
        runtime.status = lambda: dict(fresh=False)
        with self.assertRaisesRegex(ValueError, '观测已过期'): runtime.decide_recovery(payload)

    def test_stale_status_never_claims_ready(self):
        runtime = object.__new__(Runtime)
        runtime.phase='running'; runtime.lock=threading.RLock(); runtime.children={}; runtime.scenario={};runtime.report=None;runtime.controls={};runtime.error=None
        runtime.root=Path('test'); runtime.device=lambda:dict(time=time.time()-10)
        runtime.history_cache=(time.monotonic(), []); runtime.guidance_cache=(0, [])
        runtime.scheme_store=None; runtime.startup_plan=None
        with patch('controller.read',return_value={}): self.assertFalse(runtime.status()['fresh'])

    def test_task_resources_project_the_readonly_service_snapshot(self):
        runtime = object.__new__(Runtime)
        runtime.lock = threading.RLock()
        calls = []
        runtime.api = lambda path: calls.append(path) or {
            'resources': [dict(resourceId='RDUMP', capacity=1, occupiedCount=1)]}
        self.assertEqual(runtime.task_resources()[0]['resourceId'], 'RDUMP')
        self.assertEqual(calls, ['/api/snapshot'])

    def test_stopped_environment_uses_saved_report_task_evidence(self):
        runtime = object.__new__(Runtime)
        runtime.lock = threading.RLock()
        runtime.api = lambda path: (_ for _ in ()).throw(ConnectionRefusedError('service stopped'))
        runtime.report = {
            'finishedAt': 1000,
            'taskSummary': [dict(
                taskId='TASK-1', stationId='CNC-024', vehicleId='AGV-03', stage='Completed', completed=True,
                currentStepNumber=9, steps=[
                    dict(number=index, stepType=step, status='Completed', waitingExplanation='')
                    for index, step in enumerate(('GoToPickup', 'PickupFullBox', 'DeliverFullBoxToDumper',
                        'WaitForDumperReady', 'EnterDumpingPosition', 'DumpFullBox', 'ReturnEmptyBox',
                        'PlaceEmptyBox', 'CompletePlcHandshake'), 1)])]}
        page = runtime.task_page({'filter': ['All'], 'page': ['1'], 'pageSize': ['20']})
        self.assertEqual(page['summary']['completed'], 1)
        self.assertEqual(page['evidenceSource'], 'saved-report')
        detail = runtime.task_detail('TASK-1')
        self.assertEqual(detail['evidenceSource'], 'saved-report')
        self.assertEqual(len(detail['steps']), 9)
        self.assertEqual(detail['steps'][8]['stepType'], 'CompletePlcHandshake')
        self.assertEqual(runtime.task_resources(), [])

    def test_task_page_rejects_pages_larger_than_the_production_api_limit(self):
        runtime = object.__new__(Runtime)
        runtime.lock = threading.RLock()
        with self.assertRaisesRegex(ValueError, '任务查询参数无效'):
            runtime.task_page({'pageSize': ['201']})

    def test_readonly_observer_detects_overcapacity(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = object.__new__(Runtime); runtime.root=Path(name)
            runtime.abort=threading.Event(); runtime.children={}
            runtime.settings={'HandshakeExecution':{'DumperExit':{'BodyProfiles':[]}}}
            runtime.device=lambda:dict(time=time.time(),cars={})
            write(runtime.root/'navigation.agvmap.json',{'resources':[{'id':'bay','capacity':1}]})
            observer=Observer(runtime,runtime.root)
            with patch('scenarios.query', side_effect=lambda root, sql: [] if 'dispatch_task' in sql else [{'ResourceId':'bay','TaskId':'first'},{'ResourceId':'bay','TaskId':'second'}]), self.assertRaises(RuntimeError): observer.sample({})
            self.assertEqual(observer.violations[0]['kind'],'capacity')

    def test_readonly_observer_detects_one_vehicle_holding_two_unfinished_tasks(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = object.__new__(Runtime); runtime.root=Path(name)
            runtime.abort=threading.Event(); runtime.children={}
            runtime.settings={'HandshakeExecution':{'DumperExit':{'BodyProfiles':[]}}}
            runtime.device=lambda:dict(time=time.time(),cars={})
            write(runtime.root/'navigation.agvmap.json',{'resources':[]})
            observer=Observer(runtime,runtime.root)
            rows=[dict(VehicleId='car',TaskId='first'),dict(VehicleId='car',TaskId='second')]
            with patch('scenarios.query',side_effect=lambda root,sql: rows if 'dispatch_task' in sql else []), self.assertRaises(RuntimeError):
                observer.sample({})
            self.assertEqual(observer.violations[0]['kind'],'vehicle-multiple-tasks')


if __name__ == '__main__': unittest.main()
