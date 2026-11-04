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
            mapDefinition=dict(name='custom', nodes=nodes, edges=edges, resources=[
                dict(id='R-DUMP', capacity=1, boundary=[dict(x=2,y=0),dict(x=3,y=0),dict(x=3,y=1)])]))

    def test_custom_map_vehicles_stations_and_resource_are_validated(self):
        runtime = object.__new__(Runtime)
        value = runtime._validate_configuration(self.custom_payload())
        self.assertEqual([s['stationId'] for s in value['stations']], ['CNC-001','CNC-002'])
        self.assertEqual([v['id'] for v in value['vehicles']], ['CAR-1','CAR-2'])
        self.assertEqual(value['dumper']['resourceId'], 'R-DUMP')
        broken = self.custom_payload(); broken['vehicles'][0]['initialSite'] = 'MISSING'
        with self.assertRaises(ValueError): runtime._validate_configuration(broken)
        disconnected = self.custom_payload(); disconnected['mapDefinition']['edges'] = disconnected['mapDefinition']['edges'][:-1]
        with self.assertRaisesRegex(ValueError, '不连通'): runtime._validate_configuration(disconnected)

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
        with self.assertRaises(ValueError):
            validate_scenario(dict(name='plc-recovery', rounds=1, plcFault='unknown'))

    def test_scenario_rejects_nonfinite_and_invalid_inputs(self):
        for value in ({'rounds': True}, {'rounds': 101}, {'timeoutSeconds': math.nan}, {'intervalSeconds': math.inf}, {'name':'arbitrary-process'}, {'name':'service-restart','rounds':2}, {'script':'bad'}):
            with self.subTest(value=value), self.assertRaises(ValueError): validate_scenario(value)
        self.assertEqual(validate_scenario({'name':'simultaneous-calls','rounds':100})['rounds'], 100)

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
        with patch('controller.read',return_value={}): self.assertFalse(runtime.status()['fresh'])

    def test_readonly_observer_detects_overcapacity(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = object.__new__(Runtime); runtime.root=Path(name)
            runtime.settings={'HandshakeExecution':{'DumperExit':{'BodyProfiles':[]}}}
            runtime.device=lambda:dict(time=time.time(),cars={})
            write(runtime.root/'navigation.agvmap.json',{'resources':[{'id':'bay','capacity':1}]})
            observer=Observer(runtime,runtime.root)
            with patch('scenarios.query',return_value=[{'ResourceId':'bay','TaskId':'first'},{'ResourceId':'bay','TaskId':'second'}]), self.assertRaises(RuntimeError): observer.sample({})
            self.assertEqual(observer.violations[0]['kind'],'capacity')


if __name__ == '__main__': unittest.main()
