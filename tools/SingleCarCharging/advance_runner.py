#!/usr/bin/env python3
"""Upload and execute one bounded relative final-docking advance."""
import argparse
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
import sys
import time
import uuid

from approach_runner import preflight
from probe_runner import (
    CONFIG_PORT,
    NAV_PORT,
    STATUS_PORT,
    Evidence,
    Refused,
    ResultUnknown,
    Robokit,
    ipv4,
    runtime_summary,
    stopped_runtime,
    unique_task_status,
    utc_now,
)


ADVANCE_MARKER = "charge_dock_advance_v1"
NAV_TASK_PREFIX = "charge-advance-"


def validate_script_timeout(distance_m, max_speed, script_timeout):
    minimum = 10.0 + abs(float(distance_m)) / float(max_speed)
    if float(script_timeout) < minimum:
        raise Refused(
            "script timeout %.1f s is insufficient for %.3f m at %.3f m/s; "
            "at least %.1f s is required" %
            (float(script_timeout), float(distance_m), float(max_speed), minimum))


def validate_alignment_options(distance_m, lateral_m, yaw_delta_rad,
                               max_speed, motion_mode, alignment_stage):
    if lateral_m == 0.0 and yaw_delta_rad == 0.0:
        return
    if not alignment_stage:
        raise ValueError(
            "lateral/heading correction requires --alignment-stage after retracting from the dock")
    if motion_mode != "path":
        raise ValueError("alignment stage requires path motion")
    if abs(distance_m) > 0.12 or max_speed > 0.005 or abs(yaw_delta_rad) > 0.08:
        raise ValueError(
            "alignment stage limits travel to 0.12 m, yaw to 0.08 rad "
            "and speed to 0.005 m/s")


def matching_report(value, run_id):
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except ValueError:
            return None
    if (isinstance(value, dict) and value.get("advance") == ADVANCE_MARKER
            and value.get("run_id") == run_id):
        return value
    return None


def report_from_runtime(state, run_id):
    for channel in ("move_status_info", "script_res"):
        report = matching_report(state.get(channel), run_id)
        if report is not None:
            return channel, report
    return None, None


def track_advance(client, task_id, run_id, timeout_s):
    deadline = time.monotonic() + timeout_s
    report = None
    source = None
    task_status = None
    state = None
    observations = []
    errors = []
    outcome = "unknown"
    while time.monotonic() < deadline:
        task_status, report, source = None, None, None
        try:
            package = client.request(1110, STATUS_PORT, {"task_ids": [task_id]}, deadline=deadline)
            task_status = unique_task_status(package, task_id)
            state = client.request(1100, STATUS_PORT, {}, deadline=deadline)
            current_source, current_report = report_from_runtime(state, run_id)
            if current_report is not None:
                report, source = current_report, current_source
            observations.append({
                "host_utc": utc_now(), "task_status": task_status,
                "script_status": state.get("script_status"),
                "robot": {key: state.get(key)
                          for key in ("x", "y", "angle", "charging", "manual_charge",
                                      "blocked", "errors", "fatals")},
            })
            if task_status in (5, 6, 7):
                outcome = "advance_task_failed"
                break
            if (task_status == 4 and report is not None and stopped_runtime(state)
                    and state.get("blocked") is False and state.get("emergency") is False
                    and state.get("charging") is False):
                if (report.get("motion_mode") == "open_speed"
                        and report.get("phase") in ("completed", "stopped_unverified")
                        and report.get("status") == 3 and report.get("error") is None
                        and report.get("charging_commanded") is False):
                    # Older scripts also reported "completed" solely on a timer.
                    # Never promote either form to measured-distance completion.
                    outcome = "open_speed_stopped_distance_unverified"
                    break
                if (report.get("motion_mode") == "path"
                        and report.get("phase") == "completed" and report.get("status") == 3
                        and report.get("error") is None
                        and report.get("charging_commanded") is False):
                    outcome = "advance_completed"
                    break
                if report.get("phase") in ("failed", "cancelled", "suspended"):
                    outcome = "advance_report_failed"
                    break
        except Exception as exc:
            errors.append({"host_utc": utc_now(), "error": str(exc)})
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(0.5, remaining))
    client.evidence.write_json("tracking.json", observations)
    return {
        "outcome": outcome,
        "task_id": task_id,
        "task_status": task_status,
        "report": report,
        "report_source": source,
        "last_runtime": runtime_summary(state) if state else None,
        "tracking_errors": errors,
        "charging_test_passed": False,
        "distance_verified": False if outcome == "open_speed_stopped_distance_unverified" else None,
        "message": ("Timed speed probe stopped; travel distance and contact remain unverified. "
                    "Do not repeat it automatically. Inspect the preserved start/end observations."
                    if outcome == "open_speed_stopped_distance_unverified" else
                    "Bounded odometry path reached its SDK goal and stopped; contact and charging were not commanded."
                    if outcome == "advance_completed" else
                    "Inspect evidence and robot state. This runner does not resend uncertain motion."),
    }


