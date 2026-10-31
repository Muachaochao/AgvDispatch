"""Offline transport and interlock regressions; all socket connections are mocked."""
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "probe_runner_under_test", Path(__file__).with_name("probe_runner.py"))
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


def response_for(request, body=b'{"ret_code":0}', **overrides):
    magic, version, sequence, _, api, _ = runner.HEADER.unpack(request[:runner.HEADER.size])
    # This matches the site's observed use of reserved bytes to echo the request API.
    reserved = struct.pack("!H", api) + b"\0" * 4
    fields = {"magic": magic, "version": version, "sequence": sequence,
              "length": len(body), "response_api": api + 10000, "reserved": reserved}
    fields.update(overrides)
    return runner.HEADER.pack(*fields.values()) + body


class FragmentedSocket:
    def __init__(self, make_response=response_for, fragment_size=3):
        self.make_response = make_response
        self.fragment_size = fragment_size
        self.sent = []
        self.reply = b""
        self.recv_calls = 0

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def settimeout(self, value):
        if value <= 0:
            raise AssertionError("an unbounded or expired socket timeout was used")

    def sendall(self, packet):
        self.sent.append(packet)
        self.reply = self.make_response(packet)

    def recv(self, count):
        self.recv_calls += 1
        size = min(count, self.fragment_size)
        part, self.reply = self.reply[:size], self.reply[size:]
        return part


class StateClient:
    def __init__(self, state, hardware_id="expected-hardware", task_response=None):
        self.state, self.hardware_id = state, hardware_id
        self.task_response = task_response
        self.calls = []

    def request(self, api, port, payload):
        self.calls.append((api, port, payload))
        if api == 1000:
            return {"id": self.hardware_id}
        if api == 1100:
            return self.state.copy()
        if api == 1110:
            if isinstance(self.task_response, Exception):
                raise self.task_response
            if self.task_response is not None:
                return self.task_response
        raise AssertionError("unexpected API " + str(api))


