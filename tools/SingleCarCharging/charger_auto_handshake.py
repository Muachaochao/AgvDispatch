#!/usr/bin/env python3
"""Send one observed SEER charger auto-mode handshake without toggling DO7."""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import socket
import struct
import sys
import time
import uuid

from probe_runner import Evidence, Refused, ResultUnknown, Robokit, ipv4, utc_now


HEADER = struct.Struct("!BBHLH6s")
REQUEST_API = 1001
MAX_RESPONSE_BYTES = 1024 * 1024
SOCKET_TIMEOUT = 5.0


def request_packet(payload, sequence=1):
    body = json.dumps(payload, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    return HEADER.pack(0x5A, 1, sequence, len(body), REQUEST_API, b"\0" * 6) + body


def receive_exact(stream, count, deadline):
    result = bytearray()
    while len(result) < count:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("charger response deadline expired")
        stream.settimeout(remaining)
        block = stream.recv(count - len(result))
        if not block:
            raise ConnectionError("charger closed the connection before a complete frame arrived")
        result.extend(block)
    return bytes(result)


def exchange(address, port, packet, evidence, label):
    record = {
        "host_utc": utc_now(), "port": port,
        "request_sha256": hashlib.sha256(packet).hexdigest(),
        "request_bytes": len(packet), "response": None,
    }
    deadline = time.monotonic() + SOCKET_TIMEOUT
    try:
        with socket.create_connection((address, port), timeout=SOCKET_TIMEOUT) as stream:
            stream.sendall(packet)
            try:
                header = receive_exact(stream, HEADER.size, deadline)
                magic, version, sequence, length, response_api, reserved = HEADER.unpack(header)
                if magic != 0x5A or version != 1:
                    raise ValueError("charger response header magic/version is invalid")
                if sequence != 1:
                    raise ValueError("charger response sequence differs from the one-shot request")
                if length > MAX_RESPONSE_BYTES:
                    raise ValueError("charger response exceeds the bounded read limit")
                body = receive_exact(stream, length, deadline)
                record.update({
                    "response_api": response_api,
                    "response_sequence": sequence,
                    "response_body_hex": body.hex(),
                    "response_raw_sha256": hashlib.sha256(header + body).hexdigest(),
                })
                try:
                    record["response"] = json.loads(body.decode("utf-8"))
                except (UnicodeError, ValueError):
                    record["response"] = None
            except socket.timeout:
                record["read_error"] = "charger accepted the request but returned no complete frame"
    except Exception as exc:
        record["error"] = str(exc)
    evidence.write_json(label + ".json", record)
    return record


def battery_snapshot(client):
    state = client.request(1100, 19204, {})
    battery = client.request(1007, 19204, {})
    return state, battery


def state_summary(state):
    return {key: state.get(key) for key in (
        "x", "y", "angle", "confidence", "task_status", "task_id", "script_status",
        "unfinished_path", "is_stop", "chassisIsStop", "charging", "manual_charge",
        "auto_charge", "blocked", "emergency", "errors", "fatals", "current_lock")}


def battery_summary(battery):
    return {key: battery.get(key) for key in (
        "battery_level", "battery_temp", "charging", "manual_charge", "auto_charge",
        "current", "voltage", "max_charge_current", "max_charge_voltage", "ret_code")}


def build_payload(battery, voltage, current):
    return {
        "input": {
            "is_auto": True,
            "charging_types": "ProtocolCharging",
            "max_charge_voltage": voltage,
            "max_charge_current": current,
        },
        "status": {
            "battery_percent": battery.get("battery_level"),
            "battery_temp": battery.get("battery_temp"),
            "battery_current": battery.get("current"),
            "battery_voltage": battery.get("voltage"),
        },
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--charger", type=ipv4, default="192.168.98.11")
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--max-voltage", type=float, default=58.8)
    parser.add_argument("--max-current", type=float, default=22.0)
    parser.add_argument("--observe-seconds", type=int, default=20)
    parser.add_argument("--execute", action="store_true",
                        help="send the one-shot request; without this flag only build evidence")
    args = parser.parse_args(argv)
    if not 50.0 <= args.max_voltage <= 60.0:
        parser.error("--max-voltage must be between 50 and 60 V")
    if not 1.0 <= args.max_current <= 30.0:
        parser.error("--max-current must be between 1 and 30 A")
    if not 5 <= args.observe_seconds <= 30:
        parser.error("--observe-seconds must be between 5 and 30")

    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(args.evidence_dir, run_id)
    client = Robokit(args.robot, evidence)
    result = {
        "outcome": "not_sent", "run_id": run_id, "robot": args.robot,
        "charger": args.charger, "do7_commanded": False,
        "charging_test_passed": False,
    }
    try:
        info = client.request(1000, 19204, {})
        if str(info.get("id", "")).casefold() != args.expected_id.casefold():
            raise Refused("robot hardware ID mismatch")
        state, battery = battery_snapshot(client)
        blockers = []
        if state.get("is_stop") is not True or state.get("chassisIsStop") is not True:
            blockers.append("vehicle is not confirmed stopped")
        if state.get("manual_charge") is not False:
            blockers.append("manual charger signal is not false")
        if state.get("charging") is not False or battery.get("charging") is not False:
            blockers.append("vehicle charging state is not false")
        if state.get("auto_charge") is not False or battery.get("auto_charge") is not False:
            blockers.append("auto-charge state is not false before the handshake")
        if state.get("blocked") is not False or state.get("emergency") is not False:
            blockers.append("vehicle is blocked or in emergency")
        if state.get("errors") != [] or state.get("fatals") != []:
            blockers.append("vehicle reports errors")
        if blockers:
            raise Refused("; ".join(blockers))
        payload = build_payload(battery, args.max_voltage, args.max_current)
        packet = request_packet(payload)
        evidence.write_json("request.json", payload)
        evidence.write("request.bin", packet)
        result.update({
            "before": {"state": state_summary(state), "battery": battery_summary(battery)},
            "request": payload, "request_sha256": hashlib.sha256(packet).hexdigest(),
        })
        if not args.execute:
            result.update({"outcome": "handshake_staged",
                           "message": "Request built; no charger connection or application data was sent."})
        else:
            # Both ports receive the same one-shot request in the vendor reference.
            first = exchange(args.charger, 20207, packet, evidence, "port_20207")
            second = exchange(args.charger, 20204, packet, evidence, "port_20204")
            result["exchanges"] = [first, second]
            result["outcome"] = "handshake_sent"
            observations = []
            previous = None
            deadline = time.monotonic() + args.observe_seconds
            while time.monotonic() < deadline:
                current_state, current_battery = battery_snapshot(client)
                sample = {
                    "host_utc": utc_now(),
                    "battery_level": current_battery.get("battery_level"),
                    "battery_temp": current_battery.get("battery_temp"),
                    "charging": current_battery.get("charging"),
                    "manual_charge": current_battery.get("manual_charge"),
                    "auto_charge": current_battery.get("auto_charge"),
                    "current": current_battery.get("current"),
                    "voltage": current_battery.get("voltage"),
                    "blocked": current_state.get("blocked"),
                    "emergency": current_state.get("emergency"),
                    "errors": current_state.get("errors"),
                    "fatals": current_state.get("fatals"),
                }
                key = tuple(sample[k] for k in
                            ("charging", "manual_charge", "auto_charge", "current", "voltage",
                             "blocked", "emergency"))
                if key != previous:
                    observations.append(sample)
                    previous = key
                time.sleep(1.0)
            evidence.write_json("observations.json", observations)
            result["observations"] = observations
            final_state, final_battery = battery_snapshot(client)
            result["final"] = {
                "state": state_summary(final_state),
                "battery": battery_summary(final_battery),
            }
            if any((sample.get("emergency") is True or sample.get("blocked") is True
                    or sample.get("errors") or sample.get("fatals"))
                   for sample in observations):
                result["outcome"] = "handshake_attention_required"
            result["message"] = (
                "Handshake sent once to both observed ports; DO7 was not commanded. "
                "Inspect station UI for automatic mode, connection and brush extension.")
    except ResultUnknown as exc:
        result.update({"outcome": "unknown", "error": str(exc),
                       "automatic_resend_attempted": False})
    except Exception as exc:
        result.update({"outcome": "refused_or_failed", "error": str(exc),
                       "automatic_resend_attempted": False})
    result["evidence_directory"] = str(evidence.directory)
    result["host_utc"] = utc_now()
    evidence.write_json("result.json", result)
    print(json.dumps(result, ensure_ascii=True, indent=2, allow_nan=False))
    return 0 if result["outcome"] in ("handshake_staged", "handshake_sent") else (
        3 if result["outcome"] == "unknown" else 2)


if __name__ == "__main__":
    sys.exit(main())
