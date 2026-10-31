#!/usr/bin/env python3
"""Perform one guarded DO7 transition and observe the robot."""
import argparse
import datetime as dt
import math
from pathlib import Path
import sys
import time
import uuid

from probe_runner import Evidence, Refused, ResultUnknown, Robokit, ipv4, stopped_runtime, utc_now


RELAY_API = 6001
RELAY_PORT = 19210
RELAY_DO_ID = 7
MAX_EVIDENCE_GAP_SECONDS = 5.0


def snapshot(client):
    state = client.request(1100, 19204, {})
    battery = client.request(1007, 19204, {})
    return state, battery


def summary(state, battery):
    outputs = state.get("DO")
    matches = ([row for row in outputs if isinstance(row, dict)
                and type(row.get("id")) is int and row["id"] == 7]
               if isinstance(outputs, list) else [])
    do7 = matches[0].get("status") if len(matches) == 1 else None
    return {
        "state": {key: state.get(key) for key in (
            "is_stop", "chassisIsStop", "charging", "manual_charge",
            "auto_charge", "blocked", "emergency", "soft_emc", "errors", "fatals",
            "vx", "vy", "w") if key in state},
        "do7": do7,
        "battery": {key: battery.get(key) for key in (
            "battery_level", "battery_temp", "charging", "manual_charge",
            "auto_charge", "current", "voltage")},
    }


def successful_relay_response(value):
    return (isinstance(value, dict)
            and type(value.get("ret_code")) is int
            and value["ret_code"] == 0)


def finite_current(sample):
    value = sample.get("battery", {}).get("current")
    return value if type(value) in (int, float) and math.isfinite(value) else None


def observation_safe(sample):
    state, battery = sample.get("state", {}), sample.get("battery", {})
    return (stopped_runtime(state)
            and state.get("blocked") is False and state.get("emergency") is False
            and state.get("soft_emc", False) is False
            and state.get("errors") == [] and state.get("fatals") == []
            and state.get("manual_charge") is False
            and battery.get("manual_charge") is False)


def positive_charge(sample):
    current = finite_current(sample)
    return (observation_safe(sample) and sample.get("do7") is True
            and sample["state"].get("charging") is True
            and sample["battery"].get("charging") is True
            and current is not None and current > 0.1)


def current_stopped(sample):
    current = finite_current(sample)
    return (observation_safe(sample) and sample.get("do7") is False
            and sample["state"].get("charging") is False
            and sample["battery"].get("charging") is False
            and current is not None and current <= 0.1)


def fresh_pair(first, second, allow_equal=False):
    try:
        earlier = dt.datetime.fromisoformat(first["host_utc"])
        later = dt.datetime.fromisoformat(second["host_utc"])
        if earlier.tzinfo is None or later.tzinfo is None:
            return False
        elapsed = (later - earlier).total_seconds()
        return ((elapsed >= 0 if allow_equal else elapsed > 0)
                and elapsed <= MAX_EVIDENCE_GAP_SECONDS)
    except (KeyError, TypeError, ValueError):
        return False


