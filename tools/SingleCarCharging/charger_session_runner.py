#!/usr/bin/env python3
"""Run one guarded charging session with persistent station communication."""
import argparse
import datetime as dt
import json
import math
from pathlib import Path
import socket
import sys
import time
import uuid

from charger_auto_handshake import (
    HEADER,
    MAX_RESPONSE_BYTES,
    REQUEST_API,
    RESPONSE_API,
    battery_snapshot,
    build_payload,
    do7_is_confirmed_off,
    effective_limits,
    request_packet,
    state_summary,
)
from probe_runner import (
    Evidence,
    Refused,
    ResultUnknown,
    Robokit,
    blocking_vehicle_errors,
    ipv4,
    utc_now,
)


CHARGER_PORT_ACK = 20207
CHARGER_PORT_STATUS = 20204
RELAY_API = 6001
RELAY_PORT = 19210
RELAY_DO_ID = 7
MAX_SAMPLE_GAP_SECONDS = 5.0
MAX_SNAPSHOT_SECONDS = 5.0
MAX_STATION_AGE_SECONDS = 5.0
SHUTDOWN_SECONDS = 15.0


def finite_number(value):
    return type(value) in (int, float) and math.isfinite(float(value))


def explicit_success(response):
    return (isinstance(response, dict)
            and type(response.get("ret_code")) is int
            and response["ret_code"] == 0
            and ("ret" not in response or response["ret"] is True))


def receive_exact(stream, count, deadline):
    result = bytearray()
    while len(result) < count:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("charger response deadline expired")
        stream.settimeout(remaining)
        block = stream.recv(count - len(result))
        if not block:
            raise ConnectionError("charger closed the connection")
        result.extend(block)
    return bytes(result)


def exchange_on_open(stream, payload, sequence, evidence, label, timeout=5.0):
    packet = request_packet(payload, sequence)
    evidence.write_json(label + ".intent.json", {
        "host_utc": utc_now(), "sequence": sequence,
        "request_api": REQUEST_API, "request": payload,
        "policy": "persistent session state update; unknown start is not replayed",
    })
    evidence.write(label + ".request.bin", packet)
    deadline = time.monotonic() + timeout
    stream.settimeout(timeout)
    stream.sendall(packet)
    header = receive_exact(stream, HEADER.size, deadline)
    magic, version, response_sequence, length, response_api, _ = HEADER.unpack(header)
    if magic != 0x5A or version != 1:
        raise ValueError("charger response header is invalid")
    if response_sequence != sequence:
        raise ValueError("charger response sequence does not match request")
    if response_api != RESPONSE_API or length > MAX_RESPONSE_BYTES:
        raise ValueError("charger response API or length is invalid")
    body = receive_exact(stream, length, deadline)
    response = json.loads(body.decode("utf-8"))
    evidence.write_json(label, {
        "host_utc": utc_now(),
        "sequence": sequence,
        "request_api": REQUEST_API,
        "request_length": len(packet),
        "response_api": response_api,
        "response": response,
    })
    if not isinstance(response, dict):
        raise ValueError("charger response must be a JSON object")
    return response


def connect_charger(address):
    ack = socket.create_connection((address, CHARGER_PORT_ACK), timeout=5.0)
    try:
        status = socket.create_connection((address, CHARGER_PORT_STATUS), timeout=5.0)
    except Exception:
        ack.close()
        raise
    return ack, status


def station_problem(response, require_status=False):
    if not isinstance(response, dict):
        return "station response is missing"
    if not explicit_success(response):
        return "station did not explicitly acknowledge the request"
    alarms = response.get("alarms")
    if alarms is not None and not isinstance(alarms, dict):
        return "station alarms are malformed"
    if isinstance(alarms, dict):
        for name, values in alarms.items():
            if not isinstance(values, list):
                return "station alarm group %s is malformed" % name
            if values:
                return "station returned %s alarms" % name
    status = response.get("status")
    if require_status and not isinstance(status, dict):
        return "station status is missing or malformed"
    if isinstance(status, dict):
        if (status.get("is_emc") is not False
                or status.get("is_manual") is not False):
            return "station emergency/manual state is active or unknown"
    return None


