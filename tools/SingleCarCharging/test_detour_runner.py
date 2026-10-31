"""Offline detour regressions. No robot, charger, or backend connection is permitted."""
import argparse
import copy
import datetime as dt
import json
from pathlib import Path
import socket
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent))
import detour_runner as runner


def now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def args():
    return argparse.Namespace(robot="192.0.2.3", vehicle_id="AGV-03", expected_id="hardware-03",
                              expected_map="test-map", expected_md5="a"*32, route=["A", "B", "C"],
                              task_prefix="REPEAT", max_speed=0.3, max_wspeed=0.2,
                              max_acc=0.5, max_wacc=0.5, poll_seconds=0.1,
                              track_timeout=1.0, blocked_timeout=0.2, backend="http://127.0.0.1:5199",
                              isolated_route_confirmed=True, parked_clearance_m=None)


def fixture():
    positions = {"A": (0, 0), "B": (1, 0), "C": (2, 0), "D": (0, 3)}
    xy = lambda site: dict(zip(("x", "y"), positions[site]))
    pairs = [("A", "B"), ("B", "C"), ("C", "D")]
    robot_map = {"header": {"mapName": "test-map", "mapType": "2D-Map", "version": "1.0.0"},
                 "advancedPointList": [{"instanceName": site, "pos": xy(site)} for site in positions],
                 "advancedCurveList": [{"className": "StraightPath", "startPos": {"instanceName": a},
                                         "endPos": {"instanceName": b}} for a, b in pairs]}
    context = {"snapshot": {"connected": True, "receivedAt": now(), "status": {"agvs": [
                               {"vehicleId": "AGV-03", "connection": "Online", "quality": "Good",
                                "lastSuccessfulPollAt": now(), "mapPositionState": "Normal"}]},
                            "map": {"id": "MAP", "version": "V1", "definitionHash": "HASH", "unit": "m",
                                    "coordinateSystem": "RobotWorld", "warnings": [],
                                    "nodes": [{"id": "BUS-"+site, "agvSiteId": site, "enabled": True, **xy(site)}
                                              for site in positions],
                                    "edges": [{"id": "E"+str(i), "from": "BUS-"+a, "to": "BUS-"+b,
                                               "enabled": True, "bidirectional": False, "kind": "Line",
                                               "points": [xy(a), xy(b)]} for i, (a, b) in enumerate(pairs)]}},
               "closures": {"ready": True, "identityConflict": False, "revision": 1, "mapId": "MAP",
                            "versionId": "V1", "definitionHash": "HASH", "blockedTraversals": [], "closures": []},
               "configuration": {"revision": "CONFIG1", "activeVehicles": [
                   {"vehicleId": "AGV-03", "ipAddress": "192.0.2.3", "enabled": True}]}}
    state = {"task_status": 0, "task_id": "", "current_station": "A", "current_map": "test-map",
             "current_map_md5": "a"*32, "script_status": 0, "unfinished_path": [], "is_stop": True,
             "chassisIsStop": True, "charging": False, "manual_charge": False, "emergency": False,
             "soft_emc": False, "blocked": False, "fatals": [], "errors": [], "current_lock": {"locked": False},
             "dispatch_mode": 0, "confidence": 0.9, "x": 0, "y": 0, "angle": 0,
             "vx": 0, "vy": 0, "w": 0}
    return context, robot_map, state


class MemoryEvidence:
    def __init__(self):
        self.items = {}

    def write_json(self, name, value):
        if name in self.items:
            raise AssertionError("evidence filename reused: " + name)
        self.items[name] = copy.deepcopy(value)

    def replace_json(self, name, value):
        self.items[name] = copy.deepcopy(value)


class Backend:
    def __init__(self, context):
        self.context, self.calls = context, 0
        self.after_first = None

    def read(self):
        self.calls += 1
        if self.calls > 1 and self.after_first:
            return self.after_first()
        return copy.deepcopy(self.context)


