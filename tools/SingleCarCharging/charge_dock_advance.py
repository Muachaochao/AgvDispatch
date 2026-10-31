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
  "lateral_m": {"value": 0.0, "type": "float", "unit": "m"},
  "yaw_delta_rad": {"value": 0.0, "type": "float", "unit": "rad"},
  "obstacle_stop_dist_m": {"value": 0.05, "type": "float", "unit": "m"},
  "reach_dist_m": {"value": 0.001, "type": "float", "unit": "m"},
  "max_speed_mps": {"value": 0.01, "type": "float", "unit": "m/s"},
  "motion_mode": {"value": "path", "default_value": ["path", "open_speed"], "type": "complex"},
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
        self.lateral_m = 0.0
        self.yaw_delta_rad = 0.0
        self.obstacle_stop_dist_m = 0.05
        self.reach_dist_m = 0.001
        self.max_speed_mps = 0.01
        self.motion_mode = "path"
        self.open_speed_started = None
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
            if not -0.30 <= self.distance_m <= 0.15:
                raise ValueError("distance_m must be between -0.30 and 0.15 m")
            self.lateral_m = finite_number(args.get("lateral_m", self.lateral_m))
            if not -0.02 <= self.lateral_m <= 0.02:
                raise ValueError("lateral_m must be between -0.02 and 0.02 m")
            self.yaw_delta_rad = finite_number(
                args.get("yaw_delta_rad", self.yaw_delta_rad))
            if not -0.10 <= self.yaw_delta_rad <= 0.10:
                raise ValueError("yaw_delta_rad must be between -0.10 and 0.10 rad")
            self.obstacle_stop_dist_m = finite_number(
                args.get("obstacle_stop_dist_m", self.obstacle_stop_dist_m))
            if not 0.0 <= self.obstacle_stop_dist_m <= 0.10:
                raise ValueError("obstacle_stop_dist_m must be between 0 and 0.10 m")
            if self.distance_m == 0.0 and self.lateral_m == 0.0:
                if self.yaw_delta_rad == 0.0:
                    raise ValueError("distance_m, lateral_m and yaw_delta_rad cannot all be zero")
            self.reach_dist_m = finite_number(args.get("reach_dist_m", self.reach_dist_m))
            if not 0.001 <= self.reach_dist_m <= 0.01:
                raise ValueError("reach_dist_m must be between 0.001 and 0.01 m")
            self.max_speed_mps = finite_number(args.get("max_speed_mps", self.max_speed_mps))
            if not 0.002 <= self.max_speed_mps <= 0.02:
                raise ValueError("max_speed_mps must be between 0.002 and 0.02 m/s")
            self.motion_mode = args.get("motion_mode", self.motion_mode)
            if self.motion_mode not in ("path", "open_speed"):
                raise ValueError("motion_mode must be path or open_speed")
            if self.motion_mode == "open_speed" and (
                    self.distance_m <= 0 or self.distance_m > 0.01
                    or self.lateral_m != 0 or self.yaw_delta_rad != 0):
                raise ValueError(
                    "open_speed mode only permits 0 < distance_m <= 0.01 without lateral/yaw")
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
            "lateral_m": self.lateral_m,
            "yaw_delta_rad": self.yaw_delta_rad,
            "obstacle_stop_dist_m": self.obstacle_stop_dist_m,
            "reach_dist_m": self.reach_dist_m,
            "max_speed_mps": self.max_speed_mps,
            "motion_mode": self.motion_mode,
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
        r.setObsStopDist(self.obstacle_stop_dist_m)
        if self.motion_mode == "open_speed":
            self.open_speed_started = time.monotonic()
            self.motion_commanded = True
            self.phase = "open_speed"
            return
        r.resetPath()
        r.setPathReachDist(self.reach_dist_m)
        r.setPathReachAngle(0.01)
        r.setPathUseOdo(True)
        r.setPathBackMode(self.distance_m < 0.0)
        r.setPathMaxSpeed(self.max_speed_mps)
        r.setPathMaxRot(0.02)
        goal_angle = normalize(start["angle"] + self.yaw_delta_rad)
        r.setPathOnRobot([0.0, self.distance_m], [0.0, self.lateral_m], goal_angle)
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
            elif self.phase == "open_speed":
                elapsed = time.monotonic() - self.open_speed_started
                duration = abs(self.distance_m) / self.max_speed_mps
                if elapsed >= duration:
                    r.openSpeed(0.0, 0.0, 0.0)
                    r.stopRobot(False)
                    self.final_pose = self.pose(r)
                    return self.finish(r, "completed", MoveStatus.FINISHED)
                direction = 1.0 if self.distance_m > 0 else -1.0
                r.openSpeed(direction * self.max_speed_mps, 0.0, 0.0)
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
