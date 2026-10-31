# -*- coding: utf-8 -*-
"""Guarded one-stage charging-station approach.

This module recognizes the charger, verifies that the robot is in the expected
coarse-approach envelope, and moves once to a configured center-to-target
standoff. It never operates a charging relay or sends charger application data.
"""
import json
import math
import time

from rbk import BasicModule, MoveStatus


"""
####BEGIN DEFAULT ARGS####
{
  "operation": {"value": "Approach", "default_value": ["Approach"], "type": "complex"},
  "recognition_file": {"value": "charger/c0001.charger", "type": "str"},
  "approach_standoff_m": {"value": 0.875, "type": "float", "unit": "m"},
  "max_speed_mps": {"value": 0.03, "type": "float", "unit": "m/s"},
  "stability_angle_deg": {"value": 2.0, "type": "float", "unit": "deg"},
  "timeout_s": {"value": 75.0, "type": "float", "unit": "s"}
}
####END DEFAULT ARGS####
"""

APPROACH_ID = "charge_dock_approach_v1"


def finite_number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("pose contains a missing or non-numeric value")
    result = float(value)
    if not math.isfinite(result):
        raise ValueError("pose contains a non-finite value")
    return result


def normalize(angle):
    return (angle + math.pi) % (2.0 * math.pi) - math.pi


def world_pose(data, angle_key):
    if not isinstance(data, dict):
        raise ValueError("pose must be an object")
    return {
        "x": finite_number(data.get("x")),
        "y": finite_number(data.get("y")),
        "angle": normalize(finite_number(data.get(angle_key))),
    }


def selected_coordinate(profile):
    if not isinstance(profile, dict):
        return None
    fields = [key for key in ("deviceParams", "device_params") if key in profile]
    if len(fields) != 1:
        return None
    params = profile[fields[0]]
    if not isinstance(params, list):
        return None
    coordinates = [p for p in params if isinstance(p, dict)
                   and p.get("key") == "coordinate_system"]
    if len(coordinates) != 1:
        return None
    parameter = coordinates[0]
    fields = [key for key in ("comboParam", "combo_param") if key in parameter]
    if len(fields) != 1 or not isinstance(parameter[fields[0]], dict):
        return None
    combo = parameter[fields[0]]
    fields = [key for key in ("childKey", "child_key") if key in combo]
    return combo[fields[0]] if len(fields) == 1 else None


def charger_frame_distance(target, robot):
    """Return robot position in the charger target frame."""
    dx, dy = robot["x"] - target["x"], robot["y"] - target["y"]
    c, s = math.cos(target["angle"]), math.sin(target["angle"])
    return {
        "x": c * dx + s * dy,
        "y": -s * dx + c * dy,
    }


def robot_relative_delta(start, goal):
    dx, dy = goal["x"] - start["x"], goal["y"] - start["y"]
    c, s = math.cos(start["angle"]), math.sin(start["angle"])
    return {
        "x": c * dx + s * dy,
        "y": -s * dx + c * dy,
    }