def do7_state(state):
    outputs = state.get("DO")
    if not isinstance(outputs, list):
        return None
    matches = [item for item in outputs
               if isinstance(item, dict) and type(item.get("id")) is int
               and item["id"] == RELAY_DO_ID]
    return matches[0].get("status") if len(matches) == 1 else None


def warning_signature(state, battery=None):
    warnings = []
    for source in (state.get("warnings"),
                   (battery or {}).get("warnings")):
        if source is None:
            continue
        if not isinstance(source, list):
            warnings.append({"source": "malformed", "value": repr(source)})
            continue
        for item in source:
            if isinstance(item, dict):
                # Device clocks, counters and occurrence timestamps change on
                # every reply. Retain semantic fields and every unknown field.
                item = {key: value for key, value in item.items()
                        if key.casefold() not in {
                            "timestamp", "time", "create_on", "created_on",
                            "first_seen", "last_seen", "count", "counter",
                            "times", "updated_at", "created_at"}}
            warnings.append(item)
    return tuple(sorted(set(json.dumps(item, sort_keys=True, ensure_ascii=True)
                            for item in warnings)))


def safe_sample(state, battery, max_temperature):
    velocities = (state.get("vx"), state.get("vy"), state.get("w"))
    measured_stopped = (
        all(type(value) in (int, float) and math.isfinite(float(value))
            and abs(float(value)) < 0.01 for value in velocities)
    )
    return (
        state.get("chassisIsStop") is True
        and measured_stopped
        and type(state.get("is_stop")) is bool
        and state.get("blocked") is False
        and state.get("emergency") is False
        and state.get("soft_emc", False) is False
        and isinstance(state.get("errors"), list)
        and not blocking_vehicle_errors(state["errors"])
        and isinstance(battery.get("errors", []), list)
        and not blocking_vehicle_errors(battery.get("errors", []))
        and state.get("fatals") == []
        and battery.get("fatals", []) == []
        and isinstance(state.get("warnings"), list)
        and isinstance(battery.get("warnings", []), list)
        and state.get("manual_charge") is False
        and battery.get("manual_charge") is False
        and type(battery.get("battery_temp")) in (int, float)
        and math.isfinite(float(battery["battery_temp"]))
        and float(battery["battery_temp"]) <= max_temperature
    )


def sample_summary(state, battery):
    return {
        "host_utc": utc_now(),
        "do7": do7_state(state),
        "charging": battery.get("charging"),
        "state_charging": state.get("charging"),
        "manual_charge": battery.get("manual_charge"),
        "battery_level": battery.get("battery_level"),
        "voltage": battery.get("voltage"),
        "current": battery.get("current"),
        "temperature": battery.get("battery_temp"),
        "max_charge_voltage": battery.get("max_charge_voltage"),
        "max_charge_current": battery.get("max_charge_current"),
        "is_stop": state.get("is_stop"),
        "vx": state.get("vx"), "vy": state.get("vy"), "w": state.get("w"),
        "chassisIsStop": state.get("chassisIsStop"),
        "blocked": state.get("blocked"),
        "emergency": state.get("emergency"),
        "soft_emc": state.get("soft_emc"),
        "errors": state.get("errors"),
        "warnings": state.get("warnings"),
        "battery_warnings": battery.get("warnings"),
        "fatals": state.get("fatals"),
    }


def session_limits(battery, args):
    # Explicit requests may LOWER current BMS limits. An unknown/zero BMS
    # limit cannot be replaced by a previously tested setting on another car.
    return effective_limits(battery, args.max_voltage, args.max_current), "bms"


