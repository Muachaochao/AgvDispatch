#!/usr/bin/env python3
"""PC-side, standard-library runner for the stationary recognition probe only."""
import argparse
import datetime as dt
import hashlib
import ipaddress
import json
from pathlib import Path
import socket
import struct
import sys
import time
import uuid


HEADER = struct.Struct("!BBHIH6s")
STATUS_PORT, NAV_PORT, CONFIG_PORT = 19204, 19206, 19207
SOCKET_TIMEOUT = 3.0
MAX_RESPONSE_BYTES = 8 * 1024 * 1024
PROBE_MARKER = "charge_dock_probe_v1"
RECOGNITION_FILE = "charger/c0001.charger"


class Refused(RuntimeError):
    """A prerequisite or an explicit robot response rejected this operation."""


class ResultUnknown(RuntimeError):
    """A mutation might have arrived; the runner must not send it again."""


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def json_bytes(value):
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"),
                      allow_nan=False).encode("utf-8")


def ipv4(value):
    try:
        return str(ipaddress.IPv4Address(value))
    except ipaddress.AddressValueError as exc:
        raise argparse.ArgumentTypeError("an IPv4 address is required") from exc


class Evidence:
    def __init__(self, root, run_id):
        self.directory = None
        self.index = 0
        if root is not None:
            self.directory = root.resolve() / run_id
            self.directory.mkdir(parents=True, exist_ok=False)

    def write(self, name, data):
        if self.directory is not None:
            # A failed evidence write prevents the following network operation.
            with (self.directory / name).open("xb") as stream:
                stream.write(data)
                stream.flush()
                import os
                os.fsync(stream.fileno())

    def write_json(self, name, value):
        self.write(name, json_bytes(value))

    def begin(self, api, port, packet, payload, mutation):
        self.index += 1
        stem = "%03d_%d" % (self.index, api)
        self.write(stem + ".request.bin", packet)
        self.write_json(stem + ".request.json", payload)
        self.write_json(stem + ".intent.json", {
            "host_utc": utc_now(), "api": api, "port": port,
            "mutation": mutation, "request_sha256": hashlib.sha256(packet).hexdigest(),
            "policy": "one attempt only; do not resend after an unknown result",
        })
        return stem


class Robokit:
    def __init__(self, robot, evidence):
        self.robot = robot
        self.evidence = evidence
        self.sequence = uuid.uuid4().int & 0xFFFF

    @staticmethod
    def remaining(deadline):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("request deadline expired")
        return remaining

    def receive(self, stream, count, deadline):
        result = bytearray()
        while len(result) < count:
            stream.settimeout(self.remaining(deadline))
            block = stream.recv(count - len(result))
            if not block:
                raise ConnectionError("connection closed before the full response arrived")
            result.extend(block)
        return bytes(result)

    def request(self, api, port, payload, *, mutation=False, raw=False, deadline=None):
        self.sequence = (self.sequence + 1) & 0xFFFF
        body = json_bytes(payload)
        packet = HEADER.pack(0x5A, 1, self.sequence, len(body), api, b"\0" * 6) + body
        # Exact bytes and the intent are durably recorded before ANY mutation.
        stem = self.evidence.begin(api, port, packet, payload, mutation)
        request_deadline = time.monotonic() + SOCKET_TIMEOUT
        if deadline is not None:
            request_deadline = min(request_deadline, deadline)
        try:
            with socket.create_connection((self.robot, port),
                                          timeout=self.remaining(request_deadline)) as stream:
                stream.settimeout(self.remaining(request_deadline))
                stream.sendall(packet)
                header = self.receive(stream, HEADER.size, request_deadline)
                self.evidence.write(stem + ".response_header.bin", header)
                magic, version, sequence, length, response_api, reserved = HEADER.unpack(header)
                if (magic != 0x5A or version != 1 or sequence != self.sequence
                        or response_api != api + 10000):
                    raise ValueError("response header/type/sequence does not match the request")
                if length > MAX_RESPONSE_BYTES:
                    raise ValueError("response length exceeds the bounded read limit")
                response_body = self.receive(stream, length, request_deadline)
            self.evidence.write(stem + ".response.bin", header + response_body)
            self.evidence.write_json(stem + ".received.json", {
                "host_utc": utc_now(), "length": length, "response_api": response_api,
            })
            if raw:
                # File downloads return raw bytes on success and JSON on error.
                try:
                    possible_error = json.loads(response_body)
                except (ValueError, UnicodeError):
                    possible_error = None
                if isinstance(possible_error, dict) and possible_error.get("ret_code", 0) != 0:
                    raise Refused("API %d: %s" % (api, possible_error))
                return response_body
            response = json.loads(response_body)
            if not isinstance(response, dict):
                raise ValueError("response JSON must be an object")
            if response.get("ret_code", 0) != 0:
                raise Refused("API %d: %s" % (api, response))
            if mutation and (type(response.get("ret_code")) is not int
                             or response["ret_code"] != 0):
                raise ValueError("mutation response has no explicit successful ret_code")
            return response
        except Refused:
            raise
        except Exception as exc:
            if mutation:
                raise ResultUnknown("API %d result unknown; no resend was attempted: %s"
                                    % (api, exc)) from exc
            raise


