#!/usr/bin/env python3
"""Execute one guarded world-pose calibration from a configured dock profile."""
import argparse
import datetime as dt
import json
import math
from pathlib import Path
import time
import uuid

from approach_runner import preflight
from probe_runner import (
    Evidence,
    NAV_PORT,
    Refused,
    ResultUnknown,
    Robokit,
    ipv4,
    runtime_summary,
    stopped_runtime,
    unique_task_status,
    utc_now,
)


TASK_PREFIX = "charge-dock-pose-"
COMPLETION_STOP_SETTLE_SECONDS = 3.0


def normalize(angle):
    return (angle + math.pi) % (2.0 * math.pi) - math.pi


def finite(value, name):
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ValueError("%s must be a finite number" % name)
    return float(value)


def load_profile(path, expected_id, robot):
    value = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(value, dict) or value.get("schema") != "agv-configured-dock-pose-v1":
        raise ValueError("unsupported dock profile schema")
    if str(value.get("hardwareId", "")).casefold() != expected_id.casefold():
        raise Refused("dock profile hardware ID does not match --expected-id")
    if value.get("robotIp") != robot:
        raise Refused("dock profile robot IP does not match --robot")
    dock = value.get("dockPose")
    limits = value.get("motionLimits")
    if not isinstance(dock, dict) or not isinstance(limits, dict):
        raise ValueError("dockPose and motionLimits are required")
    drive_mode = value.get("driveMode", "forward")
    if drive_mode not in ("forward", "reverse"):
        raise ValueError("driveMode must be forward or reverse")
    expected_start_station = value.get("expectedStartStation")
    expected_end_station = value.get("expectedEndStation")
    for name, station in (
            ("expectedStartStation", expected_start_station),
            ("expectedEndStation", expected_end_station)):
        if station is not None and (not isinstance(station, str) or not station.strip()):
            raise ValueError("%s must be a non-empty station name" % name)
    if drive_mode == "reverse" and (
            expected_start_station is None or expected_end_station is None):
        raise ValueError(
            "reverse movement requires expectedStartStation and expectedEndStation")
    return {
        **value,
        "driveMode": drive_mode,
        "expectedStartStation": expected_start_station,
        "expectedEndStation": expected_end_station,
        "dockPose": {
            "x": finite(dock.get("x"), "dockPose.x"),
            "y": finite(dock.get("y"), "dockPose.y"),
            "angle": normalize(finite(dock.get("angleRad"), "dockPose.angleRad")),
        },
        "motionLimits": {
            "maximumCorrectionMeters": finite(
                limits.get("maximumCorrectionMeters"), "motionLimits.maximumCorrectionMeters"),
            "maximumYawRadians": finite(
                limits.get("maximumYawRadians"), "motionLimits.maximumYawRadians"),
            "maximumSpeedMetersPerSecond": finite(
                limits.get("maximumSpeedMetersPerSecond"),
                "motionLimits.maximumSpeedMetersPerSecond"),
            "maximumRotationSpeedRadiansPerSecond": finite(
                limits.get("maximumRotationSpeedRadiansPerSecond"),
                "motionLimits.maximumRotationSpeedRadiansPerSecond"),
            "reachDistanceMeters": finite(
                limits.get("reachDistanceMeters"), "motionLimits.reachDistanceMeters"),
            "reachAngleRadians": finite(
                limits.get("reachAngleRadians"), "motionLimits.reachAngleRadians"),
        },
    }


def validate_profile(profile):
    limits = profile["motionLimits"]
    reverse = profile["driveMode"] == "reverse"
    checks = (
        (0.0, limits["maximumCorrectionMeters"], 4.0 if reverse else 0.15,
         "maximumCorrectionMeters"),
        (0.0, limits["maximumYawRadians"], 0.10,
         "maximumYawRadians"),
        (0.002, limits["maximumSpeedMetersPerSecond"], 0.05 if reverse else 0.02,
         "maximumSpeedMetersPerSecond"),
        (0.002, limits["maximumRotationSpeedRadiansPerSecond"], 0.10,
         "maximumRotationSpeedRadiansPerSecond"),
        (0.001, limits["reachDistanceMeters"], 0.03,
         "reachDistanceMeters"),
        (0.005, limits["reachAngleRadians"], 0.10,
         "reachAngleRadians"),
    )
    for minimum, value, maximum, name in checks:
        if not minimum <= value <= maximum:
            raise ValueError(
                "%s must be between %s and %s" % (name, minimum, maximum))
    if profile["dockPose"]["x"] == 0.0 and profile["dockPose"]["y"] == 0.0:
        raise ValueError("dock pose cannot be the map origin")