def ensure_electrical_limits(battery, limits, tolerances=(0.0, 0.0)):
    voltage, current = battery.get("voltage"), battery.get("current")
    if not finite_number(voltage) or not finite_number(current):
        raise ResultUnknown("battery electrical feedback is missing or non-finite")
    bms_voltage = battery.get("max_charge_voltage")
    bms_current = battery.get("max_charge_current")
    if not finite_number(bms_voltage) or not finite_number(bms_current):
        raise Refused("BMS charge limits are missing or non-finite")
    voltage_tolerance, current_tolerance = tolerances
    # A lower explicit request may tolerate charger control overshoot, but the
    # allowance is always clamped by the live BMS hard limit.
    allowed_voltage = min(
        float(limits[0]) + max(0.0, float(voltage_tolerance)),
        float(bms_voltage))
    allowed_current = min(
        float(limits[1]) + max(0.0, float(current_tolerance)),
        float(bms_current))
    if float(voltage) > allowed_voltage or float(current) > allowed_current:
        raise Refused("measured battery voltage/current exceeds active BMS/request limit")


class PositiveInterval:
    """Track the FINAL fresh uninterrupted interval, never a historical peak."""
    def __init__(self):
        self.started = self.last = None
        self.count = 0
        self.seconds = self.longest = 0.0

    def observe(self, now, positive):
        fresh = (self.last is None or 0 < now - self.last <= MAX_SAMPLE_GAP_SECONDS)
        self.last = now
        if not positive or not fresh:
            self.started = None
            self.count = 0
            self.seconds = 0.0
            return fresh
        if self.started is None:
            self.started = now
        self.count += 1
        self.seconds = now - self.started
        self.longest = max(self.longest, self.seconds)
        return fresh


def positive_charge(state, battery):
    return (do7_state(state) is True and state.get("charging") is True
            and battery.get("charging") is True
            and finite_number(battery.get("current"))
            and float(battery["current"]) > 0.1)


def robot_output_stopped(state, battery):
    return (do7_state(state) is False and state.get("charging") is False
            and battery.get("charging") is False
            and finite_number(battery.get("current"))
            and float(battery["current"]) <= 0.1)


def station_output_stopped(response):
    # ACK success or charging=0 alone is not proof of output shutdown. Until
    # the vendor clarifies output.current semantics, contradictory/missing
    # fields conservatively leave shutdown unverified.
    return (station_problem(response, require_status=True) is None
            and response["status"].get("is_auto") is False
            and isinstance(response.get("output"), dict)
            and finite_number(response["output"].get("current"))
            and abs(float(response["output"]["current"])) <= 0.1)


def enforce_station_requested_limits(response, payload, tolerances=(0.0, 0.0)):
    output = response.get("output") if isinstance(response, dict) else None
    requested = payload.get("input") if isinstance(payload, dict) else None
    if not isinstance(output, dict) or not isinstance(requested, dict):
        raise Refused("station status is missing requested-limit evidence")
    checks = (
        ("max_voltage", "max_charge_voltage", tolerances[0]),
        ("max_current", "max_charge_current", tolerances[1]),
    )
    for output_key, request_key, tolerance in checks:
        configured = output.get(output_key)
        requested_value = requested.get(request_key)
        if not finite_number(configured) or not finite_number(requested_value):
            raise Refused("station requested-limit evidence is missing or non-finite")
        if float(configured) > float(requested_value) + max(0.0, float(tolerance)):
            raise Refused(
                "station did not apply requested %s: requested %.3f, configured %.3f"
                % (request_key, float(requested_value), float(configured)))


class Freshness:
    """Bound host read time and reject repeated/regressing device timestamps.

    Device clocks need not match the host. A missing device timestamp is
    recorded as unavailable; host reads alone cannot certify sensor age.
    """
    def __init__(self):
        self.timestamps = {}

    def check(self, source, response):
        stamp = response.get("create_on")
        if stamp is None:
            return
        try:
            value = dt.datetime.fromisoformat(stamp.replace("Z", "+00:00"))
            if value.tzinfo is None:
                raise ValueError("missing timezone")
        except (ValueError, TypeError, AttributeError) as exc:
            raise ResultUnknown(source + " timestamp is malformed") from exc
        previous = self.timestamps.get(source)
        if previous is not None and value <= previous:
            raise ResultUnknown(source + " timestamp repeated or regressed")
        self.timestamps[source] = value


