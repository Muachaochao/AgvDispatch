"""Offline contracts for the one-shot charger handshake packet."""
import importlib.util
import json
from pathlib import Path
import unittest
from unittest import mock


_spec = importlib.util.spec_from_file_location(
    "charger_auto_handshake_under_test",
    Path(__file__).with_name("charger_auto_handshake.py"),
)
handshake = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(handshake)


class PacketTests(unittest.TestCase):
    def test_header_and_json_payload_are_exact_and_bounded(self):
        payload = {
            "input": {
                "is_auto": True,
                "charging_types": "ProtocolCharging",
                "max_charge_voltage": 58.8,
                "max_charge_current": 22.0,
            },
            "status": {
                "battery_percent": 0.54,
                "battery_temp": 24.0,
                "battery_current": -0.83,
                "battery_voltage": 53.22,
            },
        }
        packet = handshake.request_packet(payload)
        magic, version, sequence, length, api, reserved = handshake.HEADER.unpack(packet[:16])
        self.assertEqual((0x5A, 1, 1, 1001), (magic, version, sequence, api))
        self.assertEqual(b"\0" * 6, reserved)
        self.assertEqual(length, len(packet) - handshake.HEADER.size)
        self.assertEqual(payload, json.loads(packet[handshake.HEADER.size:]))

    def test_receive_exact_reads_fragmented_frame(self):
        class Fragmented:
            def __init__(self):
                self.parts = [b"ab", b"c", b"def"]

            def settimeout(self, value):
                pass

            def recv(self, count):
                return self.parts.pop(0)

        with mock.patch.object(handshake.time, "monotonic", return_value=10.0):
            self.assertEqual(b"abcdef", handshake.receive_exact(Fragmented(), 6, 20.0))

    def test_payload_uses_observed_conservative_limits(self):
        battery = {
            "battery_level": 0.54, "battery_temp": 24.0,
            "current": -0.83, "voltage": 53.22,
        }
        payload = handshake.build_payload(battery, 58.8, 22.0)
        self.assertIs(True, payload["input"]["is_auto"])
        self.assertEqual(58.8, payload["input"]["max_charge_voltage"])
        self.assertEqual(22.0, payload["input"]["max_charge_current"])
        self.assertEqual(-0.83, payload["status"]["battery_current"])

    def test_bms_limits_are_required_and_cannot_be_exceeded(self):
        battery = {"max_charge_voltage": 54.95, "max_charge_current": 20.0}
        self.assertEqual((54.95, 20.0), handshake.effective_limits(battery))
        with self.assertRaises(handshake.Refused):
            handshake.effective_limits(battery, 58.8, 20.0)
        with self.assertRaises(handshake.Refused):
            handshake.effective_limits(battery, 54.9, 22.0)

    def test_missing_bms_limits_are_refused(self):
        with self.assertRaises(handshake.Refused):
            handshake.effective_limits(
                {"max_charge_voltage": -0.0, "max_charge_current": -0.0})


if __name__ == "__main__":
    unittest.main()
