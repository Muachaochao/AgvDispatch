import copy
import json
import math
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import dock_pose_runner as runner


class DockPoseRunnerTests(unittest.TestCase):
    def profile(self):
        return {
            "schema": "agv-configured-dock-pose-v1",
            "vehicleId": "AGV-03",
            "robotName": "B17222",
            "robotIp": "192.168.98.214",
            "hardwareId": "hardware",
            "robotMap": "map",
            "robotMapMd5": "0" * 32,
            "dockPose": {"x": 1.0132, "y": 6.3668, "angleRad": 1.5221},
            "motionLimits": {
                "maximumCorrectionMeters": 0.15,
                "maximumYawRadians": 0.10,
                "maximumSpeedMetersPerSecond": 0.01,
                "maximumRotationSpeedRadiansPerSecond": 0.08,
                "reachDistanceMeters": 0.01,
                "reachAngleRadians": 0.03,
            },
        }

    def test_profile_is_configuration_driven_and_bounded(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "dock.json"
            path.write_text(json.dumps(self.profile()), encoding="utf-8")
            profile = runner.load_profile(path, "hardware", "192.168.98.214")
        runner.validate_profile(profile)
        chain = runner.build_chain("task", profile["dockPose"], profile["motionLimits"])
        args = chain["move_task_list"][0]["script_args"]
        self.assertEqual(1.0132, args["x"])
        self.assertEqual(6.3668, args["y"])
        self.assertEqual("world", args["coordinate"])
        self.assertEqual(0.01, args["maxSpeed"])
        self.assertEqual(0, args["backMode"])

    def test_reverse_route_requires_station_bindings(self):
        value = self.profile()
        value["driveMode"] = "reverse"
        value["motionLimits"]["maximumCorrectionMeters"] = 3.0
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "route.json"
            path.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "expectedStartStation"):
                runner.load_profile(path, "hardware", "192.168.98.214")

    def test_reverse_route_sets_back_mode_and_allows_long_bounded_transit(self):
        value = self.profile()
        value["driveMode"] = "reverse"
        value["expectedStartStation"] = "LM64"
        value["expectedEndStation"] = "LM57"
        value["dockPose"] = {"x": 0.938, "y": 2.631, "angleRad": 1.59}
        value["motionLimits"].update({
            "maximumCorrectionMeters": 1.75,
            "maximumSpeedMetersPerSecond": 0.05,
        })
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "route.json"
            path.write_text(json.dumps(value), encoding="utf-8")
            profile = runner.load_profile(path, "hardware", "192.168.98.214")
        runner.validate_profile(profile)
        chain = runner.build_chain(
            "task", profile["dockPose"], profile["motionLimits"],
            profile["driveMode"])
        args = chain["move_task_list"][0]["script_args"]
        self.assertEqual(1, args["backMode"])
        self.assertEqual(0.05, args["maxSpeed"])

    def test_reverse_route_timeout_parser_allows_a_long_transit(self):
        common = [
            "dock_pose_runner.py",
            "--robot", "192.168.98.214",
            "--expected-id", "hardware",
            "--profile", "profile.json",
            "--evidence-dir", "evidence",
        ]
        with patch.object(sys, "argv", common + ["--timeout-seconds", "120"]):
            self.assertEqual(120.0, runner.parse_args().timeout_seconds)
        with patch.object(sys, "argv", common + ["--timeout-seconds", "181"]):
            with self.assertRaises(SystemExit):
                runner.parse_args()

    def test_reverse_route_rejects_unbounded_distance(self):
        value = self.profile()
        value["driveMode"] = "reverse"
        value["expectedStartStation"] = "LM64"
        value["expectedEndStation"] = "LM57"
        value["motionLimits"]["maximumCorrectionMeters"] = 4.01
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "route.json"
            path.write_text(json.dumps(value), encoding="utf-8")
            profile = runner.load_profile(path, "hardware", "192.168.98.214")
        with self.assertRaisesRegex(ValueError, "maximumCorrectionMeters"):
            runner.validate_profile(profile)

    def test_reverse_route_accepts_station_mark_with_small_map_offset(self):
        class MemoryEvidence:
            def write_json(self, name, value):
                pass

        class Clock:
            now = 0.0

            def monotonic(self):
                return self.now

            def sleep(self, seconds):
                self.now += seconds

        class Client:
            evidence = MemoryEvidence()

            def request(self, api, port, payload, **kwargs):
                if api == 1110:
                    return {"task_status_package": {"task_status_list": [
                        {"task_id": "task", "status": 4}]}}
                self.assert_api = api
                return {
                    "x": 0.9242, "y": -0.478, "angle": 1.5548,
                    "current_station": "LM44", "is_stop": True,
                    "chassisIsStop": True, "vx": 0.0, "vy": 0.0, "w": 0.0,
                }

        clock = Clock()
        with patch.object(runner, "time", clock):
            outcome, status, state = runner.track(
                Client(), "task", {"x": 0.938, "y": -0.513, "angle": 1.599},
                {
                    "reachDistanceMeters": 0.01,
                    "reachAngleRadians": 0.05,
                },
                2.0, "reverse", "LM44")
        self.assertEqual("reverse_transit_completed", outcome)
        self.assertEqual(4, status)
        self.assertEqual("LM44", state["current_station"])

    def test_completed_task_waits_for_stop_flags_to_settle(self):
        class MemoryEvidence:
            def write_json(self, name, value):
                pass

        class Clock:
            now = 0.0

            def monotonic(self):
                return self.now

            def sleep(self, seconds):
                self.now += seconds

        class Client:
            evidence = MemoryEvidence()
            reads = 0

            def request(self, api, port, payload, **kwargs):
                if api == 1110:
                    return {"task_status_package": {"task_status_list": [
                        {"task_id": "task", "status": 4}]}}
                self.reads += 1
                return {
                    "x": 0.9242,
                    "y": -0.478,
                    "angle": 1.5548,
                    "current_station": "LM44",
                    "is_stop": self.reads > 1,
                    "chassisIsStop": True,
                    "vx": 0.0,
                    "vy": 0.0,
                    "w": 0.0,
                }

        clock = Clock()
        with patch.object(runner, "time", clock):
            outcome, status, state = runner.track(
                Client(), "task", {"x": 0.938, "y": -0.513, "angle": 1.599},
                {
                    "reachDistanceMeters": 0.01,
                    "reachAngleRadians": 0.05,
                },
                2.0, "reverse", "LM44")
        self.assertEqual("reverse_transit_completed", outcome)
        self.assertEqual(4, status)
        self.assertTrue(state["is_stop"])

    def loaded_fixture(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "dock.json"
            path.write_text(json.dumps(self.profile()), encoding="utf-8")
            return runner.load_profile(path, "hardware", "192.168.98.214")

    def test_correction_rejects_excessive_distance(self):
        profile = self.loaded_fixture()
        with self.assertRaisesRegex(runner.Refused, "more than"):
            runner.correction(
                {"x": 0.0, "y": 0.0, "angle": 1.5221},
                profile["dockPose"],
                profile["motionLimits"])

    def test_correction_rejects_excessive_yaw(self):
        profile = self.loaded_fixture()
        with self.assertRaisesRegex(runner.Refused, "angle"):
            runner.correction(
                {"x": 1.0132, "y": 6.3668, "angle": 1.0},
                profile["dockPose"],
                profile["motionLimits"])

    def test_profile_identity_mismatch_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "dock.json"
            path.write_text(json.dumps(self.profile()), encoding="utf-8")
            with self.assertRaisesRegex(runner.Refused, "hardware ID"):
                runner.load_profile(path, "different", "192.168.98.214")


if __name__ == "__main__":
    unittest.main()
