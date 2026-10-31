#!/usr/bin/env python3
"""Send one observed SEER charger auto-mode handshake without toggling DO7."""
import argparse
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
import socket
import struct
import sys
import time
import uuid

from probe_runner import Evidence, Refused, ResultUnknown, Robokit, ipv4, utc_now


HEADER = struct.Struct("!BBHLH6s")
REQUEST_API = 1001
RESPONSE_API = REQUEST_API + 10000
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


def classify_exchange(record, require_auto_status=False):
    """Interpret only observed reply fields; never infer contact or charging.

    Port 20207 has returned a small acknowledgement without station status.
    Port 20204 has returned the station snapshot. Unknown enum values such as
    state/state_machine are retained as diagnostics, not guessed into success.
    """
    unknown = []
    attention = []
    response = record.get("response")
    if record.get("error") or record.get("read_error"):
        unknown.append(str(record.get("error") or record.get("read_error")))
    if (type(record.get("response_api")) is not int
            or record["response_api"] != RESPONSE_API
            or type(record.get("response_sequence")) is not int
            or record["response_sequence"] != 1):
        unknown.append("response API or sequence does not match the one-shot request")
    if not isinstance(response, dict):
        unknown.append("no complete JSON object response was received")
        response = {}
    code = response.get("ret_code")
    if type(code) is not int:
        unknown.append("response has no explicit integer ret_code")
    elif code != 0:
        attention.append("station rejected the request with ret_code=" + str(code))
    if "ret" in response:
        if response["ret"] is False:
            attention.append("station returned ret=false")
        elif response["ret"] is not True:
            unknown.append("station acknowledgement ret is not boolean")
    alarms = response.get("alarms")
    if alarms is not None:
        if not isinstance(alarms, dict):
            unknown.append("station alarms have an unknown shape")
        else:
            for key in ("errors", "fatals"):
                if key not in alarms:
                    continue
                if not isinstance(alarms[key], list):
                    unknown.append("station alarms." + key + " is not an array")
                elif alarms[key]:
                    attention.append("station alarms." + key + " is not empty")
    # A known rejection/alarm need not include the success-only status
    # snapshot. Retain that diagnosis rather than labelling it a timeout.
    require_auto_status = require_auto_status and not attention
    status = response.get("status")
    if isinstance(status, dict):
        if status.get("is_emc") is True:
            attention.append("station reports its emergency input active")
        if status.get("is_manual") is True:
            attention.append("station reports manual mode")
        if "is_auto" in status and status["is_auto"] is False:
            attention.append("station snapshot does not confirm automatic mode")
        elif require_auto_status and not attention and status.get("is_auto") is not True:
            unknown.append("station snapshot has no explicit is_auto=true")
    elif require_auto_status:
        unknown.append("station status snapshot is missing or malformed")
    return {
        "outcome": "unknown" if unknown else "attention_required" if attention else "acknowledged",
        "unknown_reasons": unknown,
        "attention_reasons": attention,
        "station_state": response.get("state"),
        "station_status": status,
        "station_alarms": alarms,
        "charging_test_passed": False,
    }


def send_handshake_once(address, packet, evidence):
    exchanges = []
    assessments = []
    for port in (20207, 20204):
        record = exchange(address, port, packet, evidence, "port_" + str(port))
        exchanges.append(record)
        assessment = classify_exchange(record, require_auto_status=port == 20204)
        assessments.append({"port": port, **assessment})
        if assessment["outcome"] != "acknowledged":
            # An unknown/rejected first send must not be followed by another
            # application command on the second port. No retries or cleanup
            # writes are implied by returning this result.
            return {
                "outcome": ("unknown" if assessment["outcome"] == "unknown"
                            else "handshake_attention_required"),
                "exchanges": exchanges, "exchange_assessments": assessments,
                "automatic_resend_attempted": False,
                "message": "Station handshake not confirmed; preserve the original responses and do not resend. "
                           + "; ".join(assessment["unknown_reasons"] + assessment["attention_reasons"]),
            }
    return {
        "outcome": "handshake_acknowledged", "exchanges": exchanges,
        "exchange_assessments": assessments, "automatic_resend_attempted": False,
        "message": "Both one-shot replies were acknowledged and the station snapshot reports automatic mode. "
                   "Contact, brush extension and electrical charging remain unverified; DO7 was not commanded.",
    }


