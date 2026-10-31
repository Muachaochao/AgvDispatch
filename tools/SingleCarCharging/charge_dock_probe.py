# -*- coding: utf-8 -*-
"""Robokit charging-target recognition probe. This module never commands motion.

The returned relative pose describes the recognition reference frame; it is NOT
a vehicle docking target, contact offset, or permission to move that distance.
"""
import json
import math
import time

from rbk import BasicModule, MoveStatus


"""
####BEGIN DEFAULT ARGS####
{
  "operation": {"value": "RecognizeOnly", "default_value": ["RecognizeOnly"], "type": "complex", "tips": "Recognition only; no vehicle movement or charger control"},
  "recognition_file": {"value": "charger/c0001.charger", "type": "str"},
  "samples": {"value": 3, "type": "int"},
  "timeout_s": {"value": 20.0, "type": "float", "unit": "s"}
}
####END DEFAULT ARGS####
"""

PROBE_ID = "charge_dock_probe_v1"


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


def relative_pose(target, robot):
    dx, dy = target["x"] - robot["x"], target["y"] - robot["y"]
    c, s = math.cos(robot["angle"]), math.sin(robot["angle"])
    return {
        "x": c * dx + s * dy,
        "y": -s * dx + c * dy,
        "angle": normalize(target["angle"] - robot["angle"]),
    }


def selected_coordinate(profile):
    if not isinstance(profile, dict):
        return None
    # The downloaded JSON uses protobuf JSON names, while r.getRecFile()
    # exposes native protobuf field names in the vendor's reference script.
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


class Module(BasicModule):
    def __init__(self, r, args):
        super(Module, self).__init__()
        self.phase = "initializing"
        self.samples = []
        self.error = None
        self.terminal = False
        self.started = time.monotonic()
        self.timeout_s = 20.0
        self.recognition_file = "charger/c0001.charger"
        self.sample_count = 3
        self.last_report = None
        self.recognition_started = False
        self.run_id = ""
        self.profile_snapshot = None
        try:
            args = {} if args is None else args
            if not isinstance(args, dict):
                raise ValueError("script_args must be an object")
            if args.get("operation", "RecognizeOnly") != "RecognizeOnly":
                raise ValueError("only RecognizeOnly is implemented; docking is not enabled")
            self.run_id = args.get("run_id", "")
            if not isinstance(self.run_id, str) or len(self.run_id) > 128:
                self.run_id = ""
                raise ValueError("run_id must be a string of at most 128 characters")
            self.recognition_file = args.get("recognition_file", self.recognition_file)
            if (not isinstance(self.recognition_file, str)
                    or not self.recognition_file.startswith("charger/")
                    or not self.recognition_file.endswith(".charger")
                    or ".." in self.recognition_file or "\\" in self.recognition_file):
                raise ValueError("recognition_file must be a charger/*.charger relative path")
            self.sample_count = args.get("samples", self.sample_count)
            if type(self.sample_count) is not int or not 1 <= self.sample_count <= 5:
                raise ValueError("samples must be an integer from 1 to 5")
            self.timeout_s = finite_number(args.get("timeout_s", self.timeout_s))
            if not 1.0 <= self.timeout_s <= 60.0:
                raise ValueError("timeout_s must be between 1 and 60 seconds")
        except Exception as exc:
            self.error = str(exc)

    def report(self, r):
        report = {
            "probe": PROBE_ID,
            "run_id": self.run_id,
            "phase": self.phase,
            "status": int(self.status),
            "recognition_file": self.recognition_file,
            "recognition_profile": self.profile_snapshot,
            "sample_count": self.sample_count,
            "samples": self.samples,
            "elapsed_s": round(time.monotonic() - self.started, 3),
            "motion_commanded": False,
            "charger_commanded": False,
            "pose_meaning": "recognition reference only; docking offset is not calibrated",
            "error": self.error,
        }
        text = json.dumps(report, ensure_ascii=True, allow_nan=False)
        r.setInfo(text)
        if text != self.last_report and self.terminal:
            r.logInfo(text)
        self.last_report = text

    def finish(self, r, phase, status, error=None):
        self.phase, self.status, self.error = phase, status, error
        self.terminal = True
        if self.recognition_started:
            try:
                r.resetRec()
            except Exception as exc:
                self.phase, self.status = "failed", MoveStatus.FAILED
                self.error = "recognition cleanup failed: " + str(exc)
        self.report(r)
        return self.status

    def run(self, r, args):
        if self.terminal:
            return self.status
        if self.error:
            return self.finish(r, "failed", MoveStatus.FAILED, self.error)
        if time.monotonic() - self.started >= self.timeout_s:
            return self.finish(r, "failed", MoveStatus.FAILED, "recognition timed out")
        self.status = MoveStatus.RUNNING
        try:
            if self.phase == "initializing":
                profile = r.getRecFile(self.recognition_file)
                self.profile_snapshot = json.loads(json.dumps(profile, allow_nan=False))
                if selected_coordinate(profile) != "world":
                    raise ValueError("recognition profile format or world-coordinate selection is not confirmed")
                world_pose(r.loc(), "angle")
                self.recognition_started = True
                r.resetRec()
                self.phase = "awaiting_reset"
            else:
                state = r.getRecStatus()
                if self.phase == "awaiting_reset":
                    # Do not reuse the previous recognition's status=2/result.
                    if state == 0:
                        r.doRecWithAngle(self.recognition_file, 0.0)
                        self.phase = "recognizing"
                    elif state in (-1, 3):
                        raise ValueError("recognition reset returned error status " + str(state))
                    elif state not in (1, 2):
                        raise ValueError("unknown recognition status " + str(state))
                elif state == 2:
                    # Native bindings may reuse the result buffer on resetRec.
                    raw = json.loads(json.dumps(r.getRecResult(), allow_nan=False))
                    target = world_pose(raw, "yaw")
                    robot = world_pose(r.loc(), "angle")
                    self.samples.append({
                        "target_world": target,
                        "robot_world": robot,
                        "reference_in_robot": relative_pose(target, robot),
                        "raw_result": raw,
                    })
                    if len(self.samples) >= self.sample_count:
                        for i, first in enumerate(self.samples):
                            for second in self.samples[i + 1:]:
                                a, b = first["target_world"], second["target_world"]
                                if (math.hypot(a["x"] - b["x"], a["y"] - b["y"]) > 0.02
                                        or abs(normalize(a["angle"] - b["angle"])) > math.radians(2)):
                                    raise ValueError("recognition samples are unstable (20 mm / 2 degrees)")
                        return self.finish(r, "completed", MoveStatus.FINISHED)
                    r.resetRec()
                    self.phase = "awaiting_reset"
                elif state in (0, 1):
                    # Matches the reference script's cyclic recognition API usage.
                    r.doRecWithAngle(self.recognition_file, 0.0)
                else:
                    raise ValueError("recognition failed with status " + str(state))
            self.report(r)
            return self.status
        except Exception as exc:
            return self.finish(r, "failed", MoveStatus.FAILED, str(exc))

    def cancel(self, r):
        if not self.terminal:
            return self.finish(r, "cancelled", MoveStatus.FAILED, "probe cancelled; rerun explicitly")
        return self.status

    def reset(self, r):
        # A cancelled/completed instance must never begin a second recognition.
        if not self.terminal:
            self.status = MoveStatus.RUNNING
        return self.status

    def suspend(self, r):
        if not self.terminal:
            return self.finish(r, "suspended", MoveStatus.SUSPENDED, "probe suspended; rerun explicitly")
        return self.status
