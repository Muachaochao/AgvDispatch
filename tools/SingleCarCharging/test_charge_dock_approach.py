"""Offline contracts for the one-stage charging coarse-approach module."""
import copy
import importlib.util
import json
import math
from pathlib import Path
import sys
import types
import unittest
from collections import deque
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
    "charge_dock_approach_under_test",
    Path(__file__).with_name("charge_dock_approach.py"),
)
approach = importlib.util.module_from_spec(_spec)
with mock.patch.dict(sys.modules, {"rbk": _rbk}):
    _spec.loader.exec_module(approach)


def profile():
    return {
        "device_params": [{
            "key": "coordinate_system",
            "combo_param": {"child_key": "world"},
        }]
    }


class FakeRobot:
    def __init__(self, target=None, robot=None, reached_after=2):
        self.target = {"x": 1.0, "y": 6.0, "yaw": -math.pi / 2} \
            if target is None else target
        self.robot = {"x": 0.75, "y": 4.7, "angle": math.pi / 2} if robot is None else robot
        self.reached_after = reached_after
        self.calls = []
        self.path_calls = 0
        self.report = None
        self.goal = None
        self.statuses = deque([0, 2, 0, 2, 0, 2, 0, 2] * 3)

    def getRecFile(self, name):
        self.calls.append(("getRecFile", name))
        return profile()

    def resetRec(self):
        self.calls.append(("resetRec",))

    def getRecStatus(self):
        value = self.statuses.popleft() if self.statuses else 2
        self.calls.append(("getRecStatus", value))
        return value

    def doRecWithAngle(self, name, angle):
        self.calls.append(("doRecWithAngle", name, angle))

    def getRecResult(self):
        self.calls.append(("getRecResult",))
        return copy.deepcopy(self.target)

    def loc(self):
        self.calls.append(("loc",))
        return copy.deepcopy(self.robot)

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
        self.goal = (x, y, angle)

    def goPath(self, param):
        self.calls.append(("goPath", param))
        self.path_calls += 1
        if self.path_calls >= self.reached_after:
            self.robot = {"x": 1.0, "y": 5.125, "angle": math.pi / 2}

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


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now

    def advance(self, duration=0.1):
        self.now += duration


class ApproachTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.clock_patch = mock.patch.object(approach.time, "monotonic", self.clock)
        self.clock_patch.start()

    def tearDown(self):
        self.clock_patch.stop()

    def drive(self, robot, args=None):
        args = {"run_id": "test-run"} if args is None else args
        module = approach.Module(robot, args)
        for _ in range(100):
            module.run(robot, args)
            if module.terminal:
                return module
            self.clock.advance()
        self.fail("approach did not terminate")

    def test_moves_once_to_coarse_approach_and_never_commands_charging(self):
        robot = FakeRobot()
        module = self.drive(robot)
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertEqual(1, robot.count("setPathOnRobot"))
        self.assertEqual("completed", robot.report["phase"])
        self.assertIs(True, robot.report["motion_commanded"])
        self.assertIs(False, robot.report["charging_commanded"])
        self.assertFalse(any(call[0] in {"setDO", "battery"} for call in robot.calls))
        self.assertEqual(3, len(robot.report["initial_samples"]))
        self.assertTrue(all(call[1] is False for call in robot.calls if call[0] == "stopRobot"))

    def test_required_advance_is_bounded_to_sixty_centimetres(self):
        robot = FakeRobot(robot={"x": 0.75, "y": 4.3, "angle": math.pi / 2})
        module = self.drive(robot, {"run_id": "far", "approach_standoff_m": 0.875})
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn("required approach distance", robot.report["error"])
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_large_lateral_offset_is_refused(self):
        robot = FakeRobot(robot={"x": 0.4, "y": 4.7, "angle": math.pi / 2})
        module = self.drive(robot, {"run_id": "lateral"})
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn("lateral offset", robot.report["error"])
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_near_field_stage_uses_planned_goal_without_post_recognition(self):
        robot = FakeRobot(robot={"x": 0.75, "y": 4.75, "angle": math.pi / 2})
        module = self.drive(robot, {"run_id": "near", "approach_standoff_m": 0.675})
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertEqual(1, robot.count("setPathOnRobot"))
        self.assertEqual(3, robot.count("getRecResult"))
        self.assertEqual("planned_goal_only", robot.report["post_check"]["mode"])
        self.assertIn("completed", robot.report["phase"])

    def test_heading_error_is_refused(self):
        robot = FakeRobot(robot={"x": 0.75, "y": 4.7, "angle": 0.0})
        module = self.drive(robot, {"run_id": "heading"})
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn("heading", robot.report["error"])
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_unstable_recognition_cannot_be_relaxed_to_five_degrees(self):
        robot = FakeRobot()
        module = self.drive(robot, {"run_id": "unstable", "stability_angle_deg": 5.0})
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_angle_median_handles_pi_wrap_without_reversing_docking_axis(self):
        module = approach.Module(FakeRobot(), {"run_id": "wrapped"})
        angles = [math.pi - 0.005, -math.pi + 0.002, math.pi - 0.001]
        module.initial_samples = [{"target_world": {"x": 1.0, "y": 6.0, "angle": a}}
                                  for a in angles]
        target = module.stable_target()
        self.assertAlmostEqual(math.pi - 0.001, target["angle"])

    def test_twenty_mm_and_two_degree_spread_still_rejects_unstable_samples(self):
        module = approach.Module(FakeRobot(), {"run_id": "spread"})
        module.initial_samples = [{"target_world": {"x": 1.0, "y": 6.0, "angle": a}}
                                  for a in [0.0, 0.01, 0.04]]
        with self.assertRaisesRegex(ValueError, "unstable"):
            module.stable_target()

    def test_one_clear_angle_outlier_requires_a_new_stable_triplet(self):
        module = approach.Module(FakeRobot(), {"run_id": "outlier"})
        module.initial_samples = [
            {"target_world": {"x": 1.02778, "y": 6.86236, "angle": -1.54404}},
            {"target_world": {"x": 1.02909, "y": 6.87636, "angle": -1.59414}},
            {"target_world": {"x": 1.02778, "y": 6.86246, "angle": -1.52765}},
        ]
        with self.assertRaisesRegex(ValueError, "unstable"):
            module.stable_target()
        self.assertFalse(module.motion_commanded)

    def test_bad_coordinate_profile_is_refused_before_motion(self):
        robot = FakeRobot()
        robot.getRecFile = lambda name: {
            "deviceParams": [{
                "key": "coordinate_system",
                "comboParam": {"childKey": "robot"},
            }]
        }
        module = self.drive(robot, {"run_id": "profile"})
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn("world-coordinate", robot.report["error"])
        self.assertEqual(0, robot.count("setPathOnRobot"))

    def test_cancel_stops_without_emergency_braking(self):
        robot = FakeRobot(reached_after=1000)
        args = {"run_id": "cancel"}
        module = approach.Module(robot, args)
        for _ in range(20):
            module.run(robot, args)
            self.clock.advance()
            if module.motion_commanded:
                break
        module.cancel(robot)
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertIn(("stopRobot", False), robot.calls)
        self.assertNotIn(("stopRobot", True), robot.calls)


if __name__ == "__main__":
    unittest.main()
