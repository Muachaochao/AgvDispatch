#!/usr/bin/env python3
"""Perform one guarded DO7 transition and observe the robot."""
import argparse
import datetime as dt
import math
from pathlib import Path
import sys
import time
import uuid

from probe_runner import Evidence, Refused, ResultUnknown, Robokit, ipv4, utc_now


RELAY_API = 6001
RELAY_PORT = 19210
RELAY_DO_ID = 7


def snapshot(client):
    state = client.request(1100, 19204, {})
    battery = client.request(1007, 19204, {})
    return state, battery


def summary(state, battery):
    do7 = next((row.get("status") for row in state.get("DO", [])
                if isinstance(row, dict) and row.get("id") == 7), None)
    return {
        "state": {key: state.get(key) for key in (
            "is_stop", "chassisIsStop", "charging", "manual_charge",
            "auto_charge", "blocked", "emergency", "errors", "fatals")},
        "do7": do7,
        "battery": {key: battery.get(key) for key in (
            "battery_level", "battery_temp", "charging", "manual_charge",
            "auto_charge", "current", "voltage")},
    }


def successful_relay_response(value):
    return (isinstance(value, dict)
            and type(value.get("ret_code")) is int
            and value["ret_code"] == 0)


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
    }
    try:
        info = client.request(1000, 19204, {})
        if str(info.get("id", "")).casefold() != args.expected_id.casefold():
            raise Refused("robot hardware ID mismatch")
        state, battery = snapshot(client)
        before = summary(state, battery)
        blockers = []
        if args.enable:
            if state.get("is_stop") is not True or state.get("chassisIsStop") is not True:
                blockers.append("vehicle is not confirmed stopped")
            if state.get("manual_charge") is not False:
                blockers.append("manual charge signal is active")
            if state.get("charging") is not False or battery.get("charging") is not False:
                blockers.append("vehicle is already charging")
            if state.get("blocked") is not False or state.get("emergency") is not False:
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
            previous = None
            charging_seen = False
            stopped_seen = False
            attention = False
            deadline = time.monotonic() + args.observe_seconds
            while time.monotonic() < deadline:
                current_state, current_battery = snapshot(client)
                sample = summary(current_state, current_battery)
                sample["host_utc"] = utc_now()
                key = (
                    sample["do7"], sample["battery"]["charging"], sample["battery"]["current"],
                    sample["battery"]["voltage"], sample["state"]["blocked"],
                    sample["state"]["emergency"], tuple(sample["state"]["errors"] or []),
                    tuple(sample["state"]["fatals"] or []),
                )
                if key != previous:
                    observations.append(sample)
                    previous = key
                current = sample["battery"].get("current")
                if isinstance(current, (int, float)) and current > 0.1:
                    charging_seen = True
                if (current is not None and current <= 0.1
                        and sample["battery"].get("charging") is False):
                    stopped_seen = True
                if (sample["state"].get("blocked") is True
                        or sample["state"].get("emergency") is True
                        or sample["state"].get("errors")
                        or sample["state"].get("fatals")):
                    attention = True
                    break
                if args.enable and charging_seen:
                    # Confirm charging with a second positive-current sample.
                    time.sleep(1.0)
                    state2, battery2 = snapshot(client)
                    current2 = battery2.get("current")
                    if isinstance(current2, (int, float)) and current2 > 0.1:
                        observations.append({"host_utc": utc_now(), **summary(state2, battery2)})
                        break
                if not args.enable and stopped_seen:
                    break
                time.sleep(1.0)
            evidence.write_json("observations.json", observations)
            result["observations"] = observations
            final_state, final_battery = snapshot(client)
            result["final"] = summary(final_state, final_battery)
            if attention:
                result["outcome"] = "relay_attention_required"
            elif args.enable:
                result["outcome"] = "charging_current_confirmed" if charging_seen else "relay_on_current_not_observed"
            else:
                result["outcome"] = "relay_off_current_stopped" if stopped_seen else "relay_off_current_not_confirmed"
            result["charging_test_passed"] = bool(args.enable and charging_seen and not attention)
            result["message"] = (
                "DO7 was commanded once. Positive current confirms electrical charging."
                if result["outcome"] == "charging_current_confirmed" else
                "Relay command was sent once; inspect the preserved response and observations before any retry."
            )
    except ResultUnknown as exc:
        result.update({"outcome": "unknown", "error": str(exc),
                       "automatic_resend_attempted": False})
    except Exception as exc:
        result.update({"outcome": "refused_or_failed", "error": str(exc),
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