def correction(current, dock, limits):
    distance = math.hypot(dock["x"] - current["x"], dock["y"] - current["y"])
    yaw = abs(normalize(dock["angle"] - current["angle"]))
    if distance > limits["maximumCorrectionMeters"]:
        raise Refused("configured dock pose is more than %.3f m away" %
                      limits["maximumCorrectionMeters"])
    if yaw > limits["maximumYawRadians"]:
        raise Refused("configured dock angle differs by more than %.3f rad" %
                      limits["maximumYawRadians"])
    return distance, yaw


def build_chain(task_id, dock, limits, drive_mode="forward"):
    if drive_mode not in ("forward", "reverse"):
        raise ValueError("unsupported drive mode")
    return {
        "move_task_list": [{
            "id": "SELF_POSITION",
            "source_id": "SELF_POSITION",
            "task_id": task_id,
            "operation": "Script",
            "script_name": "syspy/goPath.py",
            "script_stage": 2,
            "script_args": {
                "x": dock["x"],
                "y": dock["y"],
                "theta": dock["angle"],
                "reachAngle": limits["reachAngleRadians"],
                "reachDist": limits["reachDistanceMeters"],
                "useOdo": 1,
                "backMode": 1 if drive_mode == "reverse" else 0,
                "coordinate": "world",
                "maxSpeed": limits["maximumSpeedMetersPerSecond"],
                "maxRot": limits["maximumRotationSpeedRadiansPerSecond"],
            },
        }],
    }


def track(client, task_id, dock, limits, timeout_seconds,
          drive_mode="forward", expected_end_station=None):
    deadline = time.monotonic() + timeout_seconds
    latest_state = None
    task_status = None
    completed_at = None
    observations = []
    tracking_errors = []
    outcome = "unknown"
    while time.monotonic() < deadline:
        try:
            package = client.request(
                1110, 19204, {"task_ids": [task_id]}, deadline=deadline)
            task_status = unique_task_status(package, task_id)
            latest_state = client.request(1100, 19204, {}, deadline=deadline)
            observations.append({
                "host_utc": utc_now(),
                "task_status": task_status,
                "runtime": runtime_summary(latest_state),
            })
            if task_status in (5, 6, 7):
                outcome = "dock_pose_task_failed"
                break
            if task_status == 4:
                if completed_at is None:
                    completed_at = time.monotonic()
                if not stopped_runtime(latest_state):
                    if time.monotonic() - completed_at < COMPLETION_STOP_SETTLE_SECONDS:
                        # The vendor can publish task completion one sample
                        # before the explicit stop flags settle. Re-read the
                        # same completed task; never resend the motion.
                        remaining = deadline - time.monotonic()
                        if remaining > 0:
                            time.sleep(min(0.5, remaining))
                        continue
                    outcome = "dock_pose_completed_motion_unverified"
                else:
                    pose = {
                        "x": latest_state["x"],
                        "y": latest_state["y"],
                        "angle": normalize(latest_state["angle"]),
                    }
                    position_error = math.hypot(
                        pose["x"] - dock["x"], pose["y"] - dock["y"])
                    angle_error = abs(normalize(pose["angle"] - dock["angle"]))
                    if drive_mode == "reverse":
                        # A named station mark is authoritative in the narrow
                        # charging aisle. Keep a 10 cm/0.08 rad bound so an
                        # off-station success still cannot be promoted.
                        pose_verified = (
                            position_error <= max(0.10, limits["reachDistanceMeters"] * 8)
                            and angle_error <= max(0.08, limits["reachAngleRadians"] * 2)
                        )
                    else:
                        pose_verified = (
                            position_error <= max(0.03, limits["reachDistanceMeters"] * 3)
                            and angle_error <= max(0.05, limits["reachAngleRadians"] * 2)
                        )
                    outcome = (
                        ("reverse_transit_completed"
                         if drive_mode == "reverse" else "dock_pose_completed")
                        if pose_verified
                        else (
                            "reverse_transit_completed_pose_unverified"
                            if drive_mode == "reverse"
                            else "dock_pose_completed_pose_unverified")
                    )
                    if outcome.startswith("reverse_transit_completed") and \
                            latest_state.get("current_station") != expected_end_station:
                        outcome = "reverse_transit_completed_station_unverified"
                break
        except Exception as exc:
            tracking_errors.append({"host_utc": utc_now(), "error": str(exc)})
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(0.5, remaining))
    client.evidence.write_json("tracking.json", observations)
    client.evidence.write_json("tracking-errors.json", tracking_errors)
    return outcome, task_status, latest_state


