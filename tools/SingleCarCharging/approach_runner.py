#!/usr/bin/env python3
"""Upload and execute one guarded charging-station coarse-approach stage."""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import sys
import time
import uuid

from probe_runner import (
    CONFIG_PORT,
    NAV_PORT,
    STATUS_PORT,
    Evidence,
    Refused,
    ResultUnknown,
    Robokit,
    identity_summary,
    ipv4,
    runtime_summary,
    utc_now,
)


APPROACH_MARKER = "charge_dock_approach_v1"
NAV_TASK_PREFIX = "charge-approach-"


def prior_terminal_approach(state):
    status, task_id = state.get("task_status"), state.get("task_id")
    prefixes = ("charge-recognition-", NAV_TASK_PREFIX, "charge-advance-")
    return (type(status) is int and status in (4, 5, 6, 7)
            and isinstance(task_id, str)
            and any(task_id.startswith(prefix) and len(task_id) > len(prefix)
                    for prefix in prefixes))


def prior_completed_recognition(state):
    return (state.get("task_status") == 4
            and isinstance(state.get("task_id"), str)
            and state["task_id"].startswith("charge-recognition-"))


def preflight(client, expected_id, expected_station):
    info = client.request(1000, STATUS_PORT, {})
    if str(info.get("id", "")).casefold() != expected_id.casefold():
        raise Refused("robot hardware ID does not match --expected-id")
    state = client.request(1100, STATUS_PORT, {})
    reasons = []
    old_approach = prior_terminal_approach(state)
    old_recognition = prior_completed_recognition(state)
    known_prior = old_approach or old_recognition
    status = state.get("task_status")
    if (type(status) is not int or status not in (0, 4)) and not old_approach:
        reasons.append("task_status must be idle/completed or a confirmed terminal approach task")
    if expected_station is not None:
        if state.get("current_station") != expected_station:
            reasons.append("current_station must match --expected-station")
    elif state.get("current_station") not in ("", None):
        reasons.append("current_station must be empty when --expected-station is omitted")
    if state.get("script_status") != 0:
        reasons.append("script_status must be idle(0)")
    if state.get("unfinished_path") != []:
        reasons.append("unfinished_path must be an empty array")
    if state.get("task_id") and not known_prior:
        reasons.append("task_id must be empty except for a confirmed terminal recognition/approach task")
    if state.get("is_stop") is not True:
        reasons.append("is_stop must explicitly be true")
    if state.get("chassisIsStop") is not True:
        reasons.append("chassisIsStop must explicitly be true")
    for key in ("charging", "manual_charge", "emergency"):
        if state.get(key) is not False:
            reasons.append(key + " must explicitly be false")
    if "auto_charge" in state and state["auto_charge"] is not False:
        reasons.append("auto_charge must explicitly be false")
    for key in ("errors", "fatals"):
        if key in state and state[key] != []:
            reasons.append(key + " must be empty")
    lock = state.get("current_lock")
    if not isinstance(lock, dict) or lock.get("locked") is not False:
        reasons.append("control ownership must explicitly be released by its current owner")
    if state.get("dispatch_mode") != 0:
        reasons.append("dispatch_mode must explicitly be standalone(0)")
    if reasons:
        raise Refused("approach preflight refused: " + "; ".join(reasons))
    confirmed_prior = None
    if known_prior:
        task_id = state["task_id"]
        response = client.request(1110, STATUS_PORT, {"task_ids": [task_id]})
        package = response.get("task_status_package", response)
        rows = package.get("task_status_list", []) if isinstance(package, dict) else []
        matches = [row for row in rows if isinstance(row, dict) and row.get("task_id") == task_id]
        if (len(matches) != 1 or type(matches[0].get("status")) is not int
                or matches[0]["status"] != status):
            raise Refused("prior charging task terminal state was not confirmed by fresh 1110")
        confirmed_prior = {
            "task_id": task_id,
            "status": status,
            "kind": "approach" if old_approach else "recognition",
        }
    return {
        "identity": identity_summary(info),
        "runtime": runtime_summary(state),
        "confirmed_prior_approach": confirmed_prior,
    }


def matching_report(value, run_id):
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except ValueError:
            return None
    if (isinstance(value, dict) and value.get("approach") == APPROACH_MARKER
            and value.get("run_id") == run_id):
        return value
    return None


def report_from_runtime(state, run_id):
    for channel in ("move_status_info", "script_res"):
        report = matching_report(state.get(channel), run_id)
        if report is not None:
            return channel, report
    return None, None


