"""Offline dispatch bridge fault tests. No robot or charger sockets are opened."""
import copy
import datetime as dt
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import dispatch_worker as worker
from probe_runner import Evidence, Robokit, Refused, ResultUnknown


def profile():
    return {
        "schema": worker.SCHEMA, "commissioned": True, "calibrationVersion": "test-v1",
        "siteId": "C", "vehicleId": "A", "hardwareId": "hardware-a", "robotIp": "192.0.2.66",
        "robotMap": "test-map", "robotMapMd5": "a" * 32, "approachSite": "LM52",
        "chargerIp": "192.0.2.11", "approachPose": {"x": 1.0, "y": 5.0, "angleRad": 1.57},
        "maximumUndockPositionErrorM": 0.05, "approachStandoffsM": [0.875, 0.65],
        "approachSpeedMps": 0.03, "maximumAxisErrorRad": 0.03, "insertionDistanceM": 0.1,
        "insertionSpeedMps": 0.01, "obstacleStopDistanceM": 0.05,
        "undockSegmentsM": [-0.3, -0.3, -0.1], "undockSpeedMps": 0.01,
    }


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.directory = self.root / "CHARGE-test-DOCK"
        self.directory.mkdir()
        self.profile_path = self.root / "profile.json"
        self.manifest = {
            "operationId": self.directory.name, "sessionId": "CHARGE-test", "kind": "Dock",
            "siteId": "C", "vehicleId": "A", "robotIp": "192.0.2.66", "approachSite": "LM52",
            "robotMap": "test-map", "robotMapMd5": "a" * 32, "profilePath": str(self.profile_path),
            "leaseTimeoutSeconds": 15, "maximumSessionSeconds": 14400,
            "maximumTemperature": 45, "targetBatteryPercent": 80,
        }
        self.write_profile(profile())
        self.operation = self.make_operation()

    def write_profile(self, value):
        self.profile_path.write_text(json.dumps(value), encoding="utf-8")
        self.manifest["profileSha256"] = hashlib.sha256(self.profile_path.read_bytes()).hexdigest()

    def make_operation(self):
        worker.atomic_json(self.directory / "manifest.json", self.manifest)
        worker.atomic_json(self.directory / "lease.json", {
            "operationId": self.manifest["operationId"], "updatedAt": worker.utc_now()})
        return worker.Operation(self.directory / "manifest.json")

    def test_calibration_and_backend_map_binding_both_required(self):
        self.assertEqual("hardware-a", worker.load_profile(self.manifest)["hardwareId"])
        for key, value in (("commissioned", False), ("vehicleId", "B"),
                           ("robotMapMd5", "b" * 32), ("approachSite", "LM55"),
                           ("obstacleStopDistanceM", 0), ("insertionDistanceM", 0.16),
                           ("undockSegmentsM", [-0.3] * 5)):
            with self.subTest(key=key):
                updated = profile(); updated[key] = value
                self.write_profile(updated)
                with self.assertRaises(Refused):
                    worker.load_profile(self.manifest)

    def test_changed_profile_after_intent_is_never_loaded(self):
        self.profile_path.write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(Refused, "changed"):
            worker.load_profile(self.manifest)

    def test_expired_lease_stops_before_any_network_operation(self):
        worker.atomic_json(self.directory / "lease.json", {
            "operationId": self.manifest["operationId"],
            "updatedAt": (dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=20)).isoformat()})
        with patch.object(Robokit, "request", side_effect=AssertionError("network must remain unused")):
            self.assertEqual(2, self.operation.run())
        result = worker.read_json(self.directory / "result.json")
        self.assertEqual("Unknown", result["state"])
        self.assertIn("lease expired", result["message"])

    def test_same_operation_cannot_be_launched_twice(self):
        (self.directory / "worker.lock").write_bytes(b"existing")
        with patch.object(Robokit, "request", side_effect=AssertionError("no replay")):
            with self.assertRaises(FileExistsError):
                self.operation.run()

    def test_lease_identity_is_not_interchangeable_between_runs(self):
        worker.atomic_json(self.directory / "lease.json", {"operationId": "other", "updatedAt": worker.utc_now()})
        with self.assertRaisesRegex(ResultUnknown, "identity"):
            self.operation.control_check()

    def test_stop_cancels_exact_original_task_once_but_never_reenables(self):
        worker.atomic_json(self.directory / "stop.json", {"operationId": self.manifest["operationId"]})
        robot = worker.GuardedRobot("192.0.2.66", Evidence(None, "fake"), self.operation, profile())
        robot.identity_checked = True
        robot.active_task = "charge-approach-exact"
        calls = []
        def request(_client, api, port, payload, **kwargs):
            calls.append((api, copy.deepcopy(payload)))
            return {"ret_code": 0}
        with patch.object(Robokit, "request", request):
            robot.request(1110, 19204, {"task_ids": [robot.active_task]})
            robot.request(1110, 19204, {"task_ids": [robot.active_task]})
            with self.assertRaises(Refused):
                robot.request(6001, 19210, {"id": 7, "status": True}, mutation=True)
            robot.request(6001, 19210, {"id": 7, "status": False}, mutation=True)
        self.assertEqual([(3003, {"smartCancel": False, "taskId": robot.active_task})],
                         [call for call in calls if call[0] == 3003])
        self.assertEqual([(6001, {"id": 7, "status": False})], [call for call in calls if call[0] == 6001])

    def test_stop_still_allows_charge_shutdown_readbacks_and_off(self):
        worker.atomic_json(self.directory / "stop.json", {"operationId": self.manifest["operationId"]})
        robot = worker.GuardedRobot("192.0.2.66", Evidence(None, "fake"), self.operation, profile())
        robot.identity_checked = True
        calls = []
        with patch.object(Robokit, "request", lambda _s, api, port, payload, **kw: calls.append(api) or {"ret_code": 0}):
            robot.request(6001, 19210, {"id": 7, "status": False}, mutation=True)
            robot.request(1100, 19204, {})
            robot.request(1007, 19204, {})
        self.assertEqual([6001, 1100, 1007], calls)

    def test_wrong_live_hardware_or_map_prevents_motion(self):
        robot = worker.GuardedRobot("192.0.2.66", Evidence(None, "fake"), self.operation, profile())
        with patch.object(Robokit, "request", return_value={"id": "hardware-a", "current_map": "test-map", "current_map_md5": "b" * 32}) as request:
            with self.assertRaises(Refused):
                robot.request(3066, 19206, {"move_task_list": []}, mutation=True)
            self.assertEqual(1, request.call_count)

    def test_undock_rejects_another_sessions_stop_evidence_before_motion(self):
        prior = self.root / "CHARGE-test-CHARGE"; prior.mkdir()
        self.operation.manifest["chargeOperationId"] = prior.name
        worker.atomic_json(prior / "manifest.json", {"sessionId": "other", "vehicleId": "A"})
        worker.atomic_json(prior / "result.json", {"operationId": prior.name, "kind": "Charge", "shutdownVerified": True})
        with patch.object(self.operation, "advance", side_effect=AssertionError("no movement")):
            with self.assertRaisesRegex(Refused, "matching"):
                self.operation.undock(profile())

    def test_approach_heading_and_station_are_checked_before_recognition_motion(self):
        state = {"x": 1.0, "y": 5.0, "angle": 0.0, "current_station": "LM52",
                 "is_stop": True, "chassisIsStop": True, "vx": 0, "vy": 0, "w": 0}
        with self.assertRaisesRegex(Refused, "heading"):
            self.operation.validate_approach_pose(profile(), state)


if __name__ == "__main__":
    unittest.main()
