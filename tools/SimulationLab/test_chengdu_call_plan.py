import unittest
from run_chengdu_admission import build_calls


class CallPlanTests(unittest.TestCase):
    def test_two_hundred_calls_preserve_round_order_and_scheduler_vehicle_choice(self):
        stations = [f'CNC-{i:03}' for i in range(1,26)]
        calls = build_calls(stations,8)
        self.assertEqual(len(calls),200)
        self.assertEqual([c['stationId'] for c in calls],stations*8)
        self.assertTrue(all(c['vehicleId'] is None and c['atSeconds']==0 for c in calls))

    def test_limit_is_checked_before_environment_mutation(self):
        for stations, cycles in [([],1),(['CNC-001'],0),(['CNC-001'],501)]:
            with self.assertRaises(ValueError): build_calls(stations,cycles)
        self.assertEqual(len(build_calls(['CNC-001'],500)),500)


if __name__=='__main__': unittest.main()
