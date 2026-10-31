"""Offline contracts for the single-attempt charging relay runner."""
import importlib.util
import copy
from pathlib import Path
import unittest


_spec = importlib.util.spec_from_file_location(
    "charger_relay_once_under_test",
    Path(__file__).with_name("charger_relay_once.py"),
)
relay = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(relay)


def observation(second, current=2.0, charging=True, enabled=True):
    return {
        "host_utc": "2026-10-31T13:00:%02d+00:00" % second,
        "do7": enabled,
        "state": {"is_stop": True, "chassisIsStop": True,
                  "charging": charging, "manual_charge": False,
                  "blocked": False, "emergency": False, "errors": [], "fatals": []},
        "battery": {"current": current, "charging": charging, "manual_charge": False},
    }


class RelayTests(unittest.TestCase):
    def test_success_requires_explicit_integer_zero(self):
        self.assertTrue(relay.successful_relay_response({"ret_code": 0}))
        self.assertFalse(relay.successful_relay_response({"ret_code": "0"}))
        self.assertFalse(relay.successful_relay_response({}))

    def test_summary_selects_do7_and_battery_current(self):
        state = {
            "DO": [{"id": 6, "status": False}, {"id": 7, "status": True}],
            "blocked": False, "emergency": False, "errors": [], "fatals": [],
        }
        battery = {
            "battery_level": 0.54, "battery_temp": 24.0, "charging": True,
            "manual_charge": False, "auto_charge": False,
            "current": 22.2, "voltage": 56.6,
        }
        result = relay.summary(state, battery)
        self.assertIs(True, result["do7"])
        self.assertEqual(22.2, result["battery"]["current"])

    def test_targets_the_documented_do_api_and_output(self):
        self.assertEqual(6001, relay.RELAY_API)
        self.assertEqual(7, relay.RELAY_DO_ID)

    def test_two_distinct_fresh_charging_readings_and_final_check_confirm(self):
        self.assertEqual(("charging_current_confirmed", True),
                         relay.relay_observation_result(
                             [observation(1), observation(2)], observation(3), True))

    def test_single_positive_spike_followed_by_discharge_never_passes(self):
        samples = [observation(1), observation(2, -0.84, False)]
        self.assertFalse(relay.relay_observation_result(samples, observation(3, -0.84, False), True)[1])

    def test_positive_current_without_charging_flags_never_passes(self):
        self.assertFalse(relay.relay_observation_result(
            [observation(1, charging=False), observation(2, charging=False)],
            observation(3, charging=False), True)[1])

    def test_boolean_infinite_nan_or_missing_current_never_passes(self):
        for value in (True, float("inf"), float("nan"), None, "2.0"):
            with self.subTest(value=value):
                self.assertFalse(relay.relay_observation_result(
                    [observation(1, value), observation(2, value)], observation(3, value), True)[1])

    def test_final_current_loss_invalidates_previous_confirmation(self):
        self.assertFalse(relay.relay_observation_result(
            [observation(1), observation(2)], observation(3, -0.84, False), True)[1])

    def test_second_sample_safety_error_is_not_skipped(self):
        failed = observation(2)
        failed["state"]["errors"] = [{"code": 5214}]
        self.assertEqual(("relay_attention_required", False),
                         relay.relay_observation_result([observation(1), failed], observation(3), True))

    def test_final_safety_error_or_manual_charger_requires_attention(self):
        for field, value in (("emergency", True), ("manual_charge", True), ("blocked", True)):
            final = observation(3)
            final["state"][field] = value
            self.assertEqual(("relay_attention_required", False),
                             relay.relay_observation_result([observation(1), observation(2)], final, True))

    def test_stale_duplicate_or_reversed_samples_are_not_two_fresh_observations(self):
        for samples, final in (([observation(1), observation(10)], observation(11)),
                               ([observation(1), observation(1)], observation(2)),
                               ([observation(2), observation(1)], observation(3)),
                               ([observation(1), observation(2)], observation(10))):
            self.assertFalse(relay.relay_observation_result(samples, final, True)[1])

    def test_relay_or_manual_state_contradictions_never_pass(self):
        for changed in ("do7", "battery_manual", "state_charging"):
            final = copy.deepcopy(observation(3))
            if changed == "do7":
                final["do7"] = False
            elif changed == "battery_manual":
                final["battery"]["manual_charge"] = True
            else:
                final["state"]["charging"] = False
            self.assertFalse(relay.relay_observation_result([observation(1), observation(2)], final, True)[1])

    def test_stop_requires_relay_off_false_charging_and_two_consistent_reads(self):
        stopped = observation(1, -0.84, False, False)
        final = observation(2, -0.84, False, False)
        self.assertEqual(("relay_off_current_stopped", False),
                         relay.relay_observation_result([stopped], final, False))
        self.assertEqual(("relay_off_current_not_confirmed", False),
                         relay.relay_observation_result([stopped], observation(2), False))


if __name__ == "__main__":
    unittest.main()