def run_advance(client, args, run_id):
    validate_script_timeout(
        args.distance_m, args.max_speed, args.script_timeout)
    validate_alignment_options(
        args.distance_m, args.lateral_m, args.yaw_delta_rad,
        args.max_speed, args.motion_mode,
        getattr(args, "alignment_stage", False))
    source = Path(__file__).with_name("charge_dock_advance.py").read_bytes()
    digest = hashlib.sha256(source).hexdigest()
    relative_path = "charge_dock_advance_%s.py" % digest[:12]
    client.evidence.write("advance_source.py", source)
    before = preflight(client, args.expected_id, args.expected_station)
    state = client.request(1100, STATUS_PORT, {})
    start = {
        "x": state.get("x"), "y": state.get("y"), "angle": state.get("angle"),
    }
    if any(type(start[key]) not in (int, float) or not math.isfinite(start[key]) for key in start):
        raise Refused("robot pose is missing or non-numeric")
    client.evidence.write_json("preflight_before_upload.json", before)
    client.evidence.write_json("run.json", {
        "run_id": run_id, "robot": args.robot, "expected_id": args.expected_id,
        "expected_station": args.expected_station, "relative_path": relative_path,
        "source_sha256": digest, "distance_m": args.distance_m,
        "lateral_m": args.lateral_m, "yaw_delta_rad": args.yaw_delta_rad,
        "obstacle_stop_dist_m": args.obstacle_stop_dist_m,
        "motion_mode": args.motion_mode,
        "alignment_stage": getattr(args, "alignment_stage", False),
        "reach_dist_m": args.reach_dist_m, "max_speed_mps": args.max_speed, "start": start,
        "contact_and_charging_enabled": False,
    })
    listing = client.request(1506, STATUS_PORT, {"type": "module"})
    entries = listing.get("list")
    if not isinstance(entries, list) or any(not isinstance(row, dict) for row in entries):
        raise Refused("script list is missing or malformed")
    existing = [row for row in entries if row.get("relative_path") == relative_path]
    if len(existing) > 1:
        raise Refused("duplicate script entries for the content-addressed module")
    if existing:
        saved = client.request(4022, CONFIG_PORT,
                               {"type": "module", "relative_path": relative_path}, raw=True)
        if saved != source:
            raise Refused("existing advance script bytes differ")
    else:
        client.request(4021, CONFIG_PORT, {
            "type": "module", "relative_path": relative_path,
            "script": source.decode("utf-8"),
        }, mutation=True)
    verified = client.request(4022, CONFIG_PORT,
                              {"type": "module", "relative_path": relative_path}, raw=True)
    if verified != source:
        raise Refused("downloaded advance script bytes differ; execution was not sent")
    if args.stage_only:
        return {
            "outcome": "advance_staged", "robot": args.robot, "run_id": run_id,
            "script": relative_path, "source_sha256": digest,
            "initial_state": before, "start": start, "motion_commanded": False,
            "charging_test_passed": False,
            "message": "Advance script uploaded and byte-verified; no navigation task was sent.",
        }
    fresh = preflight(client, args.expected_id, args.expected_station)
    # Bind the command to the final preflight, not to a pose read before upload.
    start = {key: fresh["runtime"].get(key) for key in ("x", "y", "angle")}
    client.evidence.write_json("preflight_before_execute.json", fresh)
    task_id = NAV_TASK_PREFIX + run_id
    client.request(3066, NAV_PORT, {"move_task_list": [{
        "id": "SELF_POSITION", "source_id": "SELF_POSITION", "task_id": task_id,
        "operation": "Script", "script_name": relative_path, "script_stage": 2,
        "script_args": {
            "operation": "AdvanceByDistance", "distance_m": args.distance_m,
            "lateral_m": args.lateral_m, "yaw_delta_rad": args.yaw_delta_rad,
            "obstacle_stop_dist_m": args.obstacle_stop_dist_m,
            "motion_mode": args.motion_mode,
            "alignment_stage": getattr(args, "alignment_stage", False),
            "reach_dist_m": args.reach_dist_m, "max_speed_mps": args.max_speed,
            "expected_x": start["x"], "expected_y": start["y"],
            "expected_angle": start["angle"], "timeout_s": args.script_timeout,
            "run_id": run_id,
        },
    }]}, mutation=True)
    result = track_advance(client, task_id, run_id, args.track_timeout)
    result.update({
        "robot": args.robot, "run_id": run_id, "script": relative_path,
        "source_sha256": digest, "initial_state": before, "start": start,
    })
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--expected-station")
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--distance-m", type=float, default=0.10)
    parser.add_argument("--lateral-m", type=float, default=0.0,
                        help="positive is robot-left and negative is robot-right")
    parser.add_argument("--yaw-delta-rad", type=float, default=0.0,
                        help="positive rotates robot-left in wheel/odometry convention")
    parser.add_argument("--obstacle-stop-dist-m", type=float, default=0.05)
    parser.add_argument("--motion-mode", choices=("path", "open_speed"), default="path")
    parser.add_argument("--reach-dist-m", type=float, default=0.001)
    parser.add_argument("--max-speed", type=float, default=0.01)
    parser.add_argument("--script-timeout", type=float, default=40.0)
    parser.add_argument("--track-timeout", type=float, default=45.0)
    parser.add_argument(
        "--alignment-stage", action="store_true",
        help="permit one bounded lateral/heading correction after the vehicle has been retracted")
    parser.add_argument("--stage-only", action="store_true")
    args = parser.parse_args(argv)
    if not -0.30 <= args.distance_m <= 0.15:
        parser.error("--distance-m must be between -0.30 and 0.15")
    if not -0.02 <= args.lateral_m <= 0.02:
        parser.error("--lateral-m must be between -0.02 and 0.02")
    if not -0.10 <= args.yaw_delta_rad <= 0.10:
        parser.error("--yaw-delta-rad must be between -0.10 and 0.10")
    if not 0.05 <= args.obstacle_stop_dist_m <= 0.10:
        parser.error("--obstacle-stop-dist-m must be between 0.05 and 0.10; protection cannot be disabled")
    if args.distance_m == 0.0 and args.lateral_m == 0.0 and args.yaw_delta_rad == 0.0:
        parser.error("distance, lateral and yaw adjustments cannot all be zero")
    if args.motion_mode == "open_speed" and (
            args.distance_m <= 0 or args.distance_m > 0.01
            or args.lateral_m != 0 or args.yaw_delta_rad != 0):
        parser.error("open_speed mode only permits 0 < distance-m <= 0.01 without lateral/yaw")
    if not 0.001 <= args.reach_dist_m <= 0.01:
        parser.error("--reach-dist-m must be between 0.001 and 0.01")
    if not 0.002 <= args.max_speed <= 0.02:
        parser.error("--max-speed must be between 0.002 and 0.02")
    if not 10.0 <= args.script_timeout <= 45.0:
        parser.error("--script-timeout must be between 10 and 45")
    if not 10.0 <= args.track_timeout <= 60.0:
        parser.error("--track-timeout must be between 10 and 60")
    try:
        validate_alignment_options(
            args.distance_m, args.lateral_m, args.yaw_delta_rad,
            args.max_speed, args.motion_mode, args.alignment_stage)
    except ValueError as exc:
        parser.error(str(exc))
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(args.evidence_dir, run_id)
    client = Robokit(args.robot, evidence)
    try:
        result = run_advance(client, args, run_id)
    except ResultUnknown as exc:
        result = {"outcome": "unknown", "error": str(exc),
                  "charging_test_passed": False, "automatic_resend_attempted": False}
    except Exception as exc:
        result = {"outcome": "refused_or_failed", "error": str(exc),
                  "charging_test_passed": False, "automatic_resend_attempted": False}
    result["evidence_directory"] = str(evidence.directory)
    result["host_utc"] = utc_now()
    evidence.write_json("result.json", result)
    print(json.dumps(result, ensure_ascii=True, indent=2, allow_nan=False))
    if result["outcome"] in ("advance_completed", "advance_staged"):
        return 0
    return 3 if result["outcome"] == "unknown" else 2


if __name__ == "__main__":
    sys.exit(main())