class ProbeRunnerTests(unittest.TestCase):
    def setUp(self):
        # Even an unintended new path in the runner cannot reach a real network.
        self.connection_patch = patch.object(
            runner.socket, "create_connection", side_effect=AssertionError("network is forbidden in tests"))
        self.connection = self.connection_patch.start()
        self.addCleanup(self.connection_patch.stop)
        self.idle = dict(task_status=4, script_status=0, unfinished_path=[], task_id="",
                         is_stop=True, charging=False, manual_charge=False, emergency=False,
                         current_lock={"locked": False}, dispatch_mode=0)

    def use_socket(self, stream):
        self.connection.side_effect = None
        self.connection.return_value = stream
        return runner.Robokit("192.0.2.1", runner.Evidence(None, "offline"))

    def test_fragmented_response_with_nonzero_reserved_bytes_is_valid(self):
        stream = FragmentedSocket(lambda request: response_for(
            request, b'{"ret_code":0,"id":"robot"}'), fragment_size=1)
        client = self.use_socket(stream)
        result = client.request(1000, runner.STATUS_PORT, {})
        self.assertEqual(result["id"], "robot")
        self.assertGreater(stream.recv_calls, runner.HEADER.size)
        self.assertEqual(self.connection.call_count, 1)
        header = response_for(stream.sent[0])[:runner.HEADER.size]
        self.assertEqual(runner.HEADER.unpack(header)[-1][:2], b"\x03\xe8")

    def test_wrong_sequence_is_rejected_and_its_header_is_preserved(self):
        def wrong_sequence(request):
            seq = runner.HEADER.unpack(request[:runner.HEADER.size])[2]
            return response_for(request, sequence=(seq + 1) & 0xFFFF)

        stream = FragmentedSocket(wrong_sequence)
        self.use_socket(stream)
        with tempfile.TemporaryDirectory() as directory:
            evidence = runner.Evidence(Path(directory), "offline")
            client = runner.Robokit("192.0.2.1", evidence)
            with self.assertRaisesRegex(ValueError, "sequence"):
                client.request(1000, runner.STATUS_PORT, {})
            saved = (evidence.directory / "001_1000.response_header.bin").read_bytes()
            self.assertEqual(saved, wrong_sequence(stream.sent[0])[:runner.HEADER.size])
            self.assertFalse((evidence.directory / "001_1000.response.bin").exists())
        self.assertEqual(self.connection.call_count, 1)

    def test_other_invalid_headers_and_oversize_are_rejected(self):
        for invalid in ({"magic": 0}, {"version": 2}, {"response_api": 11001},
                        {"length": runner.MAX_RESPONSE_BYTES + 1}):
            with self.subTest(invalid=invalid):
                stream = FragmentedSocket(lambda request: response_for(request, **invalid))
                client = self.use_socket(stream)
                with self.assertRaises(ValueError):
                    client.request(1000, runner.STATUS_PORT, {})
                self.assertEqual(len(stream.sent), 1)

    def test_mutation_unknown_has_one_connection_and_no_retry(self):
        stream = FragmentedSocket(lambda request: response_for(
            request, b'{"ret_code":0}', length=100))  # Body ends before its declared length.
        client = self.use_socket(stream)
        with self.assertRaisesRegex(runner.ResultUnknown, "no resend"):
            client.request(3066, runner.NAV_PORT, {"move_task_list": []}, mutation=True)
        self.assertEqual(self.connection.call_count, 1)
        self.assertEqual(len(stream.sent), 1)
        self.assertEqual(runner.HEADER.unpack(stream.sent[0][:16])[4], 3066)

    def test_exact_mutation_intent_exists_before_connecting(self):
        stream = FragmentedSocket()
        with tempfile.TemporaryDirectory() as directory:
            evidence = runner.Evidence(Path(directory), "offline")

            def connect_only_after_intent(*args, **kwargs):
                packet = (evidence.directory / "001_4021.request.bin").read_bytes()
                intent = json.loads((evidence.directory / "001_4021.intent.json").read_bytes())
                self.assertTrue(intent["mutation"])
                self.assertEqual(runner.HEADER.unpack(packet[:16])[4], 4021)
                return stream

            self.connection.side_effect = connect_only_after_intent
            client = runner.Robokit("192.0.2.1", evidence)
            payload = {"type": "module", "relative_path": "unit_test.py", "script": "# test\n"}
            self.assertEqual(client.request(4021, runner.CONFIG_PORT, payload, mutation=True)["ret_code"], 0)
            self.assertEqual((evidence.directory / "001_4021.request.bin").read_bytes(), stream.sent[0])
        self.assertEqual(self.connection.call_count, 1)

    def test_failed_evidence_write_prevents_mutation_connection(self):
        with patch.object(runner.Evidence, "begin", side_effect=OSError("disk full")):
            client = runner.Robokit("192.0.2.1", runner.Evidence(None, "offline"))
            with self.assertRaisesRegex(OSError, "disk full"):
                client.request(3066, runner.NAV_PORT, {}, mutation=True)
        self.connection.assert_not_called()

    def test_idle_preflight_uses_fresh_identity_and_state_requests(self):
        client = StateClient(self.idle)
        result = runner.preflight(client, "expected-hardware")
        self.assertEqual(result["runtime"]["task_status"], 4)
        self.assertEqual([call[0] for call in client.calls], [1000, 1100])

    def test_hardware_mismatch_rejects_before_any_other_call(self):
        client = StateClient(self.idle, hardware_id="different-hardware")
        with self.assertRaisesRegex(runner.Refused, "hardware ID mismatch"):
            runner.preflight(client, "expected-hardware")
        self.assertEqual([call[0] for call in client.calls], [1000])

    def test_active_or_missing_critical_state_refuses_preflight(self):
        active = (("task_status", 2), ("script_status", 1), ("task_id", "another-job"),
                  ("unfinished_path", ["LM2"]), ("is_stop", False), ("charging", True),
                  ("manual_charge", True), ("emergency", True), ("task_status", False),
                  ("current_lock", {"locked": True}), ("current_lock", {}),
                  ("current_lock", None), ("dispatch_mode", 1),
                  ("dispatch_mode", None), ("dispatch_mode", False))
        for key, value in active:
            with self.subTest(key=key, value=value):
                state = dict(self.idle, **{key: value})
                with self.assertRaises(runner.Refused):
                    runner.preflight(StateClient(state), "expected-hardware")
        for key in ("charging", "task_id", "unfinished_path", "script_status", "is_stop",
                    "current_lock", "dispatch_mode"):
            with self.subTest(missing=key):
                state = self.idle.copy()
                del state[key]
                with self.assertRaises(runner.Refused):
                    runner.preflight(StateClient(state), "expected-hardware")
        self.connection.assert_not_called()

    def test_report_must_match_marker_and_current_run(self):
        current = {"probe": runner.PROBE_MARKER, "run_id": "current-run", "phase": "completed"}
        self.assertEqual(runner.matching_report(json.dumps(current), "current-run"), current)
        self.assertEqual(runner.matching_report(current, "current-run"), current)
        for value in (dict(current, run_id="previous-run"), dict(current, probe="another-probe"),
                      "not JSON", json.dumps(json.dumps(current)), None):
            with self.subTest(value=value):
                self.assertIsNone(runner.matching_report(value, "current-run"))

    def test_report_channels_prefer_actual_info_and_ignore_stale_results(self):
        primary = {"probe": runner.PROBE_MARKER, "run_id": "this-run", "phase": "failed"}
        secondary = dict(primary, phase="completed")
        state = {"move_status_info": json.dumps(primary), "script_res": json.dumps(secondary)}
        self.assertEqual(runner.report_from_runtime(state, "this-run"), ("move_status_info", primary))
        state["move_status_info"] = json.dumps(dict(primary, run_id="old-run"))
        self.assertEqual(runner.report_from_runtime(state, "this-run"), ("script_res", secondary))
        state["script_res"] = ""
        self.assertEqual(runner.report_from_runtime(state, "this-run"), (None, None))
        state["move_status_info"] = json.dumps(dict(primary, probe="other-script"))
        self.assertEqual(runner.report_from_runtime(state, "this-run"), (None, None))

    def test_tracker_correlates_info_report_and_preserves_both_raw_channels(self):
        report = {"probe": runner.PROBE_MARKER, "run_id": "this-run", "phase": "completed",
                  "status": 3, "error": None, "motion_commanded": False,
                  "charger_commanded": False, "recognition_file": runner.RECOGNITION_FILE,
                  "samples": [{}, {}, {}]}
        raw_info = json.dumps(report)
        raw_script_res = "unparseable SDK result"
        written = {}

        class TrackerClient:
            class evidence:
                @staticmethod
                def write_json(name, data):
                    written[name] = data

            @staticmethod
            def request(api, port, payload, **kwargs):
                if api == 1110:
                    return {"task_status_package": {"task_status_list": [{"task_id": "our-task", "status": 4}]}}
                if api == 1100:
                    return {"move_status_info": raw_info, "script_res": raw_script_res, "script_status": 0}
                raise AssertionError("unexpected API")

        result = runner.track_probe(TrackerClient(), "our-task", "this-run")
        self.assertEqual(result["outcome"], "recognition_probe_completed")
        self.assertEqual(result["report_source"], "move_status_info")
        self.assertFalse(result["charging_test_passed"])
        observed = written["tracking_script_res.json"][0]
        self.assertEqual(observed["move_status_info"], raw_info)
        self.assertEqual(observed["script_res"], raw_script_res)
        self.assertEqual(observed["matched_report_source"], "move_status_info")

    def test_previous_own_terminal_probe_requires_fresh_same_status(self):
        for task_id in ("charge-recognition-previous-run",
                        "charge-approach-previous-run",
                        "charge-advance-previous-run"):
            for status in (4, 5, 6, 7):
                with self.subTest(task_id=task_id, status=status):
                    state = dict(self.idle, task_status=status, task_id=task_id)
                    response = {"task_status_package": {"task_status_list": [{"task_id": task_id, "status": status}]}}
                    client = StateClient(state, task_response=response)
                    result = runner.preflight(client, "expected-hardware")
                    self.assertEqual(result["confirmed_prior_probe"], {"task_id": task_id, "status": status})
                    self.assertEqual([call[0] for call in client.calls], [1000, 1100, 1110])
                    self.assertEqual(client.calls[-1][2], {"task_ids": [task_id]})

    def test_business_failed_active_or_unidentified_probe_is_never_admitted(self):
        cases = ((5, "business-job"), (6, ""), (7, "charge-recognition-"),
                 (2, "charge-recognition-active"), (0, "charge-recognition-old"),
                 (None, "charge-recognition-unknown"))
        for status, task_id in cases:
            with self.subTest(status=status, task_id=task_id):
                client = StateClient(dict(self.idle, task_status=status, task_id=task_id))
                with self.assertRaises(runner.Refused):
                    runner.preflight(client, "expected-hardware")
                self.assertEqual([call[0] for call in client.calls], [1000, 1100])

    def test_previous_probe_missing_ambiguous_or_unconfirmed_status_is_rejected(self):
        task_id = "charge-recognition-previous-run"
        state = dict(self.idle, task_status=5, task_id=task_id)
        rows_cases = ([], [{"task_id": "different", "status": 5}],
                      [{"task_id": task_id, "status": 2}], [{"task_id": task_id, "status": 404}],
                      [{"task_id": task_id, "status": 6}], [{"task_id": task_id, "status": False}],
                      [{"task_id": task_id, "status": 5}] * 2)
        for rows in rows_cases:
            with self.subTest(rows=rows):
                client = StateClient(state, task_response={"task_status_package": {"task_status_list": rows}})
                with self.assertRaisesRegex(runner.Refused, "not confirmed"):
                    runner.preflight(client, "expected-hardware")
        with self.assertRaisesRegex(runner.Refused, "not confirmed"):
            runner.preflight(StateClient(state, task_response=TimeoutError("no fresh reply")), "expected-hardware")

    def test_terminal_own_probe_does_not_bypass_other_interlocks(self):
        task_id = "charge-recognition-previous-run"
        for key, value in (("current_lock", {"locked": True}), ("dispatch_mode", 1),
                           ("script_status", 1), ("unfinished_path", ["LM2"]),
                           ("is_stop", False), ("charging", True), ("errors", ["fault"])):
            with self.subTest(key=key):
                state = dict(self.idle, task_status=5, task_id=task_id, **{key: value})
                client = StateClient(state)
                with self.assertRaises(runner.Refused):
                    runner.preflight(client, "expected-hardware")
                self.assertEqual([call[0] for call in client.calls], [1000, 1100])


if __name__ == "__main__":
    unittest.main()