def run(args):
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(Path(args.evidence_dir), run_id)
    client = Robokit(args.robot, evidence)
    profile = load_profile(args.profile, args.expected_id, args.robot)
    validate_profile(profile)
    evidence.write_json("profile.json", profile)
    initial = preflight(
        client, args.expected_id, profile["expectedStartStation"])
    identity = initial["identity"]
    if identity.get("current_map") != profile["robotMap"] or \
            identity.get("current_map_md5") != profile["robotMapMd5"]:
        raise Refused("robot map identity does not match the configured dock profile")
    if identity.get("vehicle_id") != profile.get("robotName"):
        raise Refused("robot name does not match the configured dock profile")
    current = initial["runtime"]
    distance, yaw = correction(current, profile["dockPose"], profile["motionLimits"])
    evidence.write_json("preflight.json", {
        "preflight": initial,
        "configured_dock_pose": profile["dockPose"],
        "correction_distance_m": distance,
        "correction_yaw_rad": yaw,
    })
    task_id = TASK_PREFIX + run_id
    chain = build_chain(
        task_id, profile["dockPose"], profile["motionLimits"], profile["driveMode"])
    evidence.write_json("run.json", {
        "run_id": run_id,
        "task_id": task_id,
        "profile": str(Path(args.profile).resolve()),
        "motion": chain,
        "drive_mode": profile["driveMode"],
        "expected_start_station": profile["expectedStartStation"],
        "expected_end_station": profile["expectedEndStation"],
        "charging_commanded": False,
    })
    try:
        response = client.request(3066, NAV_PORT, chain, mutation=True)
    except ResultUnknown as exc:
        return {
            "outcome": "unknown",
            "run_id": run_id,
            "task_id": task_id,
            "message": str(exc),
            "charging_test_passed": False,
        }, 3
    evidence.write_json("send_response.json", response)
    outcome, task_status, final_state = track(
        client, task_id, profile["dockPose"], profile["motionLimits"],
        args.timeout_seconds, profile["driveMode"], profile["expectedEndStation"])
    result = {
        "outcome": outcome,
        "run_id": run_id,
        "task_id": task_id,
        "task_status": task_status,
        "drive_mode": profile["driveMode"],
        "expected_start_station": profile["expectedStartStation"],
        "expected_end_station": profile["expectedEndStation"],
        "final_station": (final_state or {}).get("current_station"),
        "configured_dock_pose": profile["dockPose"],
        "last_runtime": runtime_summary(final_state) if final_state else None,
        "charging_commanded": False,
        "charging_test_passed": False,
        "evidence_directory": str(evidence.directory),
    }
    evidence.write_json("result.json", result)
    return result, 0 if outcome in (
        "dock_pose_completed", "reverse_transit_completed") else 2


def parse_args():
    parser = argparse.ArgumentParser(
        description="Execute one bounded world-pose calibration from a vehicle dock profile.")
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--profile", required=True)
    parser.add_argument("--evidence-dir", required=True)
    parser.add_argument("--timeout-seconds", type=float, default=45.0)
    args = parser.parse_args()
    if not 10.0 <= args.timeout_seconds <= 180.0:
        parser.error("--timeout-seconds must be between 10 and 180")
    return args


if __name__ == "__main__":
    try:
        result, code = run(parse_args())
    except Exception as exc:
        result, code = {"outcome": "refused", "message": str(exc)}, 2
    print(json.dumps(result, ensure_ascii=True, allow_nan=False, indent=2))
    raise SystemExit(code)