RUNTIME_KEYS = (
    "current_ip", "current_map", "current_map_md5", "current_station", "x", "y", "angle",
    "confidence", "task_status", "task_id", "unfinished_path", "script_status", "script_res",
    "move_status_info",
    "is_stop", "chassisIsStop", "charging", "manual_charge", "emergency", "soft_emc",
    "blocked", "errors", "fatals", "current_lock",
    "dispatch_mode",
)


def runtime_summary(runtime):
    return {key: runtime[key] for key in RUNTIME_KEYS if key in runtime}


def identity_summary(info):
    keys = ("id", "vehicle_id", "model", "version", "SRC_NAME", "current_ip",
            "current_map", "current_map_md5")
    return {key: info[key] for key in keys if key in info}


def preflight(client, expected_id):
    # Both are new request/response reads. Remote create_on is not used as a clock.
    info = client.request(1000, STATUS_PORT, {})
    actual = info.get("id")
    if not isinstance(actual, str) or actual.casefold() != expected_id.casefold():
        raise Refused("robot hardware ID mismatch: expected %r, received %r" % (expected_id, actual))
    state = client.request(1100, STATUS_PORT, {})
    reasons = []
    status, task_id = state.get("task_status"), state.get("task_id")
    prior_terminal_probe = (type(status) is int and status in (5, 6, 7)
                            and isinstance(task_id, str)
                            and task_id.startswith("charge-recognition-")
                            and len(task_id) > len("charge-recognition-"))
    if (type(status) is not int or status not in (0, 4)) and not prior_terminal_probe:
        reasons.append("task_status must be idle/completed or a confirmed terminal recognition probe")
    if type(state.get("script_status")) is not int or state["script_status"] != 0:
        reasons.append("script_status must be idle(0)")
    if state.get("unfinished_path") != []:
        reasons.append("unfinished_path must be an empty array")
    if task_id != "" and not prior_terminal_probe:
        reasons.append("task_id must be empty")
    if state.get("is_stop") is not True:
        reasons.append("is_stop must explicitly be true")
    for key in ("charging", "manual_charge", "emergency"):
        if state.get(key) is not False:
            reasons.append(key + " must explicitly be false")
    for key in ("soft_emc",):
        if key in state and state[key] is not False:
            reasons.append(key + " is active or invalid")
    if "chassisIsStop" in state and state["chassisIsStop"] is not True:
        reasons.append("chassisIsStop is not true")
    for key in ("errors", "fatals"):
        if key in state and state[key] != []:
            reasons.append(key + " is not empty")
    lock = state.get("current_lock")
    if not isinstance(lock, dict) or lock.get("locked") is not False:
        reasons.append("control ownership must explicitly be released by its current owner")
    if type(state.get("dispatch_mode")) is not int or state["dispatch_mode"] != 0:
        reasons.append("dispatch_mode must explicitly be standalone(0)")
    if reasons:
        raise Refused("probe preflight refused: " + "; ".join(reasons))
    confirmed_prior = None
    if prior_terminal_probe:
        try:
            response = client.request(1110, STATUS_PORT, {"task_ids": [task_id]})
            package = response.get("task_status_package", response)
            rows = package.get("task_status_list") if isinstance(package, dict) else None
            if (not isinstance(rows, list) or len(rows) != 1 or not isinstance(rows[0], dict)
                    or rows[0].get("task_id") != task_id
                    or type(rows[0].get("status")) is not int or rows[0]["status"] != status):
                raise ValueError("the prior probe is missing, ambiguous, active, or has a different status")
            confirmed_prior = {"task_id": task_id, "status": status}
        except Exception as exc:
            raise Refused("prior recognition probe terminal state was not confirmed by fresh 1110: "
                          + str(exc)) from exc
    return {"identity": identity_summary(info), "runtime": runtime_summary(state),
            "confirmed_prior_probe": confirmed_prior}


