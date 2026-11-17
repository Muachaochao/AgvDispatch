import copy
import unittest
from unittest.mock import Mock, patch
import threading
from controller import Runtime

class BusinessScenarioTests(unittest.TestCase):
    def fixture(self):
        config = dict(vehicles=[dict(id='AGV-01', initialSite='WAIT-01', initialLoaded=True, speedScale=3), dict(id='AGV-02')],
            stations=[dict(stationId='CNC-001'), dict(stationId='CNC-002')],
            mapDefinition=dict(nodes=[dict(id='WAIT-01', enabled=True),dict(id='BAD',enabled=False)]),
            plcSignalTable=dict(sha256='bound'), dumper=dict(dumpingSite='DUMP'), mapIdentity=dict(definitionHash='source'))
        runtime = object.__new__(Runtime)
        runtime.scheme_store = Mock()
        runtime.scheme_store.get.side_effect = lambda _: dict(id='source', configuration=copy.deepcopy(config), scenario=dict(name='dispatch-backlog',calls=[dict(stationId='CNC-002',atSeconds=0,vehicleId=None)]))
        runtime._validate_configuration = Mock(side_effect=lambda value: value)
        runtime._create_instance = Mock()
        runtime.status = Mock(return_value={})
        runtime.root = 'isolated'
        return runtime, config

    def test_single_vehicle_preserves_original_scheme_and_full_map(self):
        runtime, original = self.fixture()
        runtime.select_scheme(dict(id='source', business=dict(vehicleId='AGV-01',initialSite='WAIT-01',stationId='CNC-001')))
        actual = runtime._create_instance.call_args.args[0]
        self.assertEqual(len(actual['vehicles']), 1)
        self.assertFalse(actual['vehicles'][0]['initialLoaded'])
        self.assertEqual(actual['vehicles'][0]['speedScale'], 1)
        self.assertEqual(actual['mapDefinition'], original['mapDefinition'])
        self.assertEqual(len(original['vehicles']), 2)
        self.assertTrue(original['vehicles'][0]['initialLoaded'])

    def test_start_replaces_saved_batch_with_one_unassigned_plc_call(self):
        runtime, _ = self.fixture()
        runtime.worker = None
        runtime.abort = threading.Event()
        runtime.phase = 'running'
        runtime.start = Mock()
        runtime.run_scenario = Mock()
        class InlineThread:
            def __init__(self, target, **kwargs): self.target = target
            def start(self): self.target()
        with patch('controller.threading.Thread', InlineThread):
            runtime.select_scheme(dict(id='source',business=dict(vehicleId='AGV-01',initialSite='WAIT-01',stationId='CNC-001')), start=True)
        scenario = runtime.run_scenario.call_args.args[0]
        self.assertEqual(scenario['calls'], [dict(stationId='CNC-001', atSeconds=0, vehicleId=None)])
        self.assertEqual(scenario['timeoutSeconds'], 7200)

    def test_invalid_business_rejected_before_creating_instance(self):
        for change in [dict(vehicleId='missing'),dict(stationId='missing'),dict(initialSite='BAD'),dict(extra=True)]:
            runtime, _ = self.fixture()
            with self.assertRaises(ValueError):
                runtime.select_scheme(dict(id='source',business=dict(dict(vehicleId='AGV-01', initialSite='WAIT-01',stationId='CNC-001'), **change)))
            runtime._create_instance.assert_not_called()

if __name__ == '__main__': unittest.main()