def fresh_snapshot(client, freshness):
    start = time.monotonic()
    state, battery = battery_snapshot(client)
    elapsed = time.monotonic() - start
    if elapsed > MAX_SNAPSHOT_SECONDS:
        raise ResultUnknown("vehicle snapshot exceeded freshness deadline")
    if not isinstance(state, dict) or not isinstance(battery, dict):
        raise ResultUnknown("vehicle snapshot is malformed")
    freshness.check("vehicle_state", state)
    freshness.check("battery", battery)
    return state, battery, elapsed


def finish_session(client, streams, payload, sequence, evidence, args,
                   relay_attempted, freshness, baseline_warnings):
    """Send fail-safe OFF even after unknown ON; verify before sockets close.

    These are explicit stop commands, never automatic replays of a start.
    A stream with an incomplete exchange is not reused (framing is unknown).
    """
    cleanup = {"verified": False, "errors": [], "samples": [],
               "relay_off_attempted": relay_attempted}
    deadline = time.monotonic() + SHUTDOWN_SECONDS
    if relay_attempted:
        try:
            reply = client.request(RELAY_API, RELAY_PORT,
                                   {"id": RELAY_DO_ID, "status": False}, mutation=True)
            cleanup["relay_off_response"] = reply
            if not explicit_success(reply):
                cleanup["errors"].append("DO7 off was not explicitly acknowledged")
        except Exception as exc:
            cleanup["errors"].append("DO7 off: " + str(exc))

    stop_payload = {**payload, "input": {**payload["input"], "is_auto": False}}
    stopped_count = 0
    previous_sample_at = None
    last_station_at = None
    station_stopped = False
    acknowledged = {name: False for name in ("ack", "status")}
    # Continue periodic session-stop heartbeats until verified or bounded out.
    # Do not close a socket while assuming that one ret_code=0 proves stop.
    while time.monotonic() < deadline:
        sequence += 1
        for name in ("ack", "status"):
            stream = streams.get(name)
            if stream is None:
                continue
            try:
                reply = exchange_on_open(
                    stream, stop_payload, sequence, evidence,
                    "shutdown_%04d_%s.json" % (sequence, name),
                    timeout=min(5.0, max(0.01, deadline - time.monotonic())))
                cleanup[name + "_response"] = reply
                problem = station_problem(reply, require_status=name == "status")
                if problem:
                    raise Refused(problem)
                freshness.check("station_" + name, reply)
                acknowledged[name] = True
                if name == "status":
                    station_stopped = station_output_stopped(reply)
                    last_station_at = time.monotonic()
            except Exception as exc:
                cleanup["errors"].append("station %s stop: %s" % (name, exc))
                # Timeout/header failure may leave a partial frame. Stop
                # reading/writing this stream; preserve the other connection.
                try:
                    stream.close()
                except Exception:
                    pass
                streams[name] = None
        try:
            state, battery, elapsed = fresh_snapshot(client, freshness)
            now = time.monotonic()
            sample = sample_summary(state, battery)
            sample["snapshot_seconds"] = round(elapsed, 3)
            cleanup["samples"].append(sample)
            pair_fresh = (previous_sample_at is None or
                          0 < now - previous_sample_at <= MAX_SAMPLE_GAP_SECONDS)
            previous_sample_at = now
            stopped = (robot_output_stopped(state, battery)
                       and safe_sample(state, battery, args.max_temperature)
                       and not (set(warning_signature(state, battery))
                                - set(baseline_warnings)))
            stopped_count = stopped_count + 1 if stopped and pair_fresh else 0
            station_fresh = (last_station_at is not None
                             and now - last_station_at <= MAX_STATION_AGE_SECONDS)
            if (stopped_count >= 2 and station_stopped and station_fresh
                    and all(acknowledged.values()) and not cleanup["errors"]):
                cleanup["verified"] = True
                break
        except Exception as exc:
            cleanup["errors"].append("shutdown vehicle feedback: " + str(exc))
            break
        if not all(streams.values()):
            break
        time.sleep(min(args.heartbeat_seconds,
                       max(0.0, deadline - time.monotonic())))
    if not cleanup["verified"] and not cleanup["errors"]:
        cleanup["errors"].append("shutdown feedback did not converge before deadline")
    cleanup["station_output_stopped"] = station_stopped
    cleanup["consecutive_robot_stopped"] = stopped_count
    return cleanup


