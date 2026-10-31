"""Offline contracts for the single-attempt charging relay runner."""
import importlib.util
from pathlib import Path
import unittest


_spec = importlib.util.spec_from_file_location(
    "charger_relay_once_under_test",
    Path(__file__).with_name("charger_relay_once.py"),
)
relay = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(relay)


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


if __name__ == "__main__":
    unittest.main()