def charger_ports(address, evidence):
    result = {"ip": address, "application_bytes_sent": 0, "ports": {}}
    for port in (20207, 20204):
        try:
            with socket.create_connection((address, port), timeout=SOCKET_TIMEOUT):
                result["ports"][str(port)] = {"connected": True}
        except OSError as exc:
            result["ports"][str(port)] = {"connected": False, "error": str(exc)}
    evidence.write_json("charger_connections.json", result)
    return result


def inspect_robot(client, args):
    result = {"outcome": "inspection_complete", "robot": args.robot,
              "charging_test_passed": False, "mutations_sent": False}
    requests = (
        ("identity", 1000, {}), ("runtime", 1100, {}), ("battery", 1007, {}),
        ("module_files", 1506, {"type": "module"}),
        ("charger_files", 1798, {"type": "users", "path": "resources/objects/charger"}),
    )
    for name, api, payload in requests:
        try:
            value = client.request(api, STATUS_PORT, payload)
            if name == "identity":
                if args.expected_id and value.get("id", "").casefold() != args.expected_id.casefold():
                    raise Refused("hardware ID does not match --expected-id")
                value = identity_summary(value)
            elif name == "runtime":
                value = runtime_summary(value)
            result[name] = value
        except Exception as exc:
            result[name] = {"error": str(exc)}
            result["outcome"] = "inspection_has_errors"
    if args.charger:
        result["charger_connectivity_only"] = charger_ports(args.charger, client.evidence)
    return result


def matching_report(value, run_id):
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except ValueError:
            return None
    if (isinstance(value, dict) and value.get("probe") == PROBE_MARKER
            and value.get("run_id") == run_id):
        return value
    return None


def report_from_runtime(state, run_id):
    # On this firmware r.setInfo is exposed in move_status_info, not script_res.
    # A channel is usable only when its report matches this probe AND this run.
    for channel in ("move_status_info", "script_res"):
        report = matching_report(state.get(channel), run_id)
        if report is not None:
            return channel, report
    return None, None


def track_probe(client, task_id, run_id):
    deadline = time.monotonic() + 30.0
    latest_report, latest_state, task_status = None, None, None
    latest_report_source = None
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
            if time.monotonic() >= deadline:
                break
            latest_state = client.request(1100, STATUS_PORT, {}, deadline=deadline)
            report_source, report = report_from_runtime(latest_state, run_id)
            observations.append({"host_utc": utc_now(), "task_status": task_status,
                                 "script_status": latest_state.get("script_status"),
                                 "script_res": latest_state.get("script_res"),
                                 "move_status_info": latest_state.get("move_status_info"),
                                 "matched_report_source": report_source})
            if report is not None:
                latest_report = report
                latest_report_source = report_source
            if task_status in (5, 6, 7):
                outcome = "probe_task_failed"
                break
            if task_status == 4 and latest_report is not None:
                if (latest_report.get("phase") == "completed" and latest_report.get("status") == 3
                        and latest_report.get("error") is None
                        and latest_report.get("motion_commanded") is False
                        and latest_report.get("charger_commanded") is False
                        and latest_report.get("recognition_file") == RECOGNITION_FILE
                        and isinstance(latest_report.get("samples"), list)
                        and len(latest_report["samples"]) == 3):
                    outcome = "recognition_probe_completed"
                    break
                if latest_report.get("phase") in ("failed", "cancelled", "suspended"):
                    outcome = "probe_report_failed"
                    break
        except Exception as exc:
            # Tracking reads may fail; no upload, task send, stop or takeover follows.
            errors.append({"host_utc": utc_now(), "error": str(exc)})
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(0.5, remaining))
    client.evidence.write_json("tracking_script_res.json", observations)
    return {
        "outcome": outcome, "task_id": task_id, "task_status": task_status,
        "report": latest_report, "report_source": latest_report_source,
        "last_runtime": runtime_summary(latest_state) if latest_state else None,
        "tracking_errors": errors, "charging_test_passed": False,
        "message": ("Only stationary target recognition was tested; docking and charging were not commanded."
                    if outcome == "recognition_probe_completed" else
                    "No automatic resend, stop or control takeover was attempted. Inspect the evidence before another explicit test."),
    }