def battery_snapshot(client):
    state = client.request(1100, 19204, {})
    battery = client.request(1007, 19204, {})
    return state, battery


def do7_is_confirmed_off(state):
    outputs = state.get("DO")
    if not isinstance(outputs, list):
        return False
    matching = [row for row in outputs if isinstance(row, dict)
                and type(row.get("id")) is int and row["id"] == 7]
    return len(matching) == 1 and matching[0].get("status") is False


def state_summary(state):
    return {key: state.get(key) for key in (
        "x", "y", "angle", "confidence", "task_status", "task_id", "script_status",
        "unfinished_path", "is_stop", "chassisIsStop", "charging", "manual_charge",
        "auto_charge", "blocked", "emergency", "errors", "fatals", "current_lock", "DO")}


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


def effective_limits(battery, requested_voltage=None, requested_current=None):
    values = []
    for key, requested, label in (
            ("max_charge_voltage", requested_voltage, "voltage"),
            ("max_charge_current", requested_current, "current")):
        bms = battery.get(key)
        if isinstance(bms, bool) or not isinstance(bms, (int, float)):
            raise Refused("BMS " + key + " is missing or non-numeric")
        bms = float(bms)
        if not math.isfinite(bms) or bms <= 0:
            raise Refused("BMS " + key + " is not a positive finite limit")
        if requested is not None and requested > bms:
            raise Refused("requested charge %s exceeds BMS limit %.3f" % (label, bms))
        values.append(requested if requested is not None else bms)
    return values[0], values[1]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--charger", type=ipv4, default="192.168.98.11")
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--max-voltage", type=float)
    parser.add_argument("--max-current", type=float)
    parser.add_argument("--observe-seconds", type=int, default=20)
    parser.add_argument("--execute", action="store_true",
                        help="send the one-shot request; without this flag only build evidence")
    args = parser.parse_args(argv)
    if args.max_voltage is not None and not 50.0 <= args.max_voltage <= 60.0:
        parser.error("--max-voltage must be between 50 and 60 V")
    if args.max_current is not None and not 1.0 <= args.max_current <= 30.0:
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
        result["before"] = {"state": state_summary(state), "battery": battery_summary(battery)}
        blockers = []
        if not do7_is_confirmed_off(state):
            blockers.append("DO7 is not uniquely confirmed off before station handshake")
        if state.get("is_stop") is not True or state.get("chassisIsStop") is not True:
            blockers.append("vehicle is not confirmed stopped")
        if state.get("manual_charge") is not False or battery.get("manual_charge") is not False:
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
        voltage, current = effective_limits(
            battery, args.max_voltage, args.max_current)
        payload = build_payload(battery, voltage, current)
        packet = request_packet(payload)
        evidence.write_json("request.json", payload)
        evidence.write("request.bin", packet)
        result.update({
            "before": {"state": state_summary(state), "battery": battery_summary(battery)},
            "effective_limits": {"max_voltage": voltage, "max_current": current},
            "request": payload, "request_sha256": hashlib.sha256(packet).hexdigest(),
        })
        if not args.execute:
            result.update({"outcome": "handshake_staged",
                           "message": "Request built; no charger connection or application data was sent."})
        else:
            result.update(send_handshake_once(args.charger, packet, evidence))
            if result["outcome"] == "handshake_acknowledged":
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
                    result["message"] = "Robot protection or error was observed after the handshake; charging is not verified."
    except ResultUnknown as exc:
        result.update({"outcome": "unknown", "error": str(exc),
                       "automatic_resend_attempted": False})
    except Exception as exc:
        result.update({"outcome": ("unknown" if result["outcome"] == "handshake_acknowledged"
                                   else "refused_or_failed"), "error": str(exc),
                       "automatic_resend_attempted": False})
    result["evidence_directory"] = str(evidence.directory)
    result["host_utc"] = utc_now()
    evidence.write_json("result.json", result)
    print(json.dumps(result, ensure_ascii=True, indent=2, allow_nan=False))
    return 0 if result["outcome"] in ("handshake_staged", "handshake_acknowledged") else (
        3 if result["outcome"] == "unknown" else 2)


if __name__ == "__main__":
    sys.exit(main())