def run_session(client, args, run_id, evidence):
    info = client.request(1000, 19204, {})
    if str(info.get("id", "")).casefold() != args.expected_id.casefold():
        raise Refused("robot hardware ID mismatch")
    freshness = Freshness()
    state, battery, _ = fresh_snapshot(client, freshness)
    blockers = []
    if not do7_is_confirmed_off(state):
        blockers.append("DO7 is not confirmed off before session start")
    if not safe_sample(state, battery, args.max_temperature):
        blockers.append("vehicle safety preflight failed")
    if state.get("charging") is not False or battery.get("charging") is not False:
        blockers.append("vehicle is already charging")
    if blockers:
        raise Refused("; ".join(blockers))

    (voltage, current), limits_source = session_limits(battery, args)
    ensure_electrical_limits(
        battery,
        (voltage, current),
        (args.limit_voltage_tolerance, args.limit_current_tolerance))
    payload = build_payload(battery, voltage, current)
    result = {
        "outcome": "not_started",
        "run_id": run_id,
        "before": {
            "state": state_summary(state),
            "battery": sample_summary(state, battery),
        },
        "effective_limits": {
            "max_voltage": voltage,
            "max_current": current,
            "source": limits_source,
        },
        "do7_commanded": False,
        "charging_test_passed": False,
        "automatic_resend_attempted": False,
        "timing_policy": {
            "maximum_sample_gap_seconds": MAX_SAMPLE_GAP_SECONDS,
            "maximum_snapshot_seconds": MAX_SNAPSHOT_SECONDS,
            "maximum_station_age_seconds": MAX_STATION_AGE_SECONDS,
            "device_timestamp_policy": "strictly increasing when supplied; host read age otherwise",
        },
        "full_docking_cycle_verified": False,
        "evidence_directory": str(evidence.directory),
    }
    baseline_warnings = warning_signature(state, battery)
    result["baseline_warning_signature"] = list(baseline_warnings)
    evidence.write_json("run.json", result)
    if not args.execute:
        result["outcome"] = "session_staged"
        return result

    streams = {"ack": None, "status": None}
    sequence = 0
    relay_attempted = False
    station_attempted = False
    observations = []
    station_samples = []
    result["observations"] = observations
    result["station_samples"] = station_samples
    result["charging_interval_verified"] = False
    interval = PositiveInterval()

    def station_exchange(active_payload, label):
        nonlocal sequence
        sequence += 1
        responses = {}
        for name in ("ack", "status"):
            try:
                response = exchange_on_open(
                    streams[name], active_payload, sequence, evidence,
                    "%s_%04d_%s.json" % (label, sequence, name))
            except Exception:
                # A mutation may have arrived and this stream's framing is
                # uncertain. Never blindly retry its active-mode command.
                try:
                    streams[name].close()
                except Exception:
                    pass
                streams[name] = None
                raise
            responses[name] = response
            problem = station_problem(response, require_status=name == "status")
            if problem:
                raise Refused(name + ": " + problem)
            if name == "status":
                enforce_station_requested_limits(
                    response,
                    active_payload,
                    (args.limit_voltage_tolerance,
                     args.limit_current_tolerance))
            freshness.check("station_" + name, response)
        return responses["ack"], responses["status"]

    def ensure_safe(state, battery):
        if not safe_sample(state, battery, args.max_temperature):
            raise Refused("vehicle safety stop")
        if set(warning_signature(state, battery)) - set(baseline_warnings):
            raise Refused("vehicle new warning")

    try:
        streams["ack"], streams["status"] = connect_charger(args.charger)
        station_attempted = True
        ack, status = station_exchange(payload, "initial")
        result["initial_ack"] = ack
        result["initial_status"] = status
        station_status = status.get("status") or {}
        auto_deadline = time.monotonic() + args.auto_wait_seconds
        while station_status.get("is_auto") is not True:
            if time.monotonic() >= auto_deadline:
                raise Refused("station did not confirm automatic mode")
            time.sleep(args.heartbeat_seconds)
            state, battery, _ = fresh_snapshot(client, freshness)
            ensure_safe(state, battery)
            (voltage, current_limit), _ = session_limits(battery, args)
            payload = build_payload(battery, voltage, current_limit)
            ack, status = station_exchange(payload, "auto_wait")
            station_status = status.get("status") or {}
            evidence.write_json("auto_wait_state_%04d.json" % sequence, {
                "host_utc": utc_now(),
                "sequence": sequence,
                "ack": ack,
                "status": station_status,
                "output": status.get("output") if isinstance(status, dict) else None,
                "alarms": status.get("alarms") if isinstance(status, dict) else None,
            })

        # Auto-mode waiting may have taken seconds. Recheck immediately before
        # relay enable and ensure the vehicle is still not already charging.
        state, battery, _ = fresh_snapshot(client, freshness)
        ensure_safe(state, battery)
        if (not do7_is_confirmed_off(state) or state.get("charging") is not False
                or battery.get("charging") is not False):
            raise Refused("relay/charging state changed before enable")
        (latest_voltage, latest_current), _ = session_limits(battery, args)
        ensure_electrical_limits(
            battery,
            (latest_voltage, latest_current),
            (args.limit_voltage_tolerance, args.limit_current_tolerance))
        if (payload["input"]["max_charge_voltage"] > latest_voltage
                or payload["input"]["max_charge_current"] > latest_current):
            raise Refused("BMS limits fell after the last station heartbeat")
        relay_attempted = True
        relay = client.request(RELAY_API, RELAY_PORT, {
            "id": RELAY_DO_ID,
            "status": True,
        }, mutation=True)
        result["relay_response"] = relay
        if not explicit_success(relay):
            raise ResultUnknown("DO7 response was not an explicit success")
        result["do7_commanded"] = True

        started = time.monotonic()
        deadline = started + args.duration_seconds
        connection_deadline = started + args.connection_confirm_seconds
        connection_established = False
        connection_lost_at = None
        next_heartbeat = started
        last_station = status
        station_at = time.monotonic()
        end_reason = "duration_reached"
        while time.monotonic() < deadline:
            state, battery, elapsed = fresh_snapshot(client, freshness)
            sample = sample_summary(state, battery)
            sample["snapshot_seconds"] = round(elapsed, 3)
            observations.append(sample)
            ensure_safe(state, battery)
            active_limits, _ = session_limits(battery, args)
            ensure_electrical_limits(
                battery,
                active_limits,
                (args.limit_voltage_tolerance, args.limit_current_tolerance))
            charging_now = positive_charge(state, battery)
            sampled_at = time.monotonic()
            if not connection_established and sampled_at > connection_deadline:
                result["connection_aborted_after_seconds"] = round(sampled_at - started, 3)
                raise Refused("charging connection was not established within %.1f seconds; "
                              "stop output and realign the vehicle" % args.connection_confirm_seconds)
            if charging_now:
                if not connection_established:
                    result["connection_confirmed_after_seconds"] = round(sampled_at - started, 3)
                connection_established = True
                connection_lost_at = None
            elif not connection_established and time.monotonic() >= connection_deadline:
                result["connection_aborted_after_seconds"] = round(
                    time.monotonic() - started, 3)
                raise Refused(
                    "charging connection was not established within %.1f seconds; "
                    "stop output and realign the vehicle" %
                    args.connection_confirm_seconds)
            elif connection_established:
                if connection_lost_at is None:
                    connection_lost_at = sampled_at
                if sampled_at - connection_lost_at >= args.connection_confirm_seconds:
                    result["connection_lost_for_seconds"] = round(sampled_at - connection_lost_at, 3)
                    raise Refused("charging current was lost continuously; stop output and inspect docking")
            if not interval.observe(time.monotonic(), charging_now):
                raise ResultUnknown("vehicle sampling gap exceeded freshness limit")

            now = time.monotonic()
            if now >= next_heartbeat:
                (voltage, current_limit), current_source = session_limits(
                    battery, args)
                payload = build_payload(battery, voltage, current_limit)
                ack, status = station_exchange(payload, "heartbeat")
                station_samples.append({
                    "host_utc": utc_now(),
                    "sequence": sequence,
                    "ack": ack,
                    "status": status.get("status") if isinstance(status, dict) else None,
                    "output": status.get("output") if isinstance(status, dict) else None,
                    "alarms": status.get("alarms") if isinstance(status, dict) else None,
                })
                evidence.write_json("live_state_%04d.json" % sequence, {
                    "host_utc": utc_now(),
                    "elapsed_seconds": round(time.monotonic() - started, 3),
                    "consecutive_positive": interval.count,
                    "continuous_positive_seconds": round(interval.seconds, 3),
                    "limits_source": current_source,
                    "vehicle": sample,
                    "station": station_samples[-1],
                })
                if (status.get("status") or {}).get("is_auto") is not True:
                    raise Refused("station left automatic mode")
                last_station = status
                station_at = time.monotonic()
                next_heartbeat = now + args.heartbeat_seconds
            if time.monotonic() - station_at > MAX_STATION_AGE_SECONDS:
                raise ResultUnknown("station status exceeded freshness limit")
            time.sleep(min(args.poll_seconds, max(0.0, deadline - time.monotonic())))

        confirmed = (
            end_reason == "duration_reached"
            and interval.count >= args.confirm_samples
            and interval.seconds >= args.minimum_positive_seconds
            and interval.last is not None
            and time.monotonic() - interval.last <= MAX_SAMPLE_GAP_SECONDS
            and time.monotonic() - station_at <= MAX_STATION_AGE_SECONDS
        )
        result.update({
            "outcome": "charging_awaiting_shutdown" if confirmed else "charging_not_confirmed",
            "charging_interval_verified": confirmed,
            "end_reason": end_reason,
            "consecutive_positive": interval.count,
            "continuous_positive_seconds": round(interval.seconds, 3),
            "longest_positive_seconds": round(interval.longest, 3),
            "minimum_positive_seconds": args.minimum_positive_seconds,
            "observations": observations,
            "station_samples": station_samples,
            "last_station": last_station,
        })
    except (Exception, KeyboardInterrupt) as exc:
        result.update({
            "outcome": "unknown" if station_attempted and not isinstance(exc, Refused)
                       else "refused_or_failed",
            "error": str(exc) or type(exc).__name__,
            "error_type": type(exc).__name__,
            "end_reason": "session_error",
            "consecutive_positive": interval.count,
            "continuous_positive_seconds": round(interval.seconds, 3),
            "longest_positive_seconds": round(interval.longest, 3),
        })
    finally:
        if station_attempted or relay_attempted:
            try:
                result["shutdown"] = finish_session(
                    client, streams, payload, sequence, evidence, args,
                    relay_attempted, freshness, baseline_warnings)
            except (Exception, KeyboardInterrupt) as exc:
                result["shutdown"] = {"verified": False,
                                      "errors": ["cleanup interrupted/failed: " + str(exc)]}
        for stream in streams.values():
            if stream is not None:
                try:
                    stream.close()
                except Exception:
                    pass
    result["charging_test_passed"] = (
        result["charging_interval_verified"]
        and result.get("shutdown", {}).get("verified") is True)
    if result["charging_test_passed"]:
        result["outcome"] = "charging_session_completed"
    elif result["outcome"] == "charging_awaiting_shutdown":
        result["outcome"] = "shutdown_not_confirmed"
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--charger", required=True, type=ipv4)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--duration-seconds", type=int, default=120)
    parser.add_argument("--heartbeat-seconds", type=float, default=1.0)
    parser.add_argument("--poll-seconds", type=float, default=0.5)
    parser.add_argument("--auto-wait-seconds", type=float, default=12.0)
    parser.add_argument("--confirm-samples", type=int, default=3)
    parser.add_argument("--minimum-positive-seconds", type=float, default=60.0)
    parser.add_argument("--connection-confirm-seconds", type=float, default=5.0,
                        help="stop and realign if charging current does not start by this deadline")
    parser.add_argument("--max-temperature", type=float, default=45.0)
    parser.add_argument("--limit-voltage-tolerance", type=float, default=0.5)
    parser.add_argument("--limit-current-tolerance", type=float, default=0.75)
    parser.add_argument("--allow-configured-limits", action="store_true",
                        help="deprecated: never substitutes for unknown BMS limits")
    parser.add_argument("--max-voltage", type=float)
    parser.add_argument("--max-current", type=float)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args(argv)
    if not 10 <= args.duration_seconds <= 600:
        parser.error("--duration-seconds must be between 10 and 600")
    if not 0.5 <= args.heartbeat_seconds <= 5.0:
        parser.error("--heartbeat-seconds must be between 0.5 and 5")
    if not 0.1 <= args.poll_seconds <= 2.0:
        parser.error("--poll-seconds must be between 0.1 and 2")
    if not 1.0 <= args.auto_wait_seconds <= 30.0:
        parser.error("--auto-wait-seconds must be between 1 and 30")
    if not 1 <= args.confirm_samples <= 10:
        parser.error("--confirm-samples must be between 1 and 10")
    if not 1.0 <= args.minimum_positive_seconds <= args.duration_seconds:
        parser.error("--minimum-positive-seconds must be between 1 and duration")
    if not 1.0 <= args.connection_confirm_seconds <= min(30.0, args.duration_seconds):
        parser.error("--connection-confirm-seconds must be between 1 and 30 seconds and must not exceed duration")
    if not 20.0 <= args.max_temperature <= 60.0:
        parser.error("--max-temperature must be between 20 and 60")
    if not 0.0 <= args.limit_voltage_tolerance <= 1.0:
        parser.error("--limit-voltage-tolerance must be between 0 and 1 V")
    if not 0.0 <= args.limit_current_tolerance <= 2.0:
        parser.error("--limit-current-tolerance must be between 0 and 2 A")
    if args.max_voltage is not None and not 50.0 <= args.max_voltage <= 60.0:
        parser.error("--max-voltage must be between 50 and 60")
    if args.max_current is not None and not 1.0 <= args.max_current <= 30.0:
        parser.error("--max-current must be between 1 and 30")
    if args.allow_configured_limits and (
            args.max_voltage is None or args.max_current is None):
        parser.error("configured limits require both --max-voltage and --max-current")

    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(args.evidence_dir, run_id)
    client = Robokit(args.robot, evidence)
    try:
        result = run_session(client, args, run_id, evidence)
    except ResultUnknown as exc:
        result = {
            "outcome": "unknown",
            "run_id": run_id,
            "error": str(exc),
            "charging_test_passed": False,
            "automatic_resend_attempted": False,
            "evidence_directory": str(evidence.directory),
        }
    except Exception as exc:
        result = {
            "outcome": "refused_or_failed",
            "run_id": run_id,
            "error": str(exc),
            "charging_test_passed": False,
            "automatic_resend_attempted": False,
            "evidence_directory": str(evidence.directory),
        }
    result["host_utc"] = utc_now()
    evidence.write_json("result.json", result)
    print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))
    return 0 if result["outcome"] in (
        "session_staged", "charging_session_completed") else (
            3 if result["outcome"] == "unknown" else 2)


if __name__ == "__main__":
    sys.exit(main())