class Module(BasicModule):
    def __init__(self, r, args):
        super(Module, self).__init__()
        self.phase = "initializing"
        self.started = time.monotonic()
        self.timeout_s = 75.0
        self.recognition_file = "charger/c0001.charger"
        self.standoff_m = 0.875
        self.max_speed_mps = 0.03
        self.stability_angle_rad = math.radians(2.0)
        self.run_id = ""
        self.recognition_started = False
        self.initial_samples = []
        self.current_result = None
        self.post_sample = None
        self.post_samples = None
        self.motion_commanded = False
        self.charging_commanded = False
        self.error = None
        self.terminal = False
        self.path_prepared = False
        self.last_target = None
        self.last_report = None
        try:
            args = {} if args is None else args
            if not isinstance(args, dict):
                raise ValueError("script_args must be an object")
            if args.get("operation", "Approach") != "Approach":
                raise ValueError("only Approach is implemented")
            self.run_id = args.get("run_id", "")
            if not isinstance(self.run_id, str) or not self.run_id or len(self.run_id) > 128:
                raise ValueError("run_id must be a non-empty string of at most 128 characters")
            self.recognition_file = args.get("recognition_file", self.recognition_file)
            if (not isinstance(self.recognition_file, str)
                    or not self.recognition_file.startswith("charger/")
                    or not self.recognition_file.endswith(".charger")
                    or ".." in self.recognition_file or "\\" in self.recognition_file):
                raise ValueError("recognition_file must be a charger/*.charger relative path")
            self.standoff_m = finite_number(args.get("approach_standoff_m", self.standoff_m))
            if not 0.65 <= self.standoff_m <= 1.0:
                raise ValueError("approach_standoff_m must be between 0.65 and 1.00 m")
            self.max_speed_mps = finite_number(args.get("max_speed_mps", self.max_speed_mps))
            if not 0.01 <= self.max_speed_mps <= 0.05:
                raise ValueError("max_speed_mps must be between 0.01 and 0.05 m/s")
            stability_angle_deg = finite_number(
                args.get("stability_angle_deg", 2.0))
            if not 1.0 <= stability_angle_deg <= 2.0:
                raise ValueError("stability_angle_deg must be between 1 and 2; unstable recognition cannot be relaxed")
            self.stability_angle_rad = math.radians(stability_angle_deg)
            self.timeout_s = finite_number(args.get("timeout_s", self.timeout_s))
            if not 20.0 <= self.timeout_s <= 120.0:
                raise ValueError("timeout_s must be between 20 and 120 seconds")
        except Exception as exc:
            self.error = str(exc)

    def report(self, r):
        robot = None
        try:
            robot = world_pose(r.loc(), "angle")
        except Exception:
            pass
        report = {
            "approach": APPROACH_ID,
            "run_id": self.run_id,
            "phase": self.phase,
            "status": int(self.status),
            "recognition_file": self.recognition_file,
            "approach_standoff_m": self.standoff_m,
            "max_speed_mps": self.max_speed_mps,
            "initial_samples": self.initial_samples,
            "post_sample": self.post_sample,
            "post_check": self.post_samples,
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
        if self.recognition_started:
            try:
                r.resetRec()
            except Exception:
                pass
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

    def begin_recognition(self, r, phase):
        self.recognition_started = True
        r.resetRec()
        self.phase = phase

    def consume_recognition(self, r):
        state = r.getRecStatus()
        if state == 2:
            raw = json.loads(json.dumps(r.getRecResult(), allow_nan=False))
            target = world_pose(raw, "yaw")
            robot = world_pose(r.loc(), "angle")
            self.current_result = {"target_world": target, "robot_world": robot}
            return True
        if state in (0, 1):
            r.doRecWithAngle(self.recognition_file, 0.0)
            return False
        raise ValueError("recognition failed with status " + str(state))

    def stable_target(self):
        targets = [sample["target_world"] for sample in self.initial_samples]
        if len(targets) != 3:
            raise ValueError("exactly three fresh recognition samples are required")
        for i, first in enumerate(targets):
            for second in targets[i + 1:]:
                if (math.hypot(first["x"] - second["x"], first["y"] - second["y"]) > 0.02
                        or abs(normalize(first["angle"] - second["angle"])) > self.stability_angle_rad):
                    raise ValueError(
                        "recognition samples are unstable (20 mm / %.1f degrees)"
                        % math.degrees(self.stability_angle_rad))
        # Use the median of three observations so one noisy direction sample
        # cannot become the commanded docking target.
        anchor = targets[0]["angle"]
        angles = sorted(anchor + normalize(t["angle"] - anchor) for t in targets)
        return {
            "x": sorted(t["x"] for t in targets)[len(targets) // 2],
            "y": sorted(t["y"] for t in targets)[len(targets) // 2],
            "angle": normalize(angles[len(angles) // 2]),
        }

    def start_after_initial_recognition(self, r):
        target = self.stable_target()
        robot = world_pose(r.loc(), "angle")
        frame = charger_frame_distance(target, robot)
        angle_error = abs(normalize((target["angle"] + math.pi) - robot["angle"]))
        advance = frame["x"] - self.standoff_m
        minimum_current = max(0.75, self.standoff_m + 0.08)
        if not minimum_current <= frame["x"] <= 1.80:
            raise ValueError(
                "current center-to-target distance is outside the "
                "%.2f-1.80 m approach envelope" % minimum_current)
        if abs(frame["y"]) > 0.45:
            raise ValueError("lateral offset from the charger axis exceeds 0.45 m")
        if angle_error > 0.35:
            raise ValueError("robot heading is more than 0.35 rad from the docking direction")
        if not -0.03 <= advance <= 0.60:
            raise ValueError("required approach distance is outside the -0.03 to 0.60 m limit")
        if advance <= 0.03:
            return False
        goal = {
            "x": target["x"] + self.standoff_m * math.cos(target["angle"]),
            "y": target["y"] + self.standoff_m * math.sin(target["angle"]),
            "angle": normalize(target["angle"] + math.pi),
        }
        delta = robot_relative_delta(robot, goal)
        r.resetPath()
        r.setPathReachDist(0.01)
        r.setPathReachAngle(0.02)
        r.setPathUseOdo(True)
        r.setPathBackMode(False)
        r.setPathMaxSpeed(self.max_speed_mps)
        r.setPathMaxRot(0.05)
        r.setPathOnRobot([0.0, delta["x"]], [0.0, delta["y"]], goal["angle"])
        self.motion_commanded = True
        self.phase = "moving_to_approach"
        self.path_prepared = True
        self.last_target = {"target": target, "initial_robot": robot,
                            "charger_frame_robot": frame, "goal": goal,
                            "robot_relative_delta": delta,
                            "advance_m": advance}
        return True

    def run(self, r, args):
        if self.terminal:
            return self.status
        if self.error:
            return self.finish(r, "failed", MoveStatus.FAILED, self.error)
        if time.monotonic() - self.started >= self.timeout_s:
            return self.finish(r, "failed", MoveStatus.FAILED, "approach timed out")
        self.status = MoveStatus.RUNNING
        try:
            if self.phase == "initializing":
                profile = r.getRecFile(self.recognition_file)
                if selected_coordinate(profile) != "world":
                    raise ValueError("recognition profile format or world-coordinate selection is not confirmed")
                self.begin_recognition(r, "initial_recognition")
            elif self.phase == "initial_recognition":
                if self.consume_recognition(r):
                    self.initial_samples.append(self.current_result)
                    if len(self.initial_samples) < 3:
                        self.begin_recognition(r, "initial_recognition")
                    elif not self.start_after_initial_recognition(r):
                        self.begin_recognition(r, "post_recognition")
            elif self.phase == "moving_to_approach":
                if not self.path_prepared or not self.motion_commanded:
                    raise ValueError("motion phase reached without a prepared path")
                r.goPath({})
                if r.isPathReached():
                    r.stopRobot(False)
                    r.resetPath()
                    if self.standoff_m < 0.80:
                        # The reference sequence does not re-recognize this close
                        # to the charger. At this range the configured target can
                        # leave the usable field and return status 3 even though
                        # the bounded path reached its planned goal.
                        self.post_samples = {
                            "mode": "planned_goal_only",
                            "reason": "near-field recognition is not a completion gate",
                            "planned_goal": self.last_target["goal"],
                            "robot_world": world_pose(r.loc(), "angle"),
                        }
                        return self.finish(r, "completed", MoveStatus.FINISHED)
                    self.begin_recognition(r, "post_recognition")
            elif self.phase == "post_recognition":
                if self.consume_recognition(r):
                    self.post_sample = self.current_result
                    residual = charger_frame_distance(
                        self.post_sample["target_world"], self.post_sample["robot_world"])
                    self.post_samples = {"target_world": self.post_sample["target_world"],
                                         "robot_world": self.post_sample["robot_world"],
                                         "charger_frame_robot": residual,
                                         "standoff_error_m": residual["x"] - self.standoff_m}
                    return self.finish(r, "completed", MoveStatus.FINISHED)
            else:
                raise ValueError("unknown approach phase " + str(self.phase))
            self.report(r)
            return self.status
        except Exception as exc:
            return self.finish(r, "failed", MoveStatus.FAILED, str(exc))

    def cancel(self, r):
        if not self.terminal:
            return self.finish(r, "cancelled", MoveStatus.FAILED, "approach cancelled")
        return self.status

    def reset(self, r):
        if not self.terminal:
            self.status = MoveStatus.RUNNING
        return self.status

    def suspend(self, r):
        if not self.terminal:
            return self.finish(r, "suspended", MoveStatus.SUSPENDED, "approach suspended")
        return self.status
