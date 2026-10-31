"""Offline failure-injection tests; no robot/charger sockets are opened."""
import copy
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import charger_session_runner as session


def snapshot(enabled=False):
    state = {"DO": [{"id": 7, "status": enabled}],
             "is_stop": True, "chassisIsStop": True,
             "vx": 0.0, "vy": 0.0, "w": 0.0,
             "charging": enabled, "manual_charge": False,
             "blocked": False, "emergency": False,
             "warnings": [], "errors": [], "fatals": []}
    battery = {"charging": enabled, "manual_charge": False,
               "current": 19.0 if enabled else -0.8, "voltage": 54.0,
               "battery_temp": 24.0, "battery_level": 0.7,
               "max_charge_voltage": 58.4, "max_charge_current": 20.0}
    return state, battery


def station_status(auto=True, current=20.0):
    return {"ret_code": 0, "alarms": {"errors": []},
            "status": {"is_auto": auto, "is_emc": False, "is_manual": False},
            "output": {"current": current}}


class Clock:
    def __init__(self):
        self.now = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class Evidence:
    directory = Path("memory-only")

    def __init__(self):
        self.records = {}

    def write_json(self, name, value):
        self.records[name] = copy.deepcopy(value)

    def write(self, name, value):
        self.records[name] = value


class Stream:
    def __init__(self, name):
        self.name = name
        self.closed = False

    def close(self):
        self.closed = True


class Client:
    def __init__(self):
        self.enabled = False
        self.calls = []
        self.unknown_enable = False
        self.off_fails = False

    def request(self, api, port, payload, **kwargs):
        self.calls.append((api, dict(payload)))
        if api == 1000:
            return {"id": "test-hardware"}
        if api != session.RELAY_API:
            raise AssertionError("unexpected live API")
        if payload["status"]:
            self.enabled = True
            if self.unknown_enable:
                raise session.ResultUnknown("enable response lost")
        elif self.off_fails:
            raise session.ResultUnknown("off response lost")
        else:
            self.enabled = False
        return {"ret_code": 0}


def args():
    return SimpleNamespace(expected_id="test-hardware", execute=True,
                           charger="192.0.2.11", duration_seconds=2.0,
                           heartbeat_seconds=0.5, poll_seconds=0.25,
                           auto_wait_seconds=1.0, confirm_samples=3,
                           minimum_positive_seconds=1.0, max_temperature=45,
                           max_voltage=None, max_current=None,
                           allow_configured_limits=False)