def track_approach(client, task_id, run_id, timeout_s):
    deadline = time.monotonic() + timeout_s
    latest_report = None
    latest_state = None
    latest_source = None
    task_status = None
    observations = []
    errors = []
    outcome = "unknown"
    while time.monotonic() < deadline:
        try:
            package = client.request(1110, STATUS_PORT, {"task_ids": [task_id]}, deadline=deadline)
            package = package.get("task_status_package", package)
            rows = package.get("task_status_list", [])
            matches = [row for row in rows if isinstance(row, dict) and row.get("task_id") == task_id]
            if len(matches) == 1 and type(matches[0].get("status")) is int:
                task_status = matches[0]["status"]
            latest_state = client.request(1100, STATUS_PORT, {}, deadline=deadline)
            source, report = report_from_runtime(latest_state, run_id)
            if report is not None:
                latest_report, latest_source = report, source
            observations.append({
                "host_utc": utc_now(), "task_status": task_status,
                "script_status": latest_state.get("script_status"),
                "robot": {key: latest_state.get(key)
                          for key in ("current_station", "x", "y", "angle", "confidence",
                                      "charging", "manual_charge", "blocked", "errors", "fatals")},
                "matched_report_source": source,
            })
            if task_status in (5, 6, 7):
                outcome = "approach_task_failed"
                break
            if task_status == 4 and latest_report is not None:
                if (latest_report.get("phase") == "completed"
                        and latest_report.get("status") == 3
                        and latest_report.get("error") is None
                        and latest_report.get("charging_commanded") is False):
                    outcome = "approach_completed"
                    break
                if latest_report.get("phase") in ("failed", "cancelled", "suspended"):
                    outcome = "approach_report_failed"
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
        "report": latest_report,
        "report_source": latest_source,
        "last_runtime": runtime_summary(latest_state) if latest_state else None,
        "tracking_errors": errors,
        "charging_test_passed": False,
        "message": ("Coarse approach completed; docking contact and charging were not commanded."
                    if outcome == "approach_completed" else
                    "Inspect evidence and robot state. This runner does not resend an uncertain motion command."),
    }


def run_approach(client, args, run_id):
    source_path = Path(__file__).with_name("charge_dock_approach.py")
    source = source_path.read_bytes()
    digest = hashlib.sha256(source).hexdigest()
    relative_path = "charge_dock_approach_%s.py" % digest[:12]
    client.evidence.write("approach_source.py", source)
    client.evidence.write_json("run.json", {
        "run_id": run_id, "robot": args.robot, "expected_id": args.expected_id,
        "expected_station": args.expected_station, "relative_path": relative_path,
        "source_sha256": digest, "approach_standoff_m": args.standoff_m,
        "max_speed_mps": args.max_speed, "contact_and_charging_enabled": False,
    })
    before = preflight(client, args.expected_id, args.expected_station)
    client.evidence.write_json("preflight_before_upload.json", before)
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
            raise Refused("existing approach script bytes differ")
    else:
        client.request(4021, CONFIG_PORT, {
            "type": "module",
            "relative_path": relative_path,
            "script": source.decode("utf-8"),
        }, mutation=True)
    verified = client.request(4022, CONFIG_PORT,
                              {"type": "module", "relative_path": relative_path}, raw=True)
    if verified != source:
        raise Refused("downloaded approach script bytes differ; execution was not sent")
    if args.stage_only:
        return {
            "outcome": "approach_staged",
            "robot": args.robot,
            "run_id": run_id,
            "script": relative_path,
            "source_sha256": digest,
            "initial_state": before,
            "motion_commanded": False,
            "charging_test_passed": False,
            "message": "Approach script uploaded and byte-verified; no navigation task was sent.",
        }
    fresh = preflight(client, args.expected_id, args.expected_station)
    client.evidence.write_json("preflight_before_execute.json", fresh)
    task_id = NAV_TASK_PREFIX + run_id
    payload = {"move_task_list": [{
        "id": "SELF_POSITION", "source_id": "SELF_POSITION", "task_id": task_id,
        "operation": "Script", "script_name": relative_path, "script_stage": 2,
        "script_args": {
            "operation": "Approach",
            "recognition_file": "charger/c0001.charger",
            "approach_standoff_m": args.standoff_m,
            "max_speed_mps": args.max_speed,
            "timeout_s": args.script_timeout,
            "run_id": run_id,
        },
    }]}
    client.request(3066, NAV_PORT, payload, mutation=True)
    result = track_approach(client, task_id, run_id, args.track_timeout)
    result.update({
        "robot": args.robot,
        "run_id": run_id,
        "script": relative_path,
        "source_sha256": digest,
        "initial_state": before,
    })
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--expected-station",
                        help="require an exact station; omit only after the robot has left it")
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--standoff-m", type=float, default=0.875)
    parser.add_argument("--max-speed", type=float, default=0.03)
    parser.add_argument("--script-timeout", type=float, default=75.0)
    parser.add_argument("--track-timeout", type=float, default=90.0)
    parser.add_argument("--stage-only", action="store_true",
                        help="upload and byte-verify the approach script without executing it")
    args = parser.parse_args(argv)
    if not 0.65 <= args.standoff_m <= 1.0:
        parser.error("--standoff-m must be between 0.65 and 1.00")
    if not 0.01 <= args.max_speed <= 0.05:
        parser.error("--max-speed must be between 0.01 and 0.05")
    if not 20.0 <= args.script_timeout <= 120.0:
        parser.error("--script-timeout must be between 20 and 120")
    if not 20.0 <= args.track_timeout <= 130.0:
        parser.error("--track-timeout must be between 20 and 130")
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(args.evidence_dir, run_id)
    client = Robokit(args.robot, evidence)
    try:
        result = run_approach(client, args, run_id)
    except ResultUnknown as exc:
        result = {"outcome": "unknown", "error": str(exc),
                  "charging_test_passed": False,
                  "automatic_resend_attempted": False}
    except Exception as exc:
        result = {"outcome": "refused_or_failed", "error": str(exc),
                  "charging_test_passed": False,
                  "automatic_resend_attempted": False}
    result["evidence_directory"] = str(evidence.directory)
    result["host_utc"] = utc_now()
    evidence.write_json("result.json", result)
    print(json.dumps(result, ensure_ascii=True, indent=2, allow_nan=False))
    if result["outcome"] in ("approach_completed", "approach_staged"):
        return 0
    return 3 if result["outcome"] == "unknown" else 2


if __name__ == "__main__":
    sys.exit(main())
