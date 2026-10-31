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


def reply(response=None, **fields):
    return {"response_api": 11001, "response_sequence": 1,
            "response": {"ret_code": 0, "ret": True} if response is None else response,
            **fields}


class ResponseTests(unittest.TestCase):
    def test_small_ack_is_not_proof_of_automatic_mode_or_charging(self):
        assessment = handshake.classify_exchange(reply())
        self.assertEqual("acknowledged", assessment["outcome"])
        self.assertIs(False, assessment["charging_test_passed"])
        self.assertEqual("unknown", handshake.classify_exchange(
            reply(), require_auto_status=True)["outcome"])

    def test_station_snapshot_confirms_mode_without_claiming_charging(self):
        assessment = handshake.classify_exchange(reply({
            "ret_code": 0, "status": {"is_auto": True},
            "state": 9001, "state_machine": 9876,
            "alarms": {"errors": [], "fatals": []},
        }), require_auto_status=True)
        self.assertEqual("acknowledged", assessment["outcome"])
        self.assertEqual(9001, assessment["station_state"])
        self.assertIs(False, assessment["charging_test_passed"])

    def test_incomplete_or_mismatched_response_is_unknown(self):
        records = [
            reply(read_error="response timed out"),
            reply(error="connection reset"),
            reply(response_api=1001), reply(response_sequence=2),
            reply(response_sequence=True),
            reply(response=None, response_api=11000),
            {"response_api": 11001, "response_sequence": 1, "response": None},
            reply([]), reply({}), reply({"ret_code": True}),
            reply({"ret_code": 0, "ret": "true"}),
            reply({"ret_code": 0, "alarms": {"errors": "5214"}}),
        ]
        for record in records:
            with self.subTest(record=record):
                self.assertEqual("unknown", handshake.classify_exchange(record)["outcome"])

    def test_rejected_request_does_not_require_a_success_status(self):
        for response in ({"ret_code": 10}, {"ret_code": 0, "ret": False}):
            with self.subTest(response=response):
                self.assertEqual("attention_required", handshake.classify_exchange(
                    reply(response), require_auto_status=True)["outcome"])

    def test_charger_alarms_and_explicit_protection_mode_are_attention(self):
        responses = [
            {"ret_code": 0, "alarms": {"errors": [{"code": 5214}]}},
            {"ret_code": 0, "status": {"is_auto": True}, "alarms": {
                "errors": [{"code": 5214, "desc": "AC undervoltage at charging module input"}]}},
            {"ret_code": 0, "status": {"is_auto": True},
             "alarms": {"fatals": [{"code": 1}]}},
            {"ret_code": 0, "status": {"is_auto": True, "is_emc": True}},
            {"ret_code": 0, "status": {"is_auto": True, "is_manual": True}},
            {"ret_code": 0, "status": {"is_emc": True}},
            {"ret_code": 0, "status": {"is_auto": False}},
        ]
        for response in responses:
            with self.subTest(response=response):
                assessment = handshake.classify_exchange(reply(response), require_auto_status=True)
                self.assertEqual("attention_required", assessment["outcome"])
                self.assertTrue(assessment["attention_reasons"])
                self.assertIs(False, assessment["charging_test_passed"])

    def test_unconfirmed_station_mode_is_unknown(self):
        for status in (None, [], {}, {"is_auto": "true"}, {"is_auto": 1}):
            with self.subTest(status=status):
                self.assertEqual("unknown", handshake.classify_exchange(
                    reply({"ret_code": 0, "status": status}),
                    require_auto_status=True)["outcome"])