def relay_observation_result(observations, final, enabled):
    if any(not observation_safe(sample) for sample in [*observations, final]):
        return "relay_attention_required", False
    if enabled:
        confirmed = (len(observations) >= 2
                     and positive_charge(observations[-2])
                     and positive_charge(observations[-1])
                     and fresh_pair(observations[-2], observations[-1])
                     and positive_charge(final)
                     and fresh_pair(observations[-1], final, allow_equal=True))
        return ("charging_current_confirmed" if confirmed else "relay_on_current_not_observed", confirmed)
    stopped = (bool(observations) and current_stopped(observations[-1])
               and current_stopped(final)
               and fresh_pair(observations[-1], final, allow_equal=True))
    return ("relay_off_current_stopped" if stopped else "relay_off_current_not_confirmed", False)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--enable", action="store_true")
    mode.add_argument("--disable", action="store_true")
    parser.add_argument("--observe-seconds", type=int, default=20)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args(argv)
    if not 5 <= args.observe_seconds <= 30:
        parser.error("--observe-seconds must be between 5 and 30")

    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(args.evidence_dir, run_id)
    client = Robokit(args.robot, evidence)
    result = {
        "outcome": "not_sent", "run_id": run_id, "robot": args.robot,
        "requested_relay": args.enable, "charging_test_passed": False,
        "full_docking_cycle_verified": False,
    }
    try:
        info = client.request(1000, 19204, {})
        if str(info.get("id", "")).casefold() != args.expected_id.casefold():
            raise Refused("robot hardware ID mismatch")
        state, battery = snapshot(client)
        before = summary(state, battery)
        blockers = []
        if args.enable:
            if not stopped_runtime(state):
                blockers.append("vehicle is not confirmed stopped")
            if state.get("manual_charge") is not False:
                blockers.append("manual charge signal is active")
            if state.get("charging") is not False or battery.get("charging") is not False:
                blockers.append("vehicle is already charging")
            if (state.get("blocked") is not False or state.get("emergency") is not False
                    or state.get("soft_emc", False) is not False):
                blockers.append("vehicle is blocked or in emergency")
            if state.get("errors") != [] or state.get("fatals") != []:
                blockers.append("vehicle reports errors")
            if before["do7"] is not False:
                blockers.append("DO7 is not explicitly off before enabling")
        else:
            if state.get("manual_charge") is not False:
                blockers.append("manual charge signal is active; use the station stop control")
            if before["do7"] is not True:
                blockers.append("DO7 is not explicitly on")
        if blockers:
            raise Refused("relay preflight refused: " + "; ".join(blockers))
        result["before"] = before
        if not args.execute:
            result.update({
                "outcome": "relay_staged",
                "message": "Relay request staged; no robot command was sent.",
            })
        else:
            response = client.request(RELAY_API, RELAY_PORT, {
                "id": RELAY_DO_ID,
                "status": args.enable,
            }, mutation=True)
            if not successful_relay_response(response):
                raise Refused("6001 did not return an explicit successful ret_code")
            result["relay_response"] = response
            observations = []
            deadline = time.monotonic() + args.observe_seconds
            while time.monotonic() < deadline:
                current_state, current_battery = snapshot(client)
                sample = summary(current_state, current_battery)
                sample["host_utc"] = utc_now()
                # Preserve repeated equal readings: two distinct received samples
                # are evidence, while one transient positive spike is not.
                observations.append(sample)
                if not observation_safe(sample):
                    break
                if (args.enable and len(observations) >= 2
                        and positive_charge(observations[-2]) and positive_charge(sample)
                        and fresh_pair(observations[-2], sample)):
                    break
                if not args.enable and current_stopped(sample):
                    break
                time.sleep(1.0)
            evidence.write_json("observations.json", observations)
            result["observations"] = observations
            final_state, final_battery = snapshot(client)
            result["final"] = {"host_utc": utc_now(), **summary(final_state, final_battery)}
            result["outcome"], result["charging_test_passed"] = relay_observation_result(
                observations, result["final"], args.enable)
            result["message"] = (
                "DO7 was commanded once. Two consecutive fresh positive-current and charging-state observations, plus a final check, confirm electrical charging."
                if result["outcome"] == "charging_current_confirmed" else
                "Relay command was sent once; inspect the preserved response and observations before any retry."
            )
    except ResultUnknown as exc:
        result.update({"outcome": "unknown", "error": str(exc),
                       "automatic_resend_attempted": False})
    except Exception as exc:
        result.update({"outcome": "unknown" if "relay_response" in result else "refused_or_failed", "error": str(exc),
                       "automatic_resend_attempted": False})
    result["evidence_directory"] = str(evidence.directory)
    result["host_utc"] = utc_now()
    evidence.write_json("result.json", result)
    print(__import__("json").dumps(result, ensure_ascii=True, indent=2, allow_nan=False))
    return 0 if result["outcome"] in (
        "relay_staged", "charging_current_confirmed", "relay_off_current_stopped") else (
        3 if result["outcome"] == "unknown" else 2)


if __name__ == "__main__":
    sys.exit(main())