def run_probe(client, args, run_id):
    if client.evidence.directory is None:
        raise Refused("probe requires a durable evidence directory")
    source = Path(__file__).with_name("charge_dock_probe.py").read_bytes()
    source_text = source.decode("utf-8")
    digest = hashlib.sha256(source).hexdigest()
    relative_path = "charge_dock_probe_%s.py" % digest[:12]
    client.evidence.write("probe_source.py", source)
    client.evidence.write_json("run.json", {
        "run_id": run_id, "robot": args.robot, "expected_id": args.expected_id,
        "relative_path": relative_path, "source_sha256": digest,
        "recognition_file": RECOGNITION_FILE, "motion_and_charging_enabled": False,
    })
    before = preflight(client, args.expected_id)
    client.evidence.write_json("preflight_before_upload.json", before)
    if args.charger:
        charger_ports(args.charger, client.evidence)
    listing = client.request(1506, STATUS_PORT, {"type": "module"})
    entries = listing.get("list")
    if not isinstance(entries, list) or any(not isinstance(row, dict) for row in entries):
        raise Refused("script list is missing or malformed; cannot establish a unique upload path")
    existing = [row for row in entries if row.get("relative_path") == relative_path]
    if len(existing) > 1:
        raise Refused("duplicate script entries for the content-addressed module")
    if existing:
        saved = client.request(4022, CONFIG_PORT,
                               {"type": "module", "relative_path": relative_path}, raw=True)
        if saved != source:
            raise Refused("existing script bytes differ; no existing script was overwritten")
    else:
        client.request(4021, CONFIG_PORT, {"type": "module", "relative_path": relative_path,
                                         "script": source_text}, mutation=True)
    verified = client.request(4022, CONFIG_PORT,
                              {"type": "module", "relative_path": relative_path}, raw=True)
    if verified != source:
        raise Refused("downloaded script bytes differ; execution was not sent")
    fresh = preflight(client, args.expected_id)
    client.evidence.write_json("preflight_before_execute.json", fresh)
    task_id = "charge-recognition-" + run_id
    payload = {"move_task_list": [{
        "id": "SELF_POSITION", "source_id": "SELF_POSITION", "task_id": task_id,
        "operation": "Script", "script_name": relative_path, "script_stage": 2,
        "script_args": {"operation": "RecognizeOnly", "recognition_file": RECOGNITION_FILE,
                        "samples": 3, "timeout_s": 20.0, "run_id": run_id},
    }]}
    client.request(3066, NAV_PORT, payload, mutation=True)
    result = track_probe(client, task_id, run_id)
    result.update({"robot": args.robot, "run_id": run_id, "script": relative_path,
                   "source_sha256": digest, "initial_state": before})
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", nargs="?", choices=("inspect", "probe"), default="inspect")
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", help="required for probe: robot hardware ID returned by API 1000")
    parser.add_argument("--charger", type=ipv4, help="connect-only port checks; sends no charger application data")
    parser.add_argument("--evidence-dir", type=Path, help="required for probe; creates a unique run subdirectory")
    args = parser.parse_args(argv)
    if args.command == "probe" and (not args.expected_id or not args.expected_id.strip() or args.evidence_dir is None):
        parser.error("probe requires --expected-id and --evidence-dir")
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(args.evidence_dir, run_id)
    client = Robokit(args.robot, evidence)
    try:
        result = run_probe(client, args, run_id) if args.command == "probe" else inspect_robot(client, args)
    except ResultUnknown as exc:
        result = {"outcome": "unknown", "error": str(exc), "charging_test_passed": False,
                  "automatic_resend_attempted": False}
    except Exception as exc:
        result = {"outcome": "refused_or_failed", "error": str(exc), "charging_test_passed": False,
                  "automatic_resend_attempted": False}
    result["evidence_directory"] = str(evidence.directory) if evidence.directory else None
    result["host_utc"] = utc_now()
    evidence.write_json("result.json", result)
    print(json.dumps(result, ensure_ascii=True, indent=2, allow_nan=False))
    if result["outcome"] in ("recognition_probe_completed", "inspection_complete"):
        return 0
    return 3 if result["outcome"] == "unknown" else 2


if __name__ == "__main__":
    sys.exit(main())