class OneShotTests(unittest.TestCase):
    def test_first_uncertain_reply_prevents_second_port_command(self):
        for record in (reply(read_error="timeout"), reply(response_api=1001)):
            with self.subTest(record=record), mock.patch.object(
                    handshake, "exchange", return_value=record) as exchange:
                result = handshake.send_handshake_once("192.0.2.11", b"packet", object())
                self.assertEqual("unknown", result["outcome"])
                self.assertEqual(1, exchange.call_count)
                self.assertEqual(20207, exchange.call_args.args[1])
                self.assertIs(False, result["automatic_resend_attempted"])

    def test_first_rejection_prevents_second_port_command(self):
        with mock.patch.object(handshake, "exchange", return_value=reply({"ret_code": 2})) as exchange:
            result = handshake.send_handshake_once("192.0.2.11", b"packet", object())
        self.assertEqual("handshake_attention_required", result["outcome"])
        self.assertEqual(1, exchange.call_count)

    def test_second_port_auto_false_is_not_success(self):
        responses = [reply(), reply({"ret_code": 0, "status": {"is_auto": False}})]
        with mock.patch.object(handshake, "exchange", side_effect=responses) as exchange:
            result = handshake.send_handshake_once("192.0.2.11", b"packet", object())
        self.assertEqual("handshake_attention_required", result["outcome"])
        self.assertEqual([20207, 20204], [call.args[1] for call in exchange.call_args_list])

    def test_both_acknowledged_replies_still_do_not_claim_charging(self):
        responses = [reply(), reply({"ret_code": 0, "status": {"is_auto": True}})]
        with mock.patch.object(handshake, "exchange", side_effect=responses) as exchange:
            result = handshake.send_handshake_once("192.0.2.11", b"packet", object())
        self.assertEqual("handshake_acknowledged", result["outcome"])
        self.assertEqual([20207, 20204], [call.args[1] for call in exchange.call_args_list])
        self.assertTrue(all(not item["charging_test_passed"] for item in result["exchange_assessments"]))
        self.assertIn("electrical charging remain unverified", result["message"])


class MainResultTests(unittest.TestCase):
    def invoke(self, execute, send_outcome, outputs=None):
        state = {"is_stop": True, "chassisIsStop": True,
                 "manual_charge": False, "charging": False, "auto_charge": False,
                 "blocked": False, "emergency": False, "errors": [], "fatals": [],
                 "DO": [{"id": 7, "status": False}] if outputs is None else outputs}
        battery = {"charging": False, "auto_charge": False, "manual_charge": False,
                   "max_charge_voltage": 54.41, "max_charge_current": 20.0}
        argv = ["--robot", "192.0.2.66", "--expected-id", "TEST-ROBOT",
                "--evidence-dir", "offline-evidence"]
        if execute:
            argv.append("--execute")
        with mock.patch.object(handshake, "Evidence") as evidence_type, \
                mock.patch.object(handshake, "Robokit") as client_type, \
                mock.patch.object(handshake, "send_handshake_once", return_value={
                    "outcome": send_outcome}) as send, \
                mock.patch("builtins.print"):
            evidence_type.return_value.directory = Path("offline-evidence")
            client_type.return_value.request.side_effect = [
                {"id": "TEST-ROBOT"}, state, battery]
            code = handshake.main(argv)
            result = evidence_type.return_value.write_json.call_args.args[1]
            return code, result, send.call_count

    def test_unknown_or_attention_does_not_exit_success_or_start_observation(self):
        for outcome, expected_exit in (("unknown", 3), ("handshake_attention_required", 2)):
            with self.subTest(outcome=outcome):
                code, result, send_count = self.invoke(True, outcome)
                self.assertEqual(expected_exit, code)
                self.assertEqual(outcome, result["outcome"])
                self.assertIs(False, result["do7_commanded"])
                self.assertIs(False, result["charging_test_passed"])
                self.assertEqual(1, send_count)
                self.assertNotIn("observations", result)

    def test_staging_sends_no_charger_command(self):
        code, result, send_count = self.invoke(False, "unknown")
        self.assertEqual(0, code)
        self.assertEqual("handshake_staged", result["outcome"])
        self.assertEqual(0, send_count)

    def test_read_failure_after_acknowledgement_is_unknown_without_resend(self):
        # The mock has no fourth robot reply: observing after the accepted
        # station command raises StopIteration. It must not imply nothing sent.
        code, result, send_count = self.invoke(True, "handshake_acknowledged")
        self.assertEqual(3, code)
        self.assertEqual("unknown", result["outcome"])
        self.assertEqual(1, send_count)
        self.assertIs(False, result["automatic_resend_attempted"])

    def test_on_unknown_or_duplicate_do7_prevents_station_send(self):
        for outputs in ([], [{"id": 7, "status": True}],
                        [{"id": 7, "status": 0}], [{"id": 7}],
                        [{"id": 7, "status": False}, {"id": 7, "status": False}]):
            with self.subTest(outputs=outputs):
                code, result, send_count = self.invoke(True, "unknown", outputs)
                self.assertEqual(2, code)
                self.assertEqual("refused_or_failed", result["outcome"])
                self.assertIn("DO7", result["error"])
                self.assertEqual(0, send_count)


if __name__ == "__main__":
    unittest.main()
