# -*- coding: utf-8 -*-
"""Guarded relative advance for the final charging-dock approach.

This module performs one short odometry path, then stops. It does not perform
recognition, relay control, charger communication, or any lateral correction.
"""
import json
import math
import time

from rbk import BasicModule, MoveStatus


"""
####BEGIN DEFAULT ARGS####
{
  "operation": {"value": "AdvanceByDistance", "default_value": ["AdvanceByDistance"], "type": "complex"},
  "distance_m": {"value": 0.10, "type": "float", "unit": "m"},
  "reach_dist_m": {"value": 0.001, "type": "float", "unit": "m"},
  "max_speed_mps": {"value": 0.01, "type": "float", "unit": "m/s"},
  "expected_x": {"value": 0.0, "type": "float", "unit": "m"},
  "expected_y": {"value": 0.0, "type": "float", "unit": "m"},
  "expected_angle": {"value": 0.0, "type": "float", "unit": "rad"},
  "timeout_s": {"value": 30.0, "type": "float", "unit": "s"}
}
####END DEFAULT ARGS####
"""

ADVANCE_ID = "charge_dock_advance_v1"


def finite_number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("value must be numeric")
    result = float(value)
    if not math.isfinite(result):
        raise ValueError("value must be finite")
    return result


def normalize(angle):
    return (angle + math.pi) % (2.0 * math.pi) - math.pi


class Module(BasicModule):
    def __init__(self, r, args):
        super(Module, self).__init__()
        self.phase = "initializing"
        self.started = time.monotonic()
        self.distance_m = 0.10
        self.reach_dist_m = 0.001
        self.max_speed_mps = 0.01
        self.timeout_s = 30.0
        self.expected = None
        self.run_id = ""
        self.start_pose = None
        self.final_pose = None
        self.motion_commanded = False
        self.charging_commanded = False
        self.error = None
        self.terminal = False
        self.last_report = None
        try:
            args = {} if args is None else args
            if not isinstance(args, dict):
                raise ValueError("script_args must be an object")
            if args.get("operation", "AdvanceByDistance") != "AdvanceByDistance":
                raise ValueError("only AdvanceByDistance is implemented")
            self.run_id = args.get("run_id", "")
            if not isinstance(self.run_id, str) or not self.run_id or len(self.run_id) > 128:
                raise ValueError("run_id must be a non-empty string of at most 128 characters")
            self.distance_m = finite_number(args.get("distance_m", self.distance_m))
            if not 0.001 <= self.distance_m <= 0.10:
                raise ValueError("distance_m must be between 0.001 and 0.10 m")
            self.reach_dist_m = finite_number(args.get("reach_dist_m", self.reach_dist_m))
            if not 0.001 <= self.reach_dist_m <= 0.01:
                raise ValueError("reach_dist_m must be between 0.001 and 0.01 m")
            self.max_speed_mps = finite_number(args.get("max_speed_mps", self.max_speed_mps))
            if not 0.002 <= self.max_speed_mps <= 0.02:
                raise ValueError("max_speed_mps must be between 0.002 and 0.02 m/s")
            self.timeout_s = finite_number(args.get("timeout_s", self.timeout_s))
            if not 10.0 <= self.timeout_s <= 45.0:
                raise ValueError("timeout_s must be between 10 and 45 seconds")
            self.expected = {
                "x": finite_number(args.get("expected_x")),
                "y": finite_number(args.get("expected_y")),
                "angle": normalize(finite_number(args.get("expected_angle"))),
            }
        except Exception as exc:
            self.error = str(exc)

    def pose(self, r):
        data = r.loc()
        if not isinstance(data, dict):
            raise ValueError("robot pose must be an object")
        return {
            "x": finite_number(data.get("x")),
            "y": finite_number(data.get("y")),
            "angle": normalize(finite_number(data.get("angle"))),
        }

    def report(self, r):
        robot = None
        try:
            robot = self.pose(r)
        except Exception:
            pass
        report = {
            "advance": ADVANCE_ID,
            "run_id": self.run_id,
            "phase": self.phase,
            "status": int(self.status),
            "distance_m": self.distance_m,
            "reach_dist_m": self.reach_dist_m,
            "max_speed_mps": self.max_speed_mps,
            "expected_start": self.expected,
            "start_pose": self.start_pose,
            "robot_world": robot,
            "motion_commanded": self.motion_commanded,
            "charging_commanded": self.charging_commanded,
            "elapsed_s": round(time.monotonic() - self.started, 3),
            "error": self.error,
        }
        text = json.dumps(report, ensure_ascii=True, allow_nan=False)
        r.setInfo(text)
        if self.terminal and text != self.last_report:
            r.logInfo(text)
        self.last_report = text

    def finish(self, r, phase, status, error=None):
        self.phase, self.status, self.error = phase, status, error
        self.terminal = True
        if phase in ("failed", "cancelled", "suspended"):
            try:
                r.stopRobot(False)
            except Exception:
                pass
            try:
                r.resetPath()
            except Exception:
                pass
        self.report(r)
        return self.status

    def prepare(self, r):
        start = self.pose(r)
        position_error = math.hypot(start["x"] - self.expected["x"],
                                    start["y"] - self.expected["y"])
        angle_error = abs(normalize(start["angle"] - self.expected["angle"]))
        if position_error > 0.03:
            raise ValueError("robot is more than 0.03 m from the expected start pose")
        if angle_error > 0.08:
            raise ValueError("robot heading is more than 0.08 rad from the expected start pose")
        self.start_pose = start
        r.resetPath()
        r.setPathReachDist(self.reach_dist_m)
        r.setPathReachAngle(0.01)
        r.setPathUseOdo(True)
        r.setPathBackMode(False)
        r.setPathMaxSpeed(self.max_speed_mps)
        r.setPathMaxRot(0.02)
        r.setPathOnRobot([0.0, self.distance_m], [0.0, 0.0], start["angle"])
        self.motion_commanded = True
        self.phase = "advancing"

    def run(self, r, args):
        if self.terminal:
            return self.status
        if self.error:
            return self.finish(r, "failed", MoveStatus.FAILED, self.error)
        if time.monotonic() - self.started >= self.timeout_s:
            return self.finish(r, "failed", MoveStatus.FAILED, "final advance timed out")
        self.status = MoveStatus.RUNNING
        try:
            if self.phase == "initializing":
                self.prepare(r)
            elif self.phase == "advancing":
                r.goPath({})
                if r.isPathReached():
                    r.stopRobot(False)
                    r.resetPath()
                    self.final_pose = self.pose(r)
                    return self.finish(r, "completed", MoveStatus.FINISHED)
            else:
                raise ValueError("unknown advance phase " + str(self.phase))
            self.report(r)
            return self.status
        except Exception as exc:
            return self.finish(r, "failed", MoveStatus.FAILED, str(exc))

    def cancel(self, r):
        if not self.terminal:
            return self.finish(r, "cancelled", MoveStatus.FAILED, "final advance cancelled")
        return self.status

    def reset(self, r):
        if not self.terminal:
            self.status = MoveStatus.RUNNING
        return self.status

    def suspend(self, r):
        if not self.terminal:
            return self.finish(r, "suspended", MoveStatus.SUSPENDED, "final advance suspended")
        return self.status
