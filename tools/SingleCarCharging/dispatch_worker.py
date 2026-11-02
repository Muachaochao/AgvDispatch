#!/usr/bin/env python3
"""One persisted dispatch operation. No operation replay or charger defaults.

Only this fixed entry point is launched by the service. All physical parameters
come from a commissioned vehicle/map calibration, pinned by SHA256 in manifest.
"""
import argparse
import datetime as dt
import hashlib
import json
import math
import os
from pathlib import Path
import re
import time
from types import SimpleNamespace

from probe_runner import (Evidence, Robokit, Refused, ResultUnknown, STATUS_PORT,
                          NAV_PORT, ipv4, stopped_runtime, unique_task_status, utc_now)
from approach_runner import run_approach
from advance_runner import run_advance
import charger_session_runner as charge


SCHEMA = "agv-dispatch-charge-profile-v1"
IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")


def read_json(path):
    if Path(path).stat().st_size > 1024 * 1024:
        raise Refused("dispatch JSON exceeds size limit")
    value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise Refused("dispatch JSON must be an object")
    return value


def atomic_json(path, value):
    temporary = Path(str(path) + ".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=True, allow_nan=False)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def finite(value, lower, upper, name):
    if type(value) not in (int, float) or not math.isfinite(value) or not lower <= value <= upper:
        raise Refused("%s is outside [%s,%s]" % (name, lower, upper))
    return float(value)


def normalize(angle):
    return (angle + math.pi) % (2 * math.pi) - math.pi


def load_profile(manifest):
    path = Path(manifest["profilePath"]).resolve(strict=True)
    raw = path.read_bytes()
    if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != manifest["profileSha256"]:
        raise Refused("calibration changed after the dispatch intent was saved")
    profile = read_json(path)
    if profile.get("schema") != SCHEMA or profile.get("commissioned") is not True:
        raise Refused("a commissioned dispatch charging calibration is required")
    for key in ("calibrationVersion", "hardwareId", "robotMap", "robotMapMd5", "approachSite"):
        if not isinstance(profile.get(key), str) or not profile[key].strip():
            raise Refused("calibration is missing " + key)
    for key in ("vehicleId", "siteId", "robotIp"):
        if profile.get(key) != manifest.get(key):
            raise Refused("calibration identity mismatch: " + key)
    if profile["approachSite"] != manifest.get("approachSite"):
        raise Refused("charging route approach site differs from calibration")
    if (profile["robotMap"] != manifest.get("robotMap")
            or profile["robotMapMd5"].casefold() != str(manifest.get("robotMapMd5", "")).casefold()):
        raise Refused("calibration differs from the backend verified map binding")
    ipv4(profile["robotIp"])
    ipv4(profile["chargerIp"])
    if not re.fullmatch(r"[0-9a-fA-F]{32}", profile["robotMapMd5"]):
        raise Refused("calibration map MD5 is invalid")
    # Each new invocation measures the reflector. No old world docking pose is
    # accepted as an alternative when recognition fails.
    standoffs = profile.get("approachStandoffsM")
    if not isinstance(standoffs, list) or not 1 <= len(standoffs) <= 3:
        raise Refused("one to three commissioned recognition/approach stages are required")
    for index, distance in enumerate(standoffs):
        finite(distance, 0.65, 1.0, "approachStandoffsM")
        if index and distance >= standoffs[index - 1]:
            raise Refused("approach standoffs must strictly decrease")
    finite(profile.get("approachSpeedMps"), 0.01, 0.05, "approachSpeedMps")
    finite(profile.get("insertionDistanceM"), 0.001, 0.15, "insertionDistanceM")
    finite(profile.get("insertionSpeedMps"), 0.002, 0.02, "insertionSpeedMps")
    finite(profile.get("maximumAxisErrorRad"), 0.005, 0.08, "maximumAxisErrorRad")
    finite(profile.get("obstacleStopDistanceM"), 0.05, 0.10, "obstacleStopDistanceM")
    segments = profile.get("undockSegmentsM")
    if not isinstance(segments, list) or not 1 <= len(segments) <= 6:
        raise Refused("commissioned undock segments are required")
    for distance in segments:
        finite(distance, -0.30, -0.001, "undockSegmentsM")
    if sum(abs(v) for v in segments) > 1.2:
        raise Refused("total undock travel exceeds 1.2 m")
    finite(profile.get("undockSpeedMps"), 0.002, 0.02, "undockSpeedMps")
    pose = profile.get("approachPose")
    if not isinstance(pose, dict):
        raise Refused("commissioned approachPose is required for physical undock verification")
    for key in ("x", "y", "angleRad"):
        finite(pose.get(key), -100000, 100000, "approachPose." + key)
    finite(profile.get("maximumUndockPositionErrorM"), 0.01, 0.10,
           "maximumUndockPositionErrorM")
    return profile


class Operation:
    def __init__(self, path):
        self.path = Path(path).resolve(strict=True)
        self.directory = self.path.parent
        self.manifest = read_json(self.path)
        for key in ("operationId", "sessionId"):
            if not IDENTIFIER.fullmatch(str(self.manifest.get(key, ""))):
                raise Refused("invalid " + key)
        if self.manifest.get("kind") not in ("Dock", "Charge", "Undock"):
            raise Refused("unsupported charging operation")
        finite(self.manifest.get("leaseTimeoutSeconds"), 10, 60, "leaseTimeoutSeconds")
        self.status = {
            "operationId": self.manifest["operationId"], "kind": self.manifest["kind"],
            "state": "Running", "updatedAt": utc_now(), "message": "Worker preparing calibrated operation",
            "continuousPositiveSeconds": 0.0, "shutdownVerified": False,
            "evidenceDirectory": str(self.directory),
        }
        self.active_client = None

    def control_check(self):
        stop = self.directory / "stop.json"
        if stop.exists():
            value = read_json(stop)
            if value.get("operationId") != self.manifest["operationId"]:
                raise ResultUnknown("stop request identity mismatch")
            raise Refused("dispatch requested stop; no further movement/output enable")
        lease = read_json(self.directory / "lease.json")
        if lease.get("operationId") != self.manifest["operationId"]:
            raise ResultUnknown("dispatch lease identity mismatch")
        stamp = dt.datetime.fromisoformat(lease["updatedAt"])
        age = (dt.datetime.now(dt.timezone.utc) - stamp).total_seconds()
        if not -2 <= age <= self.manifest["leaseTimeoutSeconds"]:
            raise ResultUnknown("dispatch lease expired; retain charging reservation")

    def publish(self, **changes):
        self.status.update(changes)
        self.status["updatedAt"] = utc_now()
        atomic_json(self.directory / "worker_state.json", self.status)

    def child_client(self, profile, label):
        evidence = Evidence(self.directory / "evidence", label)
        self.active_client = GuardedRobot(profile["robotIp"], evidence, self, profile)
        return self.active_client

    def validate_approach_pose(self, profile, state):
        pose = profile["approachPose"]
        x = finite(state.get("x"), -100000, 100000, "approach x")
        y = finite(state.get("y"), -100000, 100000, "approach y")
        angle = finite(state.get("angle"), -100, 100, "approach angle")
        if (state.get("current_station") != profile["approachSite"] or not stopped_runtime(state)
                or math.hypot(x - pose["x"], y - pose["y"]) > profile["maximumUndockPositionErrorM"]
                or abs(normalize(angle - pose["angleRad"])) > 0.10):
            raise Refused("actual approach position/heading does not match the commissioned docking entry")

    def progress(self, sample, station, seconds):
        battery = {
            "vehicleId": self.manifest["vehicleId"],
            "batteryPercent": sample.get("battery_level", 0) * 100 if sample.get("battery_level") is not None else None,
            "charging": sample.get("charging"), "current": sample.get("current"),
            "temperature": sample.get("temperature"), "manualCharge": sample.get("manual_charge"),
            "autoCharge": None, "observedAt": sample["host_utc"],
        }
        raw = station.get("status") or {}
        output = station.get("output") or {}
        alarms = station.get("alarms") or {}
        station_reading = {
            "chargerIp": self.profile["chargerIp"], "isAuto": raw.get("is_auto"),
            # The vendor charging numeric field and brush position are not mapped
            # to contact/charging semantics until the actual model is confirmed.
            "charging": None, "current": output.get("current"), "voltage": output.get("voltage"),
            "alarms": [json.dumps(row, ensure_ascii=True) for row in alarms.get("errors", [])],
            "observedAt": utc_now(),
        }
        self.publish(message="Observing real charge current and station heartbeat",
                     battery=battery, station=station_reading, continuousPositiveSeconds=seconds)

    def advance(self, profile, distance, speed, label, expected_station=None):
        self.control_check()
        args = SimpleNamespace(robot=profile["robotIp"], expected_id=profile["hardwareId"],
            expected_station=expected_station, distance_m=distance, lateral_m=0.0, yaw_delta_rad=0.0,
            obstacle_stop_dist_m=profile["obstacleStopDistanceM"], motion_mode="path",
            alignment_stage=False, reach_dist_m=0.001, max_speed=speed,
            script_timeout=45.0, track_timeout=55.0, stage_only=False)
        # Keep each on-board segment within its independently enforced timeout.
        if abs(distance) / speed + 10 > args.script_timeout:
            raise Refused("calibrated travel cannot finish within the bounded script timeout")
        client = self.child_client(profile, label)
        result = run_advance(client, args, self.manifest["operationId"] + "-" + label)
        client.evidence.write_json("result.json", result)
        if result.get("outcome") != "advance_completed":
            raise ResultUnknown("bounded %s did not complete: %s" % (label, result.get("outcome")))
        return result

    def dock(self, profile):
        latest = None
        for index, distance in enumerate(profile["approachStandoffsM"]):
            self.control_check()
            label = "approach-%d" % index
            self.publish(message="Stable reflector recognition and bounded approach %d" % (index + 1))
            client = self.child_client(profile, label)
            state = client.request(1100, STATUS_PORT, {})
            if index == 0:
                self.validate_approach_pose(profile, state)
            args = SimpleNamespace(robot=profile["robotIp"], expected_id=profile["hardwareId"],
                expected_station=state.get("current_station") or None,
                standoff_m=distance, max_speed=profile["approachSpeedMps"], stability_angle_deg=2.0,
                script_timeout=120.0, track_timeout=130.0, stage_only=False)
            latest = run_approach(client, args, self.manifest["operationId"] + "-" + label)
            client.evidence.write_json("result.json", latest)
            if latest.get("outcome") != "approach_completed":
                raise ResultUnknown("fresh recognition/approach did not complete")
        # Use this run's stable reference direction, never a saved dock pose.
        samples = (latest.get("report") or {}).get("initial_samples") or []
        if len(samples) != 3:
            raise Refused("approach has no matching stable recognition evidence")
        angles = [s["target_world"]["angle"] for s in samples]
        anchor = angles[0]
        axis = normalize(sorted(anchor + normalize(v - anchor) for v in angles)[1] + math.pi)
        self.control_check()
        client = self.child_client(profile, "axis-check")
        state = client.request(1100, STATUS_PORT, {})
        angle = finite(state.get("angle"), -100, 100, "current angle")
        if abs(normalize(angle - axis)) > profile["maximumAxisErrorRad"]:
            raise Refused("fresh docking axis is not aligned; return to recognition instead of pushing sideways")
        self.publish(message="One calibrated axial insertion; electrical engagement still unverified")
        self.advance(profile, profile["insertionDistanceM"], profile["insertionSpeedMps"],
                     "insert", state.get("current_station") or None)

    def run_charge(self, profile):
        required = self.manifest.get("requiredPositiveSeconds")
        target = None if required is not None else self.manifest.get("targetBatteryPercent")
        if required is not None:
            finite(required, 1, 3600, "requiredPositiveSeconds")
        elif target is not None:
            finite(target, 1, 100, "targetBatteryPercent")
        else:
            raise Refused("charging needs target battery or explicit positive interval")
        args = SimpleNamespace(expected_id=profile["hardwareId"], charger=profile["chargerIp"],
            execute=True, duration_seconds=finite(self.manifest.get("maximumSessionSeconds"), 10, 43200,
                                               "maximumSessionSeconds"),
            heartbeat_seconds=1.0, poll_seconds=0.5, auto_wait_seconds=12.0,
            confirm_samples=3, minimum_positive_seconds=required if required is not None else 1.0,
            connection_confirm_seconds=5.0, max_temperature=finite(
                self.manifest.get("maximumTemperature"), 20, 60, "maximumTemperature"),
            limit_voltage_tolerance=0.5, limit_current_tolerance=0.75,
            max_voltage=None, max_current=None, allow_configured_limits=False,
            required_positive_seconds=required, target_battery_percent=target,
            control_check=self.control_check, progress_callback=self.progress)
        client = self.child_client(profile, "charge")
        result = charge.run_session(client, args, self.manifest["operationId"], client.evidence)
        client.evidence.write_json("result.json", result)
        stopped_samples = result.get("shutdown", {}).get("samples") or []
        if stopped_samples:
            self.progress(stopped_samples[-1], result.get("shutdown", {}).get("status_response")
                          or result.get("last_station") or {},
                          result.get("continuous_positive_seconds", 0.0))
        self.status["shutdownVerified"] = result.get("shutdown", {}).get("verified") is True
        self.status["continuousPositiveSeconds"] = result.get("continuous_positive_seconds", 0.0)
        if not result.get("charging_test_passed"):
            raise ResultUnknown("charge/stop not fully verified: %s; %s" %
                                (result.get("outcome"), result.get("error", "")))

    def undock(self, profile):
        prior_id = self.manifest.get("chargeOperationId", "")
        if not IDENTIFIER.fullmatch(str(prior_id)) or prior_id == self.manifest["operationId"]:
            raise Refused("undock requires the original charge operation identity")
        prior_dir = self.directory.parent / prior_id
        prior_manifest = read_json(prior_dir / "manifest.json")
        prior = read_json(prior_dir / "result.json")
        if (prior_manifest.get("sessionId") != self.manifest["sessionId"]
                or prior_manifest.get("vehicleId") != self.manifest["vehicleId"]
                or prior_manifest.get("profileSha256") != self.manifest["profileSha256"]
                or prior.get("operationId") != prior_id or prior.get("kind") != "Charge"
                or prior.get("shutdownVerified") is not True):
            raise Refused("original charge has no matching verified shutdown evidence")
        for index, distance in enumerate(profile["undockSegmentsM"]):
            self.publish(message="Calibrated axial undock segment %d" % (index + 1))
            client = self.child_client(profile, "undock-check-%d" % index)
            state = client.request(1100, STATUS_PORT, {})
            self.advance(profile, distance, profile["undockSpeedMps"], "undock-%d" % index,
                         state.get("current_station") or None)
        client = self.child_client(profile, "undock-verify")
        state = client.request(1100, STATUS_PORT, {})
        pose = profile["approachPose"]
        x = finite(state.get("x"), -100000, 100000, "undock x")
        y = finite(state.get("y"), -100000, 100000, "undock y")
        angle = finite(state.get("angle"), -100, 100, "undock angle")
        if (not stopped_runtime(state) or state.get("charging") is not False
                or state.get("manual_charge") is not False
                or state.get("current_station") != profile["approachSite"]
                or math.hypot(x - pose["x"], y - pose["y"]) > profile["maximumUndockPositionErrorM"]
                or abs(normalize(angle - pose["angleRad"])) > 0.10):
            raise Refused("physical return to calibrated approach pose is unverified")
        self.status["shutdownVerified"] = True

    def run(self):
        # A second process can never repeat an operation whose launch reply was
        # lost. The exclusive marker is not removed after failures/restarts.
        with (self.directory / "worker.lock").open("xb") as stream:
            stream.write(str(os.getpid()).encode("ascii"))
            stream.flush()
            os.fsync(stream.fileno())
        self.publish()
        try:
            self.control_check()
            self.profile = load_profile(self.manifest)
            {"Dock": self.dock, "Charge": self.run_charge, "Undock": self.undock}[self.manifest["kind"]](self.profile)
            self.control_check()
            self.publish(state="Succeeded", message="Calibrated operation completed with matching observations")
        except Exception as exc:
            if self.manifest["kind"] != "Charge" and self.active_client is not None:
                try:
                    self.status["motionStopVerified"] = self.active_client.cancel_and_verify()
                except Exception as stop_error:
                    self.status["motionStopVerified"] = False
                    self.status["motionStopError"] = str(stop_error)
            self.publish(state="Failed" if isinstance(exc, Refused) else "Unknown", message=str(exc))
        atomic_json(self.directory / "result.json", self.status)
        return 0 if self.status["state"] == "Succeeded" else 2


class GuardedRobot(Robokit):
    """No further movement after lease loss; cancel only the exact active ID."""
    def __init__(self, robot, evidence, operation, profile):
        super().__init__(robot, evidence)
        self.operation, self.profile = operation, profile
        self.active_task = None
        self.cancel_attempted = False
        self.identity_checked = False

    def check_identity(self):
        info = super().request(1000, STATUS_PORT, {})
        if (str(info.get("id", "")).casefold() != self.profile["hardwareId"].casefold()
                or info.get("current_map") != self.profile["robotMap"]
                or str(info.get("current_map_md5", "")).casefold() != self.profile["robotMapMd5"].casefold()):
            raise Refused("live hardware/map does not match commissioned calibration")
        self.identity_checked = True

    def request(self, api, port, payload, **kwargs):
        if not self.identity_checked:
            self.check_identity()
        try:
            self.operation.control_check()
        except Exception:
            if self.active_task and not self.cancel_attempted:
                self.cancel_attempted = True
                super().request(3003, NAV_PORT, {"smartCancel": False, "taskId": self.active_task}, mutation=True)
            if kwargs.get("mutation") and not (api == 6001 and payload.get("id") == 7
                                                and payload.get("status") is False):
                raise
            # Read the one original task after cancellation. Its tracker still
            # requires matching terminal feedback and a physically stopped car.
        if kwargs.get("mutation") and not (api == 6001 and payload.get("status") is False):
            self.check_identity()
        if api == 3066:
            chain = payload.get("move_task_list")
            if not isinstance(chain, list) or len(chain) != 1:
                raise Refused("dispatch worker accepts one exact motion task only")
            self.active_task = chain[0]["task_id"]
        result = super().request(api, port, payload, **kwargs)
        if api in (1100, 1110):
            self.operation.publish()
        return result

    def cancel_and_verify(self):
        if not self.active_task:
            return False
        deadline = time.monotonic() + 12.0
        while time.monotonic() < deadline:
            response = super().request(1110, STATUS_PORT, {"task_ids": [self.active_task]}, deadline=deadline)
            status = unique_task_status(response, self.active_task)
            state = super().request(1100, STATUS_PORT, {}, deadline=deadline)
            if status in (4, 5, 6, 7) and stopped_runtime(state):
                return True
            if not self.cancel_attempted:
                self.cancel_attempted = True
                super().request(3003, NAV_PORT, {"smartCancel": False, "taskId": self.active_task}, mutation=True,
                                deadline=deadline)
            time.sleep(0.25)
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    args = parser.parse_args()
    return Operation(args.manifest).run()


if __name__ == "__main__":
    raise SystemExit(main())
