"""Offline contracts for the on-vehicle, recognition-only charging probe.

The fake exposes only the SDK methods permitted for this probe. The tests never
connect to a robot or charger, import a real Robokit SDK, or issue a command.
"""
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
    NEARTOGOAL = 2
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
    "charge_dock_probe_under_test", Path(__file__).with_name("charge_dock_probe.py")
)
probe = importlib.util.module_from_spec(_spec)
with mock.patch.dict(sys.modules, {"rbk": _rbk}):
    _spec.loader.exec_module(probe)


def profile(coordinate="world"):
    return {
        "deviceParams": [
            {
                "key": "coordinate_system",
                "type": "comboParam",
                "comboParam": {"childKey": coordinate, "childParams": []},
            }
        ]
    }


def native_sdk_profile(coordinate="world"):
    """SDK field spelling, distinct from the downloaded JSON file spelling."""
    return {
        "name": "charger",
        "device_params": [
            {
                "key": "basic",
                "type": "arrayParam",
                "array_param": {
                    "params": [
                        {"key": "rec_laser_id", "int32_value": -1},
                        {"key": "recognize", "bool_value": True},
                    ]
                },
            },
            {
                "key": "coordinate_system",
                "type": "comboParam",
                "combo_param": {"child_key": coordinate, "child_params": []},
            },
        ],
    }


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now

    def advance(self, duration=0.05):
        self.now += duration


class FakeRobot:
    """Strict allowlist: an attempted actuator access is recorded and rejected."""

    def __init__(self, statuses=None, results=None, configuration=None, location=None):
        self.statuses = deque([0, 1, 2] * 3 if statuses is None else statuses)
        self.results = deque(
            [{"x": 1.0, "y": 2.0, "yaw": 0.5}] * 3
            if results is None else results
        )
        self.configuration = profile() if configuration is None else configuration
        self.location = {"x": 0.0, "y": 0.0, "angle": 0.0} if location is None else location
        self.default_status = 2
        self.calls = []
        self.forbidden_access = []
        self.reports = []
        self.reset_failure_at = None

    def __getattr__(self, name):
        self.forbidden_access.append(name)
        raise AssertionError("probe accessed forbidden robot API: " + name)

    def count(self, method):
        return sum(call[0] == method for call in self.calls)

    def getRecFile(self, name):
        self.calls.append(("getRecFile", name))
        return copy.deepcopy(self.configuration)

    def loc(self):
        self.calls.append(("loc",))
        return copy.deepcopy(self.location)

    def resetRec(self):
        self.calls.append(("resetRec",))
        if self.reset_failure_at == self.count("resetRec"):
            raise RuntimeError("recognition reset unavailable")

    def getRecStatus(self):
        value = self.statuses.popleft() if self.statuses else self.default_status
        self.calls.append(("getRecStatus", value))
        return value

    def doRecWithAngle(self, filename, angle):
        self.calls.append(("doRecWithAngle", filename, angle))
        if type(angle) is not float or angle != 0.0:
            raise AssertionError("probe must keep the recognition mechanism angle at float 0.0")

    def getRecResult(self):
        self.calls.append(("getRecResult",))
        return copy.deepcopy(self.results.popleft())

    def setInfo(self, text):
        self.calls.append(("setInfo",))
        self.reports.append(json.loads(text))

    def logInfo(self, text):
        self.calls.append(("logInfo",))

    def logWarn(self, text):
        self.calls.append(("logWarn",))


class ProbeTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.clock_patch = mock.patch.object(probe.time, "monotonic", self.clock)
        self.clock_patch.start()
        # Also catch an attempted direct connection outside the robot SDK.
        self.socket_patch = mock.patch("socket.socket", side_effect=AssertionError("network forbidden"))
        self.socket = self.socket_patch.start()
        self.robots = []

    def tearDown(self):
        try:
            self.socket.assert_not_called()
            for robot in self.robots:
                self.assertEqual([], robot.forbidden_access)
                for report in robot.reports:
                    self.assertEqual("charge_dock_probe_v1", report["probe"])
                    self.assertIs(False, report["motion_commanded"])
                    self.assertIs(False, report["charger_commanded"])
        finally:
            self.socket_patch.stop()
            self.clock_patch.stop()

    def robot(self, **kwargs):
        result = FakeRobot(**kwargs)
        self.robots.append(result)
        return result

    def drive(self, robot, args=None, duration=0.05, cycles=100):
        module = probe.Module(robot, args)
        for _ in range(cycles):
            module.run(robot, args)
            if module.terminal:
                return module
            self.clock.advance(duration)
        self.fail("probe did not reach a bounded terminal state")

    def test_three_fresh_samples_complete_without_actuator_or_network_access(self):
        robot = self.robot()
        module = self.drive(robot)
        self.assertEqual(MoveStatus.FINISHED, module.status)
        report = robot.reports[-1]
        self.assertEqual("completed", report["phase"])
        self.assertEqual(3, len(report["samples"]))
        self.assertEqual(3, robot.count("getRecResult"))
        self.assertGreaterEqual(robot.count("resetRec"), 4)
        for sample in report["samples"]:
            self.assertEqual({"x": 1.0, "y": 2.0, "angle": 0.5}, sample["target_world"])
            self.assertIn("reference", report["pose_meaning"])

    def test_reference_pose_uses_robot_heading_without_becoming_a_motion_target(self):
        robot = self.robot(
            statuses=[0, 2], results=[{"x": 1.0, "y": 3.0, "yaw": math.pi}],
            location={"x": 1.0, "y": 2.0, "angle": math.pi / 2},
        )
        module = self.drive(robot, {"samples": 1})
        self.assertEqual(MoveStatus.FINISHED, module.status)
        relative = robot.reports[-1]["samples"][0]["reference_in_robot"]
        self.assertAlmostEqual(1.0, relative["x"])
        self.assertAlmostEqual(0.0, relative["y"])
        self.assertAlmostEqual(math.pi / 2, relative["angle"])

    def test_stale_success_after_reset_is_not_accepted_as_a_new_sample(self):
        robot = self.robot(statuses=[2] * 50)
        module = self.drive(robot, {"samples": 1, "timeout_s": 1.0}, duration=0.1)
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertEqual(0, robot.count("getRecResult"))
        self.assertEqual(0, robot.count("doRecWithAngle"))
        self.assertIn("timed out", robot.reports[-1]["error"])

    def test_each_sample_requires_a_new_reset_acknowledgement(self):
        robot = self.robot(statuses=[0, 2] + [2] * 50)
        module = self.drive(robot, {"samples": 3, "timeout_s": 1.0}, duration=0.1)
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertEqual(1, robot.count("getRecResult"))
        self.assertEqual(1, len(robot.reports[-1]["samples"]))

    def test_unknown_or_failed_recognition_never_advances(self):
        for state in (-1, 3, 99):
            with self.subTest(state=state):
                robot = self.robot(statuses=[0, state])
                module = self.drive(robot)
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual(0, robot.count("getRecResult"))
                self.assertEqual([], robot.reports[-1]["samples"])

    def test_absent_or_non_world_profile_fails_before_recognition_start(self):
        configurations = [{}, profile("robot"), {"deviceParams": "invalid"},
                          native_sdk_profile("robot"), {"device_params": "invalid"}]
        duplicate = profile()
        duplicate["deviceParams"] *= 2
        configurations.append(duplicate)
        for configuration in configurations:
            with self.subTest(configuration=configuration):
                robot = self.robot(configuration=configuration)
                module = self.drive(robot)
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual(0, robot.count("doRecWithAngle"))

    def test_native_sdk_snake_case_profile_supports_three_fresh_recognitions(self):
        robot = self.robot(configuration=native_sdk_profile())
        module = self.drive(robot)
        self.assertEqual(MoveStatus.FINISHED, module.status)
        self.assertEqual("completed", robot.reports[-1]["phase"])
        self.assertEqual(3, robot.count("getRecResult"))
        self.assertEqual(3, len(robot.reports[-1]["samples"]))

    def test_ambiguous_profile_aliases_are_rejected_even_when_values_agree(self):
        cases = []
        for snake_value in (native_sdk_profile()["device_params"], [], None):
            mixed = profile()
            mixed["device_params"] = snake_value
            cases.append(("both profile aliases", mixed))
        for other_value in ({"child_key": "world"}, {"child_key": "robot"}, None):
            mixed = profile()
            mixed["deviceParams"][0]["combo_param"] = other_value
            cases.append(("both combo aliases", mixed))
        for other_value in ("world", "robot", None):
            mixed = profile()
            mixed["deviceParams"][0]["comboParam"]["child_key"] = other_value
            cases.append(("both child aliases", mixed))
        for label, configuration in cases:
            with self.subTest(alias=label, configuration=configuration):
                robot = self.robot(configuration=configuration)
                module = self.drive(robot)
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual(0, robot.count("doRecWithAngle"))
                self.assertEqual(0, robot.count("getRecResult"))

    def test_duplicate_coordinate_entries_are_rejected_in_native_sdk_profile(self):
        for coordinate in ("world", "robot"):
            configuration = native_sdk_profile()
            duplicate = copy.deepcopy(configuration["device_params"][1])
            duplicate["combo_param"]["child_key"] = coordinate
            configuration["device_params"].append(duplicate)
            with self.subTest(duplicate_coordinate=coordinate):
                robot = self.robot(configuration=configuration)
                module = self.drive(robot)
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual(0, robot.count("doRecWithAngle"))
                self.assertEqual(0, robot.count("getRecResult"))

    def test_unsupported_or_malformed_args_do_not_start_recognition(self):
        args_cases = [
            {"operation": "Dock"}, {"operation": "StartCharge"}, [],
            {"samples": True}, {"samples": 0}, {"samples": 6}, {"samples": 1.5},
            {"timeout_s": 0}, {"timeout_s": 61}, {"timeout_s": float("nan")},
            {"timeout_s": True}, {"recognition_file": "../charger/c0001.charger"},
            {"recognition_file": "charger/../c0001.charger"},
            {"recognition_file": "charger\\c0001.charger"},
            {"recognition_file": "/charger/c0001.charger"},
        ]
        for args in args_cases:
            with self.subTest(args=args):
                robot = self.robot()
                module = self.drive(robot, args)
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual(0, robot.count("getRecFile"))
                self.assertEqual(0, robot.count("doRecWithAngle"))

    def test_missing_or_nonfinite_recognized_pose_fails_without_a_sample(self):
        results = [None, [], {}, {"x": 1, "y": 2}, {"x": True, "y": 2, "yaw": 0}]
        for key in ("x", "y", "yaw"):
            for value in (float("nan"), float("inf"), "0.1"):
                result = {"x": 1.0, "y": 2.0, "yaw": 0.0}
                result[key] = value
                results.append(result)
        for result in results:
            with self.subTest(result=result):
                robot = self.robot(statuses=[0, 2], results=[result])
                module = self.drive(robot, {"samples": 1})
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual([], robot.reports[-1]["samples"])

    def test_missing_or_nonfinite_robot_pose_fails_before_recognition_start(self):
        for location in ({}, {"x": 0, "y": 0, "angle": float("inf")}, {"x": 0, "y": None, "angle": 0}):
            with self.subTest(location=location):
                robot = self.robot(location=location)
                module = self.drive(robot)
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual(0, robot.count("doRecWithAngle"))

    def test_unserializable_sdk_metadata_is_rejected_without_corrupting_the_report(self):
        for extra in (float("nan"), float("inf"), object()):
            with self.subTest(extra=type(extra).__name__):
                result = {"x": 1.0, "y": 2.0, "yaw": 0.5, "extra": extra}
                robot = self.robot(statuses=[0, 2], results=[result])
                module = self.drive(robot, {"samples": 1})
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual([], robot.reports[-1]["samples"])
                self.assertEqual("failed", robot.reports[-1]["phase"])

    def test_report_preserves_sdk_result_before_reset_reuses_its_buffer(self):
        class ReusingBufferRobot(FakeRobot):
            def __init__(self):
                super().__init__(statuses=[0, 2])
                self.sdk_result = {"x": 1.0, "y": 2.0, "yaw": 0.5, "metadata": {"frame": "original"}}

            def getRecResult(self):
                self.calls.append(("getRecResult",))
                return self.sdk_result

            def resetRec(self):
                super().resetRec()
                if self.count("resetRec") >= 2:
                    self.sdk_result["x"] = 999.0
                    self.sdk_result["metadata"]["frame"] = "reused"

        robot = ReusingBufferRobot()
        self.robots.append(robot)
        module = self.drive(robot, {"samples": 1})
        self.assertEqual(MoveStatus.FINISHED, module.status)
        raw = robot.reports[-1]["samples"][0]["raw_result"]
        self.assertEqual(1.0, raw["x"])
        self.assertEqual("original", raw["metadata"]["frame"])

    def test_reports_are_correlated_with_the_requested_run_id(self):
        robot = self.robot(statuses=[0, 2])
        module = self.drive(robot, {"samples": 1, "run_id": "offline-recognition-attempt"})
        self.assertEqual(MoveStatus.FINISHED, module.status)
        for report in robot.reports:
            self.assertEqual("offline-recognition-attempt", report["run_id"])

    def test_invalid_run_id_does_not_start_recognition(self):
        for run_id in (None, True, 3, "x" * 129):
            with self.subTest(run_id=run_id):
                robot = self.robot()
                module = self.drive(robot, {"run_id": run_id})
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertEqual(0, robot.count("getRecFile"))
                self.assertEqual(0, robot.count("doRecWithAngle"))

    def test_unstable_translation_or_heading_is_not_a_success(self):
        for last in ({"x": 1.03, "y": 2.0, "yaw": 0.5},
                     {"x": 1.0, "y": 2.0, "yaw": 0.5 + math.radians(3)}):
            with self.subTest(last=last):
                robot = self.robot(results=[{"x": 1.0, "y": 2.0, "yaw": 0.5}] * 2 + [last])
                module = self.drive(robot)
                self.assertEqual(MoveStatus.FAILED, module.status)
                self.assertIn("unstable", robot.reports[-1]["error"])

    def test_heading_stability_handles_wrapping_at_pi(self):
        results = [{"x": 1.0, "y": 2.0, "yaw": angle}
                   for angle in (math.pi - 0.005, -math.pi + 0.005, math.pi)]
        robot = self.robot(results=results)
        module = self.drive(robot)
        self.assertEqual(MoveStatus.FINISHED, module.status)

    def test_recognition_that_remains_busy_times_out(self):
        robot = self.robot(statuses=[0] + [1] * 50)
        module = self.drive(robot, {"timeout_s": 1.0}, duration=0.1)
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertEqual(0, robot.count("getRecResult"))
        self.assertIn("timed out", robot.reports[-1]["error"])

    def test_completed_probe_is_not_restarted_by_repeated_run(self):
        robot = self.robot()
        module = self.drive(robot)
        calls = list(robot.calls)
        self.assertEqual(MoveStatus.FINISHED, module.reset(robot))
        for _ in range(3):
            self.clock.advance(30)
            self.assertEqual(MoveStatus.FINISHED, module.run(robot, {}))
        self.assertEqual(calls, robot.calls)

    def test_cancel_and_suspend_remain_terminal_even_if_run_is_called_again(self):
        for operation, phase, status in (("cancel", "cancelled", MoveStatus.FAILED),
                                         ("suspend", "suspended", MoveStatus.SUSPENDED)):
            with self.subTest(operation=operation):
                robot = self.robot()
                module = probe.Module(robot, {})
                module.run(robot, {})
                self.assertEqual(status, getattr(module, operation)(robot))
                self.assertEqual(phase, robot.reports[-1]["phase"])
                calls = list(robot.calls)
                self.assertEqual(status, module.reset(robot))
                self.clock.advance(30)
                self.assertEqual(status, module.run(robot, {}))
                self.assertEqual(calls, robot.calls)
                self.assertEqual(0, robot.count("getRecResult"))

    def test_cancel_before_first_cycle_cannot_start_recognition_on_reset(self):
        robot = self.robot()
        module = probe.Module(robot, {})
        self.assertEqual(MoveStatus.FAILED, module.cancel(robot))
        calls = list(robot.calls)
        self.assertEqual(MoveStatus.FAILED, module.reset(robot))
        self.assertEqual(MoveStatus.FAILED, module.run(robot, {}))
        self.assertEqual(calls, robot.calls)
        self.assertEqual(0, robot.count("resetRec"))
        self.assertEqual(0, robot.count("getRecFile"))

    def test_cleanup_failure_cannot_be_reported_as_completed(self):
        robot = self.robot(statuses=[0, 2])
        robot.reset_failure_at = 2
        module = self.drive(robot, {"samples": 1})
        self.assertEqual(MoveStatus.FAILED, module.status)
        self.assertEqual("failed", robot.reports[-1]["phase"])
        self.assertIn("cleanup failed", robot.reports[-1]["error"])


if __name__ == "__main__":
    unittest.main()