class HelpersTests(unittest.TestCase):
    def test_stop_flag_cannot_hide_measured_motion_or_unknown_velocity(self):
        for value in (0.1, None, float("nan"), True):
            state, battery = snapshot()
            state["vx"] = value
            self.assertFalse(session.safe_sample(state, battery, 45))

    def test_measured_stop_fallback_is_recorded_and_keeps_chassis_requirement(self):
        state, battery = snapshot()
        state["is_stop"] = False
        self.assertTrue(session.safe_sample(state, battery, 45))
        summary = session.sample_summary(state, battery)
        self.assertEqual(0.0, summary["vx"])
        self.assertFalse(summary["is_stop"])
        state["chassisIsStop"] = False
        self.assertFalse(session.safe_sample(state, battery, 45))

    def test_missing_errors_or_duplicate_do7_are_unknown(self):
        state, battery = snapshot(True)
        del state["errors"]
        self.assertFalse(session.safe_sample(state, battery, 45))
        state, battery = snapshot(True)
        state["DO"].append({"id": 7, "status": True})
        self.assertFalse(session.positive_charge(state, battery))

    def test_soft_emergency_must_be_explicitly_false_when_supplied(self):
        for value in (True, None, 0, "false"):
            state, battery = snapshot()
            state["soft_emc"] = value
            self.assertFalse(session.safe_sample(state, battery, 45))
            self.assertEqual(value, session.sample_summary(state, battery)["soft_emc"])
        state, battery = snapshot()
        state["soft_emc"] = False
        self.assertTrue(session.safe_sample(state, battery, 45))

    def test_two_vehicle_charging_flags_required(self):
        state, battery = snapshot(True)
        for field in ("state", "battery"):
            a, b = copy.deepcopy(state), copy.deepcopy(battery)
            (a if field == "state" else b)["charging"] = False
            self.assertFalse(session.positive_charge(a, b))

    def test_historical_minute_does_not_replace_final_continuity(self):
        interval = session.PositiveInterval()
        for second in range(62):
            self.assertTrue(interval.observe(float(second), True))
        self.assertEqual(61, interval.seconds)
        interval.observe(62.0, False)
        for second in range(63, 66):
            interval.observe(float(second), True)
        self.assertEqual(2, interval.seconds)
        self.assertEqual(61, interval.longest)
        self.assertEqual(3, interval.count)

    def test_sampling_gap_duplicate_and_regressed_times_break_continuity(self):
        for later in (0.0, -1.0, 6.0):
            interval = session.PositiveInterval()
            interval.observe(0.0, True)
            self.assertFalse(interval.observe(later, True))
            self.assertEqual(0, interval.seconds)
            self.assertEqual(0, interval.count)

    def test_warning_timestamp_and_counter_change_is_same_warning(self):
        a = {"warnings": [{"code": 1, "desc": "same", "timestamp": "old", "count": 2}]}
        b = {"warnings": [{"code": 1, "desc": "same", "timestamp": "new", "count": 3}]}
        self.assertEqual(session.warning_signature(a), session.warning_signature(b))
        b["warnings"][0]["code"] = 2
        self.assertNotEqual(session.warning_signature(a), session.warning_signature(b))

    def test_ack_and_alarm_shapes_fail_closed(self):
        for response in ({"ret_code": False}, {"ret_code": "0"}, {},
                         {"ret_code": 0, "ret": False},
                         {"ret_code": 0, "alarms": []},
                         {"ret_code": 0, "alarms": {"warnings": [{}]}}):
            self.assertIsNotNone(session.station_problem(response))
        self.assertIsNotNone(session.station_problem({"ret_code": 0}, require_status=True))

    def test_stop_ack_without_output_zero_is_not_shutdown_proof(self):
        self.assertFalse(session.station_output_stopped(station_status(True, 20)))
        self.assertFalse(session.station_output_stopped(station_status(False, 20)))
        self.assertFalse(session.station_output_stopped({"ret_code": 0}))
        self.assertTrue(session.station_output_stopped(station_status(False, 0)))

    def test_freshness_compares_source_progress_not_host_clock_offset(self):
        freshness = session.Freshness()
        freshness.check("station", {"create_on": "2026-08-13T00:00:00+08:00"})
        freshness.check("station", {"create_on": "2026-08-13T00:00:01+08:00"})
        with self.assertRaises(session.ResultUnknown):
            freshness.check("station", {"create_on": "2026-08-13T00:00:01+08:00"})

    def test_requested_limits_are_respected_but_unknown_bms_cannot_be_overridden(self):
        _, battery = snapshot()
        config = args()
        config.max_current = 10.0
        self.assertEqual(((58.4, 10.0), "bms"), session.session_limits(battery, config))
        config.max_current = 21.0
        with self.assertRaises(session.Refused):
            session.session_limits(battery, config)
        config.allow_configured_limits = True
        config.max_current = 10.0
        battery["max_charge_voltage"] = -0.0
        with self.assertRaises(session.Refused):
            session.session_limits(battery, config)

    def test_electrical_feedback_cannot_exceed_limit_or_be_unknown(self):
        _, battery = snapshot(True)
        session.ensure_electrical_limits(battery, (58.4, 20.0))
        for field, value in (("voltage", 58.5), ("current", 20.1),
                             ("current", None), ("voltage", float("nan"))):
            changed = {**battery, field: value}
            with self.subTest(field=field, value=value), self.assertRaises(
                    (session.Refused, session.ResultUnknown)):
                session.ensure_electrical_limits(changed, (58.4, 20.0))

    def test_station_exchange_records_intent_before_send_and_matches_sequence(self):
        evidence = Evidence()
        response = json.dumps({"ret_code": 0}).encode()
        frame = session.HEADER.pack(0x5A, 1, 9, len(response), session.RESPONSE_API,
                                    b"\0" * 6) + response
        class FramedStream:
            def settimeout(self, value):
                pass
            def sendall(self, value):
                if "frame.intent.json" not in evidence.records:
                    raise AssertionError("send occurred before evidence")
            def recv(self, count):
                nonlocal frame
                result, frame = frame[:count], frame[count:]
                return result
        stream = FramedStream()
        self.assertEqual({"ret_code": 0}, session.exchange_on_open(
            stream, {"input": {"is_auto": True}}, 9, evidence, "frame"))

    def test_station_exchange_rejects_wrong_sequence_and_truncated_reply(self):
        for sequence, body in ((8, b'{"ret_code":0}'), (9, b'')):
            data = session.HEADER.pack(0x5A, 1, sequence, 14,
                                       session.RESPONSE_API, b"\0" * 6) + body
            class FramedStream:
                def settimeout(self, value):
                    pass
                def sendall(self, value):
                    pass
                def recv(self, count):
                    nonlocal data
                    result, data = data[:count], data[count:]
                    return result
            with self.subTest(sequence=sequence), self.assertRaises((ValueError, ConnectionError)):
                session.exchange_on_open(FramedStream(), {}, 9, Evidence(), "bad")