class Client:
    def __init__(self, robot_map, state):
        self.robot_map, self.state = robot_map, state
        self.evidence, self.calls, self.task_ids = MemoryEvidence(), [], []
        self.mutation_error = None
        self.tracking_error = None
        self.rows = None
        self.final_edit = None

    def request(self, api, port, payload, mutation=False, **kwargs):
        self.calls.append((api, port, payload, mutation))
        if api == 1000:
            return {"id": "hardware-03"}
        if api == 1300:
            return {"current_map": "test-map", "current_map_md5": "a"*32}
        if api == 1022:
            return {"loadmap_status": 1}
        if api == 4011:
            return copy.deepcopy(self.robot_map)
        if api == 1303:
            return {"list": [payload["source_id"], payload["target_id"]]}
        if api == 3066:
            self.task_ids = [move["task_id"] for move in payload["move_task_list"]]
            if self.mutation_error:
                raise self.mutation_error
            return {"ret_code": 0}
        if api == 1100:
            result = copy.deepcopy(self.state)
            if self.task_ids:
                if self.tracking_error:
                    raise self.tracking_error
                result.update(task_id=self.task_ids[-1], task_status=4, current_station="C")
                if self.final_edit:
                    result.update(self.final_edit)
            return result
        if api == 1110:
            if self.rows:
                rows = self.rows(payload["task_ids"])
            else:
                rows = [{"task_id": task_id, "status": 4} for task_id in payload["task_ids"]]
            return {"task_status_package": {"task_status_list": rows}}
        raise AssertionError("unexpected API " + str(api))


