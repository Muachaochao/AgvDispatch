"""Regression for the virtual P-side scan; it never opens a device socket."""
import json
import tempfile
import unittest
from pathlib import Path
from device_twin import DeviceTwin
from simulation_clock import CLOCK


class Plc28TwinTests(unittest.TestCase):
    def make_twin(self, contract='plc-28-stage-v2'):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        (root / 'service-settings.json').write_text(json.dumps({'HandshakeExecution': {'PlcContractVersion': contract}}), encoding='utf-8')
        (root / 'navigation.agvmap.json').write_text(json.dumps({'nodes': [
            {'id': '检修', 'agvSiteId': 'LM1', 'x': 0, 'y': 0},
            {'id': '倒料', 'agvSiteId': 'LM2', 'x': 4, 'y': 0}], 'edges': []}), encoding='utf-8')
        return DeviceTwin(root)

    def test_current_cycle_holds_then_closes_and_allows_next_permit(self):
        twin = self.make_twin()
        self.assertFalse(twin.bit(610, 2))
        twin.setbit(0, 5, True)
        twin.setbit(0, 6, True)
        twin.setbit(0, 1, False)
        twin.setbit(610, 0, True)
        twin.scan_handshakes()
        self.assertTrue(twin.bit(610, 2))
        twin.setbit(610, 0, False)
        twin.scan_handshakes()
        self.assertFalse(twin.bit(610, 2))
        self.assertTrue(twin.dumper_reserved)
        twin.station = 'LM2'
        twin.x, twin.y = twin.nodes['LM2']['x'], twin.nodes['LM2']['y']
        twin.setbit(610, 1, True)
        twin.scan_handshakes()
        twin.dump_started -= 6
        twin.scan_handshakes()
        self.assertTrue(twin.bit(610, 3))
        twin.scan_handshakes()
        self.assertTrue(twin.bit(610, 1))
        self.assertTrue(twin.bit(0, 5))
        self.assertTrue(twin.bit(0, 6))
        twin.setbit(610, 1, False)  # Only H's external verified exit changes this bit.
        twin.scan_handshakes()
        self.assertFalse(twin.bit(610, 3))
        self.assertFalse(twin.bit(610, 2))
        self.assertFalse(twin.dumper_reserved)
        twin.setbit(610, 0, True)
        twin.scan_handshakes()
        self.assertTrue(twin.bit(610, 2))
        self.assertTrue(twin.bit(0, 5))
        self.assertTrue(twin.bit(0, 6))

    def test_unknown_contract_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'Unknown'):
            self.make_twin('unknown')

    def test_old_isolated_contract_keeps_old_initial_ready(self):
        self.assertTrue(self.make_twin('sender-owned-return-confirmed-v1').bit(610, 2))

    def test_call_response_does_not_clear_held_removed_notification(self):
        twin = self.make_twin()
        twin.setbit(0, 0, True)
        twin.setbit(0, 4, True)
        twin.setbit(0, 5, True)
        twin.setbit(0, 6, True)
        twin.scan_handshakes()
        self.assertFalse(twin.bit(0, 0))
        self.assertTrue(twin.bit(0, 4))
        self.assertTrue(twin.bit(0, 5))
        self.assertTrue(twin.bit(0, 6))

    def test_vehicle_following_is_not_an_external_obstacle_timeout(self):
        twin = self.make_twin()
        twin.tasks['TASK'] = 2
        twin.current = 'TASK'
        twin.blocked_by_vehicle = 'AGV-OTHER'
        twin.navigation_timeout_after_blocked_seconds = 5
        twin.navigation_blocked_since = 1.0

        twin.apply_navigation_timeout()

        self.assertEqual(2, twin.tasks['TASK'])
        self.assertFalse(twin.navigation_timeout_tripped)
        self.assertIsNone(twin.navigation_blocked_since)
        self.assertFalse(twin.robokit_reply(1100, {})['blocked'])
        self.assertTrue(twin.blocked_now())

        twin.obstacle = True
        # The twin measures blocking on the isolated business clock, which starts near zero in a
        # fresh process; place the start of blocking clearly beyond the 5 s timeout on that clock.
        twin.navigation_blocked_since = CLOCK.monotonic() - 10
        twin.apply_navigation_timeout()
        self.assertEqual(7, twin.tasks['TASK'])
        self.assertTrue(twin.navigation_timeout_tripped)

    def test_battery_and_relay_api_share_the_same_charging_state(self):
        twin = self.make_twin()
        twin.battery_percent = .245
        before = twin.robokit_reply(1007, {'simple': False})
        self.assertEqual(before['battery_level'], .245)
        self.assertFalse(before['charging'])
        self.assertEqual(before['current'], 0)
        self.assertEqual(before['battery_temp'], 30)
        self.assertFalse(before['manual_charge'])
        self.assertFalse(before['auto_charge'])

        response = twin.robokit_reply(6005, {'status': True})
        self.assertEqual(response, {'ret_code': 0})
        during = twin.robokit_reply(1007, {})
        self.assertTrue(during['charging'])
        self.assertEqual(during['current'], 5)
        self.assertTrue(during['auto_charge'])

        self.assertEqual(twin.robokit_reply(6005, {'status': False}), {'ret_code': 0})
        after = twin.robokit_reply(1007, {})
        self.assertFalse(after['charging'])
        self.assertEqual(after['current'], 0)

    def test_relay_api_rejects_ambiguous_status(self):
        twin = self.make_twin()
        for request in ({}, {'status': 1}, {'status': 'true'}, {'status': None}):
            response = twin.robokit_reply(6005, request)
            self.assertEqual(response['ret_code'], 400)
        self.assertFalse(twin.charging)


if __name__ == '__main__':
    unittest.main()
