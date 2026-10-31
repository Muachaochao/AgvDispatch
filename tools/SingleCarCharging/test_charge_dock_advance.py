"""Offline contracts for the bounded final charging-dock advance."""
import copy
import importlib.util
import json
import math
from pathlib import Path
import sys
import types
import unittest
from enum import IntEnum
from unittest import mock


class MoveStatus(IntEnum):
    NONE = 0
    RUNNING = 1
    FINISHED = 3
    FAILED = 4
    SUSPENDED = 5


class BasicModule:
    def __init__(self):
        self.status = MoveStatus.NONE


_rbk = types.ModuleType("rbk")
_rbk.BasicModule = BasicModule
_rbk.MoveStatus = MoveStatus
_spec = importlib.util.spec_from_file_location(
    "charge_dock_advance_under_test",
    Path(__file__).with_name("charge_dock_advance.py"),
)
advance = importlib.util.module_from_spec(_spec)
with mock.patch.dict(sys.modules, {"rbk": _rbk}):
    _spec.loader.exec_module(advance)


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now

    def advance(self, duration=0.1):
        self.now += duration


class FakeRobot:
    def __init__(self, pose=None, reached_after=2):
        self.pose = {"x": 1.015, "y": 6.174, "angle": 1.526} if pose is None else pose
        self.reached_after = reached_after
        self.path_calls = 0
        self.calls = []
        self.report = None

    def loc(self):
        self.calls.append(("loc",))
        return copy.deepcopy(self.pose)

    def resetPath(self):
        self.calls.append(("resetPath",))

    def setPathReachDist(self, value):
        self.calls.append(("setPathReachDist", value))

    def setPathReachAngle(self, value):
        self.calls.append(("setPathReachAngle", value))

    def setPathUseOdo(self, value):
        self.calls.append(("setPathUseOdo", value))

    def setPathBackMode(self, value):
        self.calls.append(("setPathBackMode", value))

    def setPathMaxSpeed(self, value):
        self.calls.append(("setPathMaxSpeed", value))

    def setPathMaxRot(self, value):
        self.calls.append(("setPathMaxRot", value))

    def setPathOnRobot(self, x, y, angle):
        self.calls.append(("setPathOnRobot", x, y, angle))

    def goPath(self, param):
        self.calls.append(("goPath", param))
        self.path_calls += 1
        if self.path_calls >= self.reached_after:
            self.pose = {"x": 1.021, "y": 6.274, "angle": 1.526}

    def isPathReached(self):
        self.calls.append(("isPathReached",))
        return self.path_calls >= self.reached_after

    def stopRobot(self, emergency):
        self.calls.append(("stopRobot", emergency))

    def setInfo(self, text):
        self.report = json.loads(text)

    def logInfo(self, text):
        self.calls.append(("logInfo",))

    def count(self, name):
        return sum(call[0] == name for call in self.calls)


class AdvanceTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.patch = mock.patch.object(advance.time, "monotonic", self.clock)
        self.patch.start()

    def tearDown(self):
        self.patch.stop()

    def args(self, **updates):
        result = {
            "run_id": "advance-test",
            "distance_m": 0.10,
            "max_speed_mps": 0.01,
            "expected_x": 1.015,
            "expected_y": 6.174,
            "expected_angle": 1.526,
        }
        result.update(updates)
        return result

    def drive(self, robot, args):
        module = advance.Module(robot, args)
        for _ in range(60):
            module.run(robot, args)
            if module.terminal:
                return module
            self.clock.advance()
        self.fail("advance did not terminate")

    def test_moves_once_then_stops_without_charging(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args())
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertEqual(1, robot.count("setPathOnRobot"))
        self.assertEqual(("setPathMaxSpeed", 0.01),
                         next(call for call in robot.calls if call[0] == "setPathMaxSpeed"))
        self.assertIn(("stopRobot", False), robot.calls)
        self.assertNotIn(("stopRobot", True), robot.calls)
        self.assertIs(False, robot.report["charging_commanded"])
        self.assertFalse(any(call[0] in {"setDO", "battery"} for call in robot.calls))

    def test_distance_above_fifteen_centimetres_is_refused(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(distance_m=0.16))
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn("distance_m", robot.report["error"])
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_reach_tolerance_must_be_at_most_one_centimetre(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(reach_dist_m=0.011))
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn("reach_dist_m", robot.report["error"])
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_fine_advance_uses_one_millimetre_reach_tolerance(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(distance_m=0.01, reach_dist_m=0.001))
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertIn(("setPathReachDist", 0.001), robot.calls)

    def test_two_millimetre_advance_is_bounded(self):
        robot = FakeRobot()
        module = self.drive(
            robot, self.args(distance_m=0.002, reach_dist_m=0.001, max_speed_mps=0.002))
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertIn(("setPathMaxSpeed", 0.002), robot.calls)

    def test_pure_lateral_alignment_moves_left_without_forward_motion(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(distance_m=0.0, lateral_m=0.005))
        self.assertEqual(MoveStatus.FINISHED, module.status)
        call = next(call for call in robot.calls if call[0] == "setPathOnRobot")
        self.assertEqual((0.0, 0.0), tuple(call[1]))
        self.assertEqual((0.0, 0.005), tuple(call[2]))

    def test_reverse_releases_contact_using_back_mode(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(distance_m=-0.01))
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertIn(("setPathBackMode", True), robot.calls)

    def test_long_retreat_to_recognition_zone_is_bounded(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(distance_m=-0.25, max_speed_mps=0.02))
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertIn(("setPathBackMode", True), robot.calls)

    def test_combined_path_carries_lateral_and_heading_correction(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(
            distance_m=0.104, lateral_m=0.013, yaw_delta_rad=0.029))
        self.assertEqual(MoveStatus.FINISHED, module.status)
        call = next(call for call in robot.calls if call[0] == "setPathOnRobot")
        self.assertAlmostEqual(1.526 + 0.029, call[3])

    def test_unexpected_pose_is_refused_before_motion(self):
        robot = FakeRobot()
        module = self.drive(robot, self.args(expected_x=0.9))
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn("expected start pose", robot.report["error"])
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_timeout_uses_service_stop(self):
        robot = FakeRobot(reached_after=1000)
        self.clock.now = 100.0
        module = advance.Module(robot, self.args(timeout_s=10.0))
        robot.reached_after = 1000
        for _ in range(120):
            module.run(robot, self.args(timeout_s=10.0))
            if module.terminal:
                break
            self.clock.advance(0.1)
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn(("stopRobot", False), robot.calls)


if __name__ == "__main__":
    unittest.main()