class SessionTests(unittest.TestCase):
    def run_fake(self, client=None, exchange=None, read=None):
        client = client or Client()
        clock = Clock()
        streams = (Stream("ack"), Stream("status"))
        commands = []

        def default_exchange(stream, payload, sequence, evidence, label, **kwargs):
            active = payload["input"]["is_auto"]
            commands.append((stream.name, active))
            if exchange is not None:
                return exchange(stream, active, clock, client)
            return ({"ret_code": 0, "ret": True} if stream.name == "ack"
                    else station_status(active, 20.0 if active else 0.0))

        def default_read(_):
            values = snapshot(client.enabled)
            return read(values, client, clock) if read else values

        evidence = Evidence()
        with patch.object(session, "time", clock), \
                patch.object(session, "connect_charger", return_value=streams), \
                patch.object(session, "exchange_on_open", side_effect=default_exchange), \
                patch.object(session, "battery_snapshot", side_effect=default_read):
            result = session.run_session(client, args(), "offline-run", evidence)
        self.assertTrue(all(stream.closed for stream in streams))
        return result, client, commands

    def test_interval_and_verified_shutdown_required_for_pass(self):
        result, client, commands = self.run_fake()
        self.assertEqual("charging_session_completed", result["outcome"])
        self.assertTrue(result["charging_interval_verified"])
        self.assertTrue(result["shutdown"]["verified"])
        self.assertTrue(result["charging_test_passed"])
        self.assertFalse(result["full_docking_cycle_verified"])
        self.assertEqual([True, False], [p["status"] for api, p in client.calls if api == session.RELAY_API])
        self.assertGreaterEqual(commands.count(("status", False)), 2)

    def test_old_real_shutdown_response_does_not_pass(self):
        def exchange(stream, active, clock, client):
            return {"ret_code": 0, "ret": True} if stream.name == "ack" else station_status(True, 20)
        result, _, _ = self.run_fake(exchange=exchange)
        self.assertTrue(result["charging_interval_verified"])
        self.assertFalse(result["charging_test_passed"])
        self.assertEqual("shutdown_not_confirmed", result["outcome"])

    def test_unknown_enable_still_sends_off_once_and_preserves_original_error(self):
        client = Client()
        client.unknown_enable = True
        result, client, _ = self.run_fake(client)
        self.assertEqual("unknown", result["outcome"])
        self.assertIn("enable response lost", result["error"])
        self.assertTrue(result["shutdown"]["verified"])
        self.assertEqual([True, False], [p["status"] for api, p in client.calls if api == session.RELAY_API])

    def test_start_and_cleanup_failures_both_remain_in_result(self):
        client = Client()
        client.unknown_enable = client.off_fails = True
        result, _, _ = self.run_fake(client)
        self.assertIn("enable response lost", result["error"])
        self.assertIn("off response lost", str(result["shutdown"]["errors"]))
        self.assertFalse(result["charging_test_passed"])

    def test_ack_rejection_prevents_relay_and_active_status_followup(self):
        def exchange(stream, active, clock, client):
            if stream.name == "ack":
                return {"ret_code": 0, "ret": not active}
            return station_status(False, 0)
        result, client, commands = self.run_fake(exchange=exchange)
        self.assertFalse(result["charging_test_passed"])
        self.assertNotIn(("status", True), commands)
        self.assertFalse(any(api == session.RELAY_API for api, _ in client.calls))

    def test_disconnect_preserves_observations_and_cleans_up_surviving_socket(self):
        active_status = [0]
        def exchange(stream, active, clock, client):
            if stream.name == "status" and active:
                active_status[0] += 1
                if active_status[0] > 1:
                    raise ConnectionError("status TCP closed")
            return {"ret_code": 0, "ret": True} if stream.name == "ack" else station_status(active, 20 if active else 0)
        result, client, commands = self.run_fake(exchange=exchange)
        self.assertEqual("unknown", result["outcome"])
        self.assertIn("TCP closed", result["error"])
        self.assertTrue(result["observations"])
        self.assertIn(("ack", False), commands)
        self.assertFalse(client.enabled)
        self.assertFalse(result["shutdown"]["verified"])

    def test_contradictory_vehicle_charge_signal_never_confirms(self):
        def read(values, client, clock):
            if client.enabled:
                values[0]["charging"] = False
            return values
        result, _, _ = self.run_fake(read=read)
        self.assertFalse(result["charging_interval_verified"])
        self.assertFalse(result["charging_test_passed"])

    def test_slow_vehicle_read_cannot_count_as_continuous_charge(self):
        def read(values, client, clock):
            if client.enabled:
                clock.sleep(6)
            return values
        result, _, _ = self.run_fake(read=read)
        self.assertEqual("unknown", result["outcome"])
        self.assertIn("freshness", result["error"])
        self.assertFalse(result["charging_test_passed"])

    def test_warning_during_shutdown_prevents_full_success(self):
        enabled_once = [False]
        def read(values, client, clock):
            if client.enabled:
                enabled_once[0] = True
            elif enabled_once[0]:
                values[0]["warnings"] = [{"code": 7, "desc": "new after stop"}]
            return values
        result, _, _ = self.run_fake(read=read)
        self.assertTrue(result["charging_interval_verified"])
        self.assertFalse(result["shutdown"]["verified"])
        self.assertFalse(result["charging_test_passed"])

    def test_changed_heartbeat_ack_is_not_ignored(self):
        active_acks = [0]
        def exchange(stream, active, clock, client):
            if stream.name == "ack":
                if active:
                    active_acks[0] += 1
                return {"ret_code": 0, "ret": not (active and active_acks[0] > 1)}
            return station_status(active, 20 if active else 0)
        result, _, _ = self.run_fake(exchange=exchange)
        self.assertIn("ack", result["error"])
        self.assertFalse(result["charging_test_passed"])


if __name__ == "__main__":
    unittest.main()