class Clock:
    def __init__(self):
        self.now = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class DetourTests(unittest.TestCase):
    def setUp(self):
        self.net = patch.object(socket, "create_connection", side_effect=AssertionError("real network forbidden"))
        self.net.start()
        self.addCleanup(self.net.stop)
        self.context, self.robot_map, self.state = fixture()
        self.args = args()
        self.client = Client(self.robot_map, self.state)
        self.backend = Backend(self.context)
        self.clock = Clock()

    def run_detour(self):
        with patch.object(runner.time, "monotonic", self.clock.monotonic), patch.object(runner.time, "sleep", self.clock.sleep):
            return runner.run_detour(self.client, self.args, "offline", self.backend)

    def validate(self):
        return runner.validate_context(self.context, self.args, self.robot_map)

    def assert_no_send(self):
        self.assertFalse(any(call[3] for call in self.client.calls))

    def test_repeated_labels_still_generate_globally_unique_original_ids(self):
        first, second = runner.segment_ids("REPEAT", 2), runner.segment_ids("REPEAT", 2)
        self.assertEqual(len(set(first+second)), 4)
        self.assertTrue(all(value.startswith("REPEAT-") for value in first+second))
        self.assertTrue(all(len(value) <= 64 for value in first+second))

    def test_bad_route_and_prefix_are_refused(self):
        for route in ("A", "A,B,A", ",,"):
            with self.assertRaises(argparse.ArgumentTypeError):
                runner.route_value(route)
        for prefix in ("x"*33, "x y", "../escape"):
            with self.assertRaises(runner.Refused):
                runner.segment_ids(prefix, 2)

    def test_local_backend_does_not_allow_external_or_credential_urls(self):
        for url in ("https://example.com", "http://evil/", "http://a@127.0.0.1:5199",
                    "http://127.0.0.1:5199/x", "http://127.0.0.1:5199/#x"):
            with self.assertRaises(argparse.ArgumentTypeError):
                runner.backend_url(url)

    def test_topology_includes_coordinates_and_full_directed_roads(self):
        self.validate()
        self.robot_map["advancedCurveList"][0]["startPos"]["instanceName"] = "B"
        self.robot_map["advancedCurveList"][0]["endPos"]["instanceName"] = "A"
        with self.assertRaisesRegex(runner.Refused, "directed roads"):
            self.validate()

    def test_matching_selected_edges_cannot_hide_an_extra_remote_road(self):
        self.robot_map["advancedCurveList"].append({"className": "StraightPath", "startPos": {"instanceName": "D"},
                                                  "endPos": {"instanceName": "A"}})
        with self.assertRaisesRegex(runner.Refused, "road counts"):
            self.validate()

    def test_map_coordinate_mismatch_is_refused(self):
        self.robot_map["advancedPointList"][0]["pos"]["x"] += 0.001
        with self.assertRaisesRegex(runner.Refused, "coordinates differ"):
            self.validate()

    def test_curve_geometry_cannot_be_replaced_by_same_endpoints(self):
        curve = self.robot_map["advancedCurveList"][0]
        curve.update(className="BezierPath", controlPos1={"x": 0, "y": 1}, controlPos2={"x": 1, "y": 1})
        with self.assertRaisesRegex(runner.Refused, "geometry differ"):
            self.validate()

    def test_curve_sampler_preserves_arc_direction_and_bezier_bulge(self):
        nodes = {"A": (1, 0), "B": (-1, 0)}
        curve = {"className": "ArcPath", "startPos": {"instanceName": "A"}, "endPos": {"instanceName": "B"},
                 "controlPos1": {"x": 0, "y": 1}}
        row = runner.curve_points(curve, nodes)
        self.assertEqual(row[:3], ("A", "B", "CircularArc"))
        self.assertAlmostEqual(row[3][16][0], 0)
        self.assertAlmostEqual(row[3][16][1], 1)
        curve.update(className="BezierPath", controlPos1={"x": 1, "y": 1}, controlPos2={"x": -1, "y": 1})
        self.assertAlmostEqual(runner.curve_points(curve, nodes)[3][16][1], 0.75)

    def test_malformed_or_unsupported_map_is_refused(self):
        for edit in (lambda: self.robot_map.update(advancedPointList=[]),
                     lambda: self.robot_map["advancedCurveList"][0].update(className="UnknownPath"),
                     lambda: self.context["snapshot"]["map"]["nodes"][0].update(enabled=False)):
            self.context, self.robot_map, self.state = fixture()
            edit()
            with self.assertRaises(runner.Refused):
                self.validate()

    def test_missing_stale_disconnected_or_conflicting_backend_refuses(self):
        for key, value in (("connected", False), ("receivedAt", "2000-01-01T00:00:00Z")):
            with self.subTest(key=key):
                self.context["snapshot"][key] = value
                with self.assertRaises(runner.Refused):
                    self.validate()
                self.context = fixture()[0]
        self.context["closures"]["definitionHash"] = "OTHER"
        with self.assertRaises(runner.Refused):
            self.validate()

    def test_blocked_directed_edge_is_refused(self):
        self.context["closures"]["blockedTraversals"] = [{"edgeId": "E0", "fromNodeId": "BUS-A", "toNodeId": "BUS-B"}]
        with self.assertRaisesRegex(runner.Refused, "active road closure"):
            self.validate()

    def test_active_closure_is_checked_even_if_computed_traversal_missing(self):
        self.context["closures"]["closures"] = [{"active": True, "mapId": "MAP", "versionId": "V1",
                                                   "definitionHash": "HASH", "fromNodeId": "BUS-A", "toNodeId": "BUS-B",
                                                   "bidirectional": False}]
        with self.assertRaisesRegex(runner.Refused, "active road closure"):
            self.validate()

    def add_other_vehicle(self, **changes):
        self.context["configuration"]["activeVehicles"].append({"vehicleId": "AGV-01", "enabled": True,
                                                                 "ipAddress": "192.0.2.1"})
        state = {"vehicleId": "AGV-01", "connection": "Online", "quality": "Good", "lastSuccessfulPollAt": now(),
                 "mapPositionState": "Normal", "currentStation": "D", "velocityX": 0, "velocityY": 0,
                 "angularVelocity": 0, "taskCount": 0, "x": 0, "y": 3}
        state.update(changes)
        self.context["snapshot"]["status"]["agvs"].append(state)

    def test_another_car_needs_fresh_stationary_occupancy_and_calibrated_clearance(self):
        self.add_other_vehicle()
        with self.assertRaisesRegex(runner.Refused, "parked-clearance"):
            self.validate()
        self.args.parked_clearance_m = 1.1
        self.validate()
        self.context["snapshot"]["status"]["agvs"][-1]["lastSuccessfulPollAt"] = "2000-01-01T00:00:00Z"
        with self.assertRaisesRegex(runner.Refused, "unverified occupancy"):
            self.validate()

    def test_other_car_on_station_chain_is_refused(self):
        self.add_other_vehicle(currentStation="B")
        self.args.parked_clearance_m = 1.1
        with self.assertRaisesRegex(runner.Refused, "occupies"):
            self.validate()

    def test_car_stale_station_label_cannot_hide_xy_blocking_route(self):
        self.add_other_vehicle(x=0.5, y=0)
        self.args.parked_clearance_m = 1.1
        with self.assertRaisesRegex(runner.Refused, "within.*clearance"):
            self.validate()

    def test_paused_or_unknown_other_vehicle_is_not_assumed_clear(self):
        self.add_other_vehicle(taskCount=1)
        self.args.parked_clearance_m = 1.1
        with self.assertRaisesRegex(runner.Refused, "moving or.*unverified"):
            self.validate()

    def test_wrong_own_vehicle_ip_or_inactive_configuration_is_refused(self):
        self.context["configuration"]["activeVehicles"][0]["ipAddress"] = "192.0.2.4"
        with self.assertRaisesRegex(runner.Refused, "selected vehicle/IP"):
            self.validate()

    def test_own_disconnected_or_stale_backend_vehicle_is_refused(self):
        self.context["snapshot"]["status"]["agvs"][0]["connection"] = "Interrupted"
        with self.assertRaisesRegex(runner.Refused, "selected vehicle has no fresh"):
            self.validate()

    def test_curve_occupancy_padding_covers_more_than_sampled_chords(self):
        nodes = {"A": (0, 0), "B": (1, 0)}
        curve = {"className": "BezierPath", "startPos": {"instanceName": "A"}, "endPos": {"instanceName": "B"},
                 "controlPos1": {"x": 0, "y": 1}, "controlPos2": {"x": 1, "y": 1}}
        self.assertAlmostEqual(runner.curve_cover_padding(curve, nodes), 3/64)

    def test_isolation_acknowledgement_is_required_before_any_requests(self):
        self.args.isolated_route_confirmed = False
        with self.assertRaisesRegex(runner.Refused, "isolated route"):
            self.run_detour()
        self.assertEqual(self.client.calls, [])

    def test_contradictory_stop_and_missing_emergency_confidence_are_refused(self):
        for edit in ({"vx": 0.02}, {"vx": None}, {"soft_emc": None}, {"confidence": 0.49},
                     {"confidence": 1.01}, {"errors": None}, {"script_status": False}, {"dispatch_mode": False}):
            self.client.state = {**self.state, **edit}
            with self.assertRaises(runner.Refused):
                self.run_detour()
            self.assert_no_send()

    def test_changed_closure_revision_between_checks_prevents_send(self):
        def changed():
            result = copy.deepcopy(self.context)
            result["closures"]["revision"] += 1
            return result
        self.backend.after_first = changed
        with self.assertRaisesRegex(runner.Refused, "changed before send"):
            self.run_detour()
        self.assert_no_send()

    def test_success_requires_fresh_whole_chain_and_matching_stopped_runtime(self):
        result = self.run_detour()
        self.assertEqual(result["outcome"], "detour_completed")
        self.assertEqual(sum(call[0] == 3066 for call in self.client.calls), 1)
        self.assertFalse(result["traffic_reservation"])

    def test_duplicate_or_boolean_task_rows_are_rejected(self):
        for rows in ([{"task_id": "same", "status": 4}]*2, [{"task_id": "x", "status": True}]):
            with self.assertRaises(runner.Refused):
                runner.task_rows({"task_status_list": rows})

    def test_partial_task_reply_does_not_reuse_completed_rows_from_old_poll(self):
        self.client.rows = lambda ids: [{"task_id": ids[-1], "status": 4}]
        result = self.run_detour()
        self.assertEqual(result["outcome"], "unknown")
        self.assertIn("every original segment", result["error"])
        self.assertEqual(sum(call[0] == 3066 for call in self.client.calls), 1)

    def test_lost_ack_queries_original_ids_without_resending_or_cancelling(self):
        self.client.mutation_error = runner.ResultUnknown("lost acknowledgement")
        result = self.run_detour()
        self.assertEqual(result["outcome"], "unknown")
        self.assertEqual(result["original_task_query"]["segment_ids"], self.client.task_ids)
        self.assertEqual(sum(call[3] for call in self.client.calls), 1)
        self.assertFalse(any(call[0] == 3003 for call in self.client.calls))

    def test_explicit_rejection_preserves_ids_and_is_not_retried(self):
        self.client.mutation_error = runner.Refused("explicit rejection")
        result = self.run_detour()
        self.assertEqual(result["outcome"], "detour_rejected")
        self.assertEqual(result["segment_ids"], self.client.task_ids)
        self.assertEqual(sum(call[3] for call in self.client.calls), 1)

    def test_disk_error_after_acceptance_does_not_lose_original_task_ids(self):
        self.client.evidence.replace_json = lambda *args: (_ for _ in ()).throw(OSError("disk full"))
        result = self.run_detour()
        self.assertEqual(result["outcome"], "unknown")
        self.assertEqual(result["segment_ids"], self.client.task_ids)
        self.assertIn("original_task_query", result)
        self.assertEqual(sum(call[3] for call in self.client.calls), 1)

    def test_previous_terminal_confirmation_must_not_include_another_pending_task(self):
        self.client.state.update(task_id="previous-task", task_status=4)
        self.client.rows = lambda ids: [{"task_id": ids[0], "status": 4}, {"task_id": "other", "status": 1}]
        with self.assertRaisesRegex(runner.Refused, "prior terminal"):
            self.run_detour()
        self.assert_no_send()

    def test_unknown_backend_evidence_does_not_open_a_mutating_path(self):
        self.backend.after_first = lambda: (_ for _ in ()).throw(ConnectionError("backend offline"))
        with self.assertRaisesRegex(ConnectionError, "backend offline"):
            self.run_detour()
        self.assert_no_send()

    def test_tracking_disconnect_preserves_original_chain(self):
        self.client.tracking_error = ConnectionError("offline")
        result = self.run_detour()
        self.assertEqual(result["outcome"], "unknown")
        self.assertEqual(result["segment_ids"], self.client.task_ids)
        self.assertIn("runtime_query_error", result["original_task_query"])

    def test_completed_rows_with_different_runtime_task_do_not_pass(self):
        self.client.final_edit = {"task_id": "someone-else"}
        result = self.run_detour()
        self.assertEqual(result["outcome"], "tracking_timeout")
        self.assertIn("original_task_query", result)

    def test_changed_map_or_new_vehicle_error_never_reports_success(self):
        for edit in ({"current_map_md5": "b"*32}, {"soft_emc": True}, {"errors": [{"code": 12345}]}):
            self.client = Client(self.robot_map, self.state)
            self.client.final_edit = edit
            result = self.run_detour()
            self.assertEqual(result["outcome"], "attention_required")

    def test_blocked_task_is_left_addressable_without_resend(self):
        self.client.rows = lambda ids: [{"task_id": key, "status": 2} for key in ids]
        self.client.final_edit = {"blocked": True, "task_status": 2}
        result = self.run_detour()
        self.assertEqual(result["outcome"], "blocked_waiting")
        self.assertEqual(sum(call[0] == 3066 for call in self.client.calls), 1)
        self.assertEqual(result["segment_ids"], self.client.task_ids)


if __name__ == "__main__":
    unittest.main()
