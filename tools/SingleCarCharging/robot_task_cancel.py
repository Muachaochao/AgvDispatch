#!/usr/bin/env python3
"""Cancel exactly one confirmed Robokit task without retrying an unknown send."""
import argparse
import datetime as dt
import json
from pathlib import Path
import time
import uuid

from probe_runner import (
    Evidence,
    NAV_PORT,
    Refused,
    ResultUnknown,
    Robokit,
    STATUS_PORT,
    ipv4,
    runtime_summary,
    stopped_runtime,
    unique_task_status,
    utc_now,
)


def run(args):
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(Path(args.evidence_dir), run_id)
    client = Robokit(args.robot, evidence)
    info = client.request(1000, STATUS_PORT, {})
    if str(info.get("id", "")).casefold() != args.expected_id.casefold():
        raise Refused("robot hardware ID does not match --expected-id")
    before_response = client.request(
        1110, STATUS_PORT, {"task_ids": [args.task_id]})
    before_status = unique_task_status(before_response, args.task_id)
    if before_status in (4, 5, 6, 7):
        state = client.request(1100, STATUS_PORT, {})
        stopped = stopped_runtime(state)
        return {
            "outcome": "already_terminal" if stopped else "terminal_motion_unverified",
            "run_id": run_id,
            "task_id": args.task_id,
            "task_status": before_status,
            "last_runtime": runtime_summary(state),
            "evidence_directory": str(evidence.directory),
        }, 0 if stopped else 3
    evidence.write_json("before.json", {
        "identity": {key: info.get(key) for key in (
            "id", "vehicle_id", "model", "version", "current_map", "current_map_md5")},
        "task_status": before_status,
    })
    try:
        response = client.request(3003, NAV_PORT, {
            "smartCancel": False,
            "taskId": args.task_id,
        }, mutation=True)
    except ResultUnknown as exc:
        return {
            "outcome": "unknown",
            "run_id": run_id,
            "task_id": args.task_id,
            "message": str(exc),
        }, 3
    evidence.write_json("cancel_response.json", response)
    deadline = time.monotonic() + args.timeout_seconds
    latest = None
    status = None
    while time.monotonic() < deadline:
        package = client.request(1110, STATUS_PORT, {
            "task_ids": [args.task_id],
        }, deadline=deadline)
        status = unique_task_status(package, args.task_id)
        latest = client.request(1100, STATUS_PORT, {}, deadline=deadline)
        if status in (4, 5, 6, 7) and stopped_runtime(latest):
            break
        time.sleep(0.5)
    evidence.write_json("after.json", {
        "task_status": status,
        "runtime": runtime_summary(latest) if latest else None,
    })
    stopped = latest is not None and stopped_runtime(latest)
    outcome = ("cancelled" if status == 6 else
               "completed_before_cancel" if status == 4 else "task_failed") if (
                   status in (4, 5, 6, 7) and stopped) else "unknown"
    accepted = outcome in ("cancelled", "completed_before_cancel")
    return {
        "outcome": outcome,
        "run_id": run_id,
        "task_id": args.task_id,
        "task_status": status,
        "last_runtime": runtime_summary(latest) if latest else None,
        "evidence_directory": str(evidence.directory),
    }, 0 if accepted else 2 if outcome == "task_failed" else 3


def parse_args():
    parser = argparse.ArgumentParser(
        description="Cancel one exact Robokit task after an explicit blocker.")
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--task-id", required=True)
    parser.add_argument("--evidence-dir", required=True)
    parser.add_argument("--timeout-seconds", type=float, default=20.0)
    args = parser.parse_args()
    if not 5.0 <= args.timeout_seconds <= 30.0:
        parser.error("--timeout-seconds must be between 5 and 30")
    return args


if __name__ == "__main__":
    try:
        result, code = run(parse_args())
    except Exception as exc:
        result, code = {"outcome": "refused", "message": str(exc)}, 2
    print(json.dumps(result, ensure_ascii=True, allow_nan=False, indent=2))
    raise SystemExit(code)
