#!/usr/bin/env python3
"""Send one guarded, isolated 3066 station chain; never a fleet traffic controller."""
import argparse
import datetime as dt
import json
import math
from pathlib import Path
import re
import sys
import time
import urllib.parse
import urllib.request
import uuid

from probe_runner import (
    NAV_PORT,
    STATUS_PORT,
    CONFIG_PORT,
    Evidence,
    Refused,
    ResultUnknown,
    Robokit,
    blocking_vehicle_errors,
    ipv4,
    runtime_summary,
    utc_now,
)


TERMINAL_FAILURES = frozenset({5, 6, 7})
MAX_CONTEXT_AGE_SECONDS = 5.0
MAX_CONTEXT_BYTES = 8 * 1024 * 1024


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def stopped(state):
    return (state.get("is_stop") is True and state.get("chassisIsStop") is True
            and all(finite(state.get(key)) and abs(state[key]) < 0.01
                    for key in ("vx", "vy", "w")))


def backend_url(value):
    parsed = urllib.parse.urlsplit(value)
    if (parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost")
            or parsed.username or parsed.password or parsed.query or parsed.fragment
            or parsed.path not in ("", "/")):
        raise argparse.ArgumentTypeError("backend must be a local HTTP origin, e.g. http://127.0.0.1:5199")
    return value.rstrip("/")


class BackendReader:
    """GET-only, bounded observations. No send, release, cancel, or configuration API."""
    def __init__(self, base_url, evidence):
        self.base_url, self.evidence, self.read_index = backend_url(base_url), evidence, 0

    def read(self):
        self.read_index += 1
        result = {}
        started = time.monotonic()
        # Sequential: Web's backing named pipe is serial, not an atomic snapshot.
        for name, path in (("snapshot", "/api/snapshot"),
                           ("closures", "/api/road-closures"),
                           ("configuration", "/api/vehicles/configuration")):
            request = urllib.request.Request(self.base_url + path, method="GET",
                                             headers={"Accept": "application/json"})
            # Disable proxies and redirects: these observations must come from the
            # configured local service, not an external redirect/proxy endpoint.
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
            with opener.open(request, timeout=2.0) as response:
                body = response.read(MAX_CONTEXT_BYTES + 1)
            if len(body) > MAX_CONTEXT_BYTES:
                raise Refused("backend observation exceeds the bounded read budget")
            result[name] = json.loads(body)
        if time.monotonic() - started > MAX_CONTEXT_AGE_SECONDS:
            raise Refused("backend observation took too long; no fresh traffic evidence")
        self.evidence.write_json("backend-%03d.json" % self.read_index, result)
        return result


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def fresh(timestamp, now=None):
    if not isinstance(timestamp, str):
        return False
    try:
        observed = dt.datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        if observed.tzinfo is None:
            return False
        age = ((now or dt.datetime.now(dt.timezone.utc)) - observed).total_seconds()
        return -1.0 <= age <= MAX_CONTEXT_AGE_SECONDS
    except ValueError:
        return False


def point(value):
    if not isinstance(value, dict) or not all(finite(value.get(k)) for k in ("x", "y")):
        raise Refused("map point has missing or invalid coordinates")
    return (value["x"], value["y"])


def curve_points(curve, nodes):
    source, target = (curve.get(key, {}).get("instanceName") for key in ("startPos", "endPos"))
    if source not in nodes or target not in nodes:
        raise Refused("robot curve references an unknown station")
    a, b = nodes[source], nodes[target]
    for key, expected in (("startPos", a), ("endPos", b)):
        embedded = curve[key].get("pos")
        if embedded is not None and math.dist(point(embedded), expected) > 0.001:
            raise Refused("robot curve endpoint disagrees with its station")
    kind = curve.get("className")
    if kind == "StraightPath":
        return source, target, "Line", [a, b]
    c = point(curve.get("controlPos1"))
    if kind in ("BezierPath", "DegenerateBezier"):
        d = point(curve.get("controlPos2"))
        samples = [tuple((1-t)**3*a[j] + 3*(1-t)**2*t*c[j]
                         + 3*(1-t)*t*t*d[j] + t**3*b[j] for j in (0, 1))
                   for t in (i/32 for i in range(33))]
        return source, target, "CubicBezier", samples
    if kind != "ArcPath":
        raise Refused("unsupported robot path geometry; cannot prove map equivalence")
    # Same relative-coordinate construction and degeneracy test as the
    # backend's MapEdgeGeometry.GetArc; avoids cancellation at large world XY.
    cx, cy = c[0]-a[0], c[1]-a[1]
    bx, by = b[0]-a[0], b[1]-a[1]
    denominator = 2*(cx*by-cy*bx)
    energy = max(cx*cx+cy*cy, bx*bx+by*by)
    if abs(denominator) <= 1e-10 * max(energy, 1e-12):
        raise Refused("collinear or degenerate robot arc")
    control_squared, end_squared = cx*cx+cy*cy, bx*bx+by*by
    ox = a[0]+(by*control_squared-cy*end_squared)/denominator
    oy = a[1]+(cx*end_squared-bx*control_squared)/denominator
    start, mid, end = (math.atan2(y-oy, x-ox) for x, y in (a, c, b))
    sweep = (end-start) % math.tau
    if (mid-start) % math.tau > sweep:
        sweep -= math.tau
    radius = math.dist(a, (ox, oy))
    samples = [(ox+radius*math.cos(start+sweep*i/32),
                oy+radius*math.sin(start+sweep*i/32)) for i in range(33)]
    samples[0], samples[-1] = a, b
    return source, target, "CircularArc", samples


def topology_matches(local, robot_map, expected_map):
    """Exact enabled station/directed-edge coverage, including sampled geometry."""
    header = robot_map.get("header", {})
    if header.get("mapName") != expected_map or header.get("mapType") != "2D-Map":
        raise Refused("4011 map header does not match the inspected current map")
    robot_nodes = {}
    for node in robot_map.get("advancedPointList", []):
        site = node.get("instanceName")
        if not isinstance(site, str) or not site or site in robot_nodes:
            raise Refused("robot map station ID is missing or duplicated")
        robot_nodes[site] = point(node.get("pos"))
    nodes = local.get("nodes")
    edges = local.get("edges")
    if not isinstance(nodes, list) or not nodes or not isinstance(edges, list) or not edges:
        raise Refused("backend map has no usable topology")
    local_nodes, ids = {}, {}
    for node in nodes:
        if node.get("enabled") is not True:
            raise Refused("isolated detour requires the complete enabled runtime graph")
        site, node_id = node.get("agvSiteId"), node.get("id")
        if (not isinstance(site, str) or not site or site in local_nodes
                or not isinstance(node_id, str) or not node_id or node_id in ids):
            raise Refused("backend map station identity is missing or duplicated")
        ids[node_id], local_nodes[site] = site, point(node)
    if set(local_nodes) != set(robot_nodes) or any(
            math.dist(xy, robot_nodes[site]) > 0.00001 for site, xy in local_nodes.items()):
        raise Refused("backend and robot station coordinates differ")
    curves = robot_map.get("advancedCurveList")
    if not isinstance(curves, list) or not curves:
        raise Refused("4011 has no directed curve evidence")
    remote = [curve_points(curve, robot_nodes) for curve in curves]
    local_directed = []
    for edge in edges:
        if (edge.get("enabled") is not True or edge.get("from") not in ids or edge.get("to") not in ids
                or type(edge.get("bidirectional")) is not bool):
            raise Refused("backend edge is disabled or references an unknown station")
        samples = edge.get("points")
        if not isinstance(samples, list) or len(samples) < 2:
            raise Refused("backend edge has no geometry evidence")
        row = (ids[edge["from"]], ids[edge["to"]], edge.get("kind"), [point(p) for p in samples])
        local_directed.append(row)
        if edge.get("bidirectional") is True:
            local_directed.append((row[1], row[0], row[2], list(reversed(row[3]))))
    if len(local_directed) != len(remote):
        raise Refused("backend and robot directed road counts differ")
    unmatched = list(remote)
    for row in local_directed:
        match = next((i for i, other in enumerate(unmatched)
                      if row[:3] == other[:3] and len(row[3]) == len(other[3])
                      and all(math.dist(a, b) <= 0.00001 for a, b in zip(row[3], other[3]))), None)
        if match is None:
            raise Refused("backend and robot directed roads or geometry differ")
        unmatched.pop(match)
    return ids, local_nodes


def curve_cover_padding(curve, nodes):
    """Conservative curve-to-chord cover, rather than assuming 32 chords are exact."""
    a = nodes[curve["startPos"]["instanceName"]]
    b = nodes[curve["endPos"]["instanceName"]]
    if curve["className"] == "StraightPath":
        return 0.0
    c = point(curve["controlPos1"])
    if curve["className"] in ("BezierPath", "DegenerateBezier"):
        d = point(curve["controlPos2"])
        # The derivative of a cubic Bezier is in the convex hull of these
        # three vectors, so its norm is bounded by three times the longest.
        maximum_speed = 3*max(math.dist(a, c), math.dist(c, d), math.dist(d, b))
    else:
        cross = abs((c[0]-a[0])*(b[1]-a[1])-(c[1]-a[1])*(b[0]-a[0]))
        radius = math.dist(a, c)*math.dist(c, b)*math.dist(b, a)/(2*cross)
        maximum_speed = math.tau*radius  # A supported arc traverses less than one full circle.
    # Every curve point is at most half of a 1/32 parameter interval from a
    # sampled endpoint. This intentionally overestimates the cover error.
    return maximum_speed/64


def validate_context(context, args, robot_map):
    snapshot, closures, configuration = (context.get(key) for key in ("snapshot", "closures", "configuration"))
    if not all(isinstance(item, dict) for item in (snapshot, closures, configuration)):
        raise Refused("incomplete backend traffic observation")
    local = snapshot.get("map")
    if (snapshot.get("connected") is not True or not fresh(snapshot.get("receivedAt"))
            or not isinstance(local, dict) or local.get("unit") != "m"
            or local.get("coordinateSystem") != "RobotWorld" or local.get("warnings") != []):
        raise Refused("backend snapshot/map is stale, incomplete, or not in robot-world metres")
    if (closures.get("ready") is not True or closures.get("identityConflict") is not False
            or closures.get("mapId") != local.get("id") or closures.get("versionId") != local.get("version")
            or not local.get("definitionHash") or closures.get("definitionHash") != local["definitionHash"]
            or type(closures.get("revision")) is not int):
        raise Refused("road closure state or map identity is not ready")
    ids, nodes = topology_matches(local, robot_map, args.expected_map)
    pairs = set(zip(args.route, args.route[1:]))
    edges = {(ids[e["from"]], ids[e["to"]]) for e in local["edges"]}
    edges |= {(ids[e["to"]], ids[e["from"]]) for e in local["edges"] if e.get("bidirectional") is True}
    if not pairs <= edges:
        raise Refused("selected station chain is not a directed route in the runtime map")
    blocked = closures.get("blockedTraversals")
    if not isinstance(blocked, list) or not isinstance(closures.get("closures"), list):
        raise Refused("road closure evidence is missing")
    for traversal in blocked:
        if not isinstance(traversal, dict) or traversal.get("fromNodeId") not in ids or traversal.get("toNodeId") not in ids:
            raise Refused("malformed road closure traversal")
        if (ids[traversal["fromNodeId"]], ids[traversal["toNodeId"]]) in pairs:
            raise Refused("selected route crosses an active road closure")
    # Fail closed even if the serializer omitted a computed blocked traversal.
    for closure in closures["closures"]:
        if not isinstance(closure, dict) or type(closure.get("active")) is not bool:
            raise Refused("malformed road closure record")
        if closure["active"]:
            if (closure.get("mapId"), closure.get("versionId"), closure.get("definitionHash")) != (
                    local["id"], local["version"], local["definitionHash"]):
                raise Refused("active road closure belongs to another map identity")
            a, b = closure.get("fromNodeId"), closure.get("toNodeId")
            if a not in ids or b not in ids:
                raise Refused("active closure references unknown nodes")
            if (ids[a], ids[b]) in pairs or (closure.get("bidirectional") is True and (ids[b], ids[a]) in pairs):
                raise Refused("selected route crosses an active road closure")
    configured = configuration.get("activeVehicles")
    agvs = snapshot.get("status", {}).get("agvs")
    if (not isinstance(configured, list) or not configured or not isinstance(agvs, list)
            or not configuration.get("revision")):
        raise Refused("fleet configuration/occupancy evidence is missing")
    own = [v for v in configured if v.get("vehicleId") == args.vehicle_id]
    if len(own) != 1 or own[0].get("ipAddress") != args.robot or own[0].get("enabled") is not True:
        raise Refused("selected vehicle/IP is not enabled in the active backend configuration")
    if len({v.get("vehicleId") for v in configured}) != len(configured):
        raise Refused("duplicate configured vehicle identities")
    own_observed = [v for v in agvs if v.get("vehicleId") == args.vehicle_id]
    if (len(own_observed) != 1 or own_observed[0].get("connection") != "Online"
            or own_observed[0].get("quality") != "Good"
            or not fresh(own_observed[0].get("lastSuccessfulPollAt"))
            or own_observed[0].get("mapPositionState") != "Normal"):
        raise Refused("selected vehicle has no fresh, good backend position observation")
    # Disabled vehicles have no reliable live location here. The mandatory
    # physical isolation acknowledgement covers them; disabling is never used
    # as evidence that a physical vehicle disappeared from the aisle.
    robot_nodes = {n["instanceName"]: point(n["pos"]) for n in robot_map["advancedPointList"]}
    cover_padding = {}
    for curve in robot_map["advancedCurveList"]:
        pair = (curve["startPos"]["instanceName"], curve["endPos"]["instanceName"])
        cover_padding[pair] = max(cover_padding.get(pair, 0), curve_cover_padding(curve, robot_nodes))
    for vehicle in configured:
        if type(vehicle.get("enabled")) is not bool:
            raise Refused("configured vehicle enablement is unknown")
        if vehicle["vehicleId"] == args.vehicle_id or not vehicle["enabled"]:
            continue
        observed = [v for v in agvs if v.get("vehicleId") == vehicle["vehicleId"]]
        if len(observed) != 1:
            raise Refused("other enabled vehicle has no unique occupancy observation")
        state = observed[0]
        if (state.get("connection") != "Online" or state.get("quality") != "Good"
                or not fresh(state.get("lastSuccessfulPollAt"))
                or state.get("mapPositionState") != "Normal"
                or state.get("currentStation") not in nodes
                or not all(finite(state.get(key)) and abs(state[key]) < 0.01
                           for key in ("velocityX", "velocityY", "angularVelocity"))
                or type(state.get("taskCount")) is not int or state["taskCount"] != 0):
            raise Refused("other enabled vehicle is moving or has unverified occupancy")
        if state["currentStation"] in args.route:
            raise Refused("other enabled vehicle occupies the selected station chain")
        clearance = getattr(args, "parked_clearance_m", None)
        if not finite(clearance) or not 0.1 <= clearance <= 10:
            raise Refused("other enabled vehicles require --parked-clearance-m from verified vehicle/load envelopes plus margin")
        xy = point(state)
        for edge in local["edges"]:
            a, b = ids[edge["from"]], ids[edge["to"]]
            if (a, b) not in pairs and not (edge.get("bidirectional") is True and (b, a) in pairs):
                continue
            samples = [point(p) for p in edge["points"]]
            padding = max(cover_padding.get((a, b), 0), cover_padding.get((b, a), 0))
            if any(distance_to_segment(xy, start, end) <= clearance+padding
                   for start, end in zip(samples, samples[1:])):
                raise Refused("other enabled vehicle is within the verified route clearance")
    return {"map_id": local["id"], "map_version": local["version"],
            "definition_hash": local["definitionHash"], "closure_revision": closures["revision"],
            "configuration_revision": configuration["revision"]}


def distance_to_segment(p, a, b):
    length_squared = (b[0]-a[0])**2 + (b[1]-a[1])**2
    if length_squared == 0:
        return math.dist(p, a)
    t = max(0.0, min(1.0, ((p[0]-a[0])*(b[0]-a[0]) + (p[1]-a[1])*(b[1]-a[1]))/length_squared))
    return math.dist(p, (a[0]+t*(b[0]-a[0]), a[1]+t*(b[1]-a[1])))


def route_value(value):
    route = [item.strip() for item in value.split(",") if item.strip()]
    if len(route) < 2:
        raise argparse.ArgumentTypeError("route must contain at least two stations")
    if len(set(route)) != len(route):
        raise argparse.ArgumentTypeError("route stations must not repeat")
    return route


def preflight(client, args):
    info = client.request(1000, STATUS_PORT, {})
    actual_id = info.get("id")
    if (not isinstance(actual_id, str) or not actual_id.strip()
            or not args.expected_id.strip() or actual_id.casefold() != args.expected_id.casefold()):
        raise Refused("robot hardware ID does not match --expected-id")

    state = client.request(1100, STATUS_PORT, {})
    reasons = []
    status = state.get("task_status")
    cancelled_prior = (
        status == 6
        and isinstance(state.get("task_id"), str)
        and bool(state["task_id"].strip())
    )
    if type(status) is not int or (status not in (0, 4) and not cancelled_prior):
        reasons.append("task_status must be idle(0), completed(4), or a confirmed cancelled task(6)")
    if state.get("current_station") != args.route[0]:
        reasons.append("current_station must equal the route origin")
    if state.get("current_map") != args.expected_map:
        reasons.append("current_map mismatch")
    if state.get("current_map_md5") != args.expected_md5:
        reasons.append("current_map_md5 mismatch")
    if type(state.get("script_status")) is not int or state["script_status"] != 0:
        reasons.append("script_status must be idle(0)")
    if state.get("unfinished_path") != []:
        reasons.append("unfinished_path must be empty")
    if not stopped(state):
        reasons.append("vehicle must be explicitly stopped with finite zero velocities")
    for key in ("charging", "manual_charge", "emergency", "soft_emc"):
        if state.get(key) is not False:
            reasons.append(key + " must explicitly be false")
    if state.get("blocked") is not False:
        reasons.append("blocked must explicitly be false")
    if state.get("fatals") != []:
        reasons.append("fatals must be empty")
    if not isinstance(state.get("errors"), list) or blocking_vehicle_errors(state["errors"]):
        reasons.append("errors may only contain low-battery trigger 52503")
    if not finite(state.get("confidence")) or not 0.5 <= state["confidence"] <= 1.0:
        reasons.append("localization confidence must be at least 0.5")
    if not all(finite(state.get(key)) for key in ("x", "y", "angle")):
        reasons.append("pose must be finite")
    lock = state.get("current_lock")
    if not isinstance(lock, dict) or lock.get("locked") is not False:
        reasons.append("control ownership must be released")
    if type(state.get("dispatch_mode")) is not int or state["dispatch_mode"] != 0:
        reasons.append("dispatch_mode must be standalone(0)")
    if reasons:
        raise Refused("detour preflight refused: " + "; ".join(reasons))

    if cancelled_prior or status == 4 and state.get("task_id"):
        task_id = state["task_id"]
        response = client.request(1110, STATUS_PORT, {"task_ids": [task_id]})
        package = response.get("task_status_package", response)
        rows = package.get("task_status_list", []) if isinstance(package, dict) else []
        matches = [row for row in rows if isinstance(row, dict)
                   and row.get("task_id") == task_id]
        if (len(rows) != 1 or len(matches) != 1 or type(matches[0].get("status")) is not int
                or matches[0]["status"] != status):
            raise Refused("prior terminal task state was not confirmed by fresh 1110")

    edge_routes = []
    for source, target in zip(args.route, args.route[1:]):
        response = client.request(
            1303, STATUS_PORT, {"source_id": source, "target_id": target}
        )
        path = response.get("list")
        if path != [source, target]:
            raise Refused(
                "route edge %s->%s is not one direct map edge: %r"
                % (source, target, path)
            )
        edge_routes.append({"from": source, "to": target, "route": path})

    return {
        "identity": {
            key: info.get(key)
            for key in (
                "id",
                "vehicle_id",
                "model",
                "version",
                "current_map",
                "current_map_md5",
            )
        },
        "runtime": runtime_summary(state),
        "edges": edge_routes,
    }


def task_rows(response):
    if not isinstance(response, dict):
        raise Refused("1110 task response is not an object")
    package = response.get("task_status_package", response)
    rows = package.get("task_status_list", []) if isinstance(package, dict) else []
    if not isinstance(rows, list):
        raise Refused("1110 task list is missing or malformed")
    result = {}
    for row in rows:
        if (not isinstance(row, dict) or not isinstance(row.get("task_id"), str)
                or not row["task_id"] or type(row.get("status")) is not int
                or row["status"] not in range(8) or row["task_id"] in result):
            raise Refused("1110 task row has a missing, duplicated, or malformed identity/status")
        result[row["task_id"]] = row["status"]
    return result


def segment_ids(prefix, count):
    # The readable prefix is a label, never a reusable vendor task identity.
    label = prefix or "DETOUR"
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,24}", label):
        raise Refused("task prefix must contain 1..24 ASCII letters, digits, underscores or hyphens")
    token = uuid.uuid4().hex
    return ["%s-%s-%02d" % (label, token, index) for index in range(1, count+1)]


def read_robot_map(client, args):
    before = client.request(1300, STATUS_PORT, {})
    if (before.get("current_map"), before.get("current_map_md5")) != (args.expected_map, args.expected_md5):
        raise Refused("current robot map identity differs before 4011 export")
    load = client.request(1022, STATUS_PORT, {}).get("loadmap_status")
    if type(load) is not int or load != 1:
        raise Refused("current robot map is not loaded")
    content = client.request(4011, CONFIG_PORT, {"map_name": args.expected_map})
    after = client.request(1300, STATUS_PORT, {})
    if (after.get("current_map"), after.get("current_map_md5")) != (args.expected_map, args.expected_md5):
        raise Refused("robot map identity changed during 4011 export")
    load = client.request(1022, STATUS_PORT, {}).get("loadmap_status")
    if type(load) is not int or load != 1:
        raise Refused("robot map load state changed during export")
    return content


def query_original(client, task_ids):
    """A single read-only reconciliation, including when 3066 ACK was lost."""
    result = {"segment_ids": task_ids}
    try:
        result["statuses"] = task_rows(client.request(1110, STATUS_PORT, {"task_ids": task_ids}))
    except Exception as exc:
        result["task_query_error"] = str(exc)
    try:
        result["runtime"] = runtime_summary(client.request(1100, STATUS_PORT, {}))
    except Exception as exc:
        result["runtime_query_error"] = str(exc)
    return result


def state_problem(state, args):
    if (state.get("current_map"), state.get("current_map_md5")) != (args.expected_map, args.expected_md5):
        return "robot map identity changed"
    if state.get("emergency") is not False or state.get("soft_emc") is not False:
        return "emergency/soft emergency is active or unknown"
    if type(state.get("blocked")) is not bool:
        return "obstruction state is unknown"
    if state.get("fatals") != [] or not isinstance(state.get("errors"), list):
        return "vehicle fatal/error observation is unavailable"
    if blocking_vehicle_errors(state["errors"]):
        return "vehicle reported a blocking error"
    if not finite(state.get("confidence")) or not 0.5 <= state["confidence"] <= 1.0:
        return "localization confidence is insufficient or unknown"
    if not all(finite(state.get(key)) for key in ("x", "y", "angle", "vx", "vy", "w")):
        return "vehicle pose or velocity observation is invalid"
    if any(state.get(key) is not False for key in ("charging", "manual_charge")):
        return "charging/manual charging is active or unknown"
    return None


def terminal_summary(state, statuses, task_ids, target):
    return {
        "x": state.get("x"),
        "y": state.get("y"),
        "angle": state.get("angle"),
        "current_station": state.get("current_station"),
        "task_status": state.get("task_status"),
        "task_id": state.get("task_id"),
        "is_stop": state.get("is_stop"),
        "chassisIsStop": state.get("chassisIsStop"),
        "blocked": state.get("blocked"),
        "emergency": state.get("emergency"),
        "errors": state.get("errors"),
        "fatals": state.get("fatals"),
        "charging": state.get("charging"),
        "vx": state.get("vx"), "vy": state.get("vy"), "w": state.get("w"),
        "confidence": state.get("confidence"), "soft_emc": state.get("soft_emc"),
        "current_map": state.get("current_map"), "current_map_md5": state.get("current_map_md5"),
        "target_reached": state.get("current_station") == target,
        "all_segments_completed": all(statuses.get(item) == 4 for item in task_ids),
    }


def run_detour(client, args, run_id, backend=None):
    if getattr(args, "isolated_route_confirmed", False) is not True:
        raise Refused("isolated route must be confirmed clear, including disabled vehicles and charging cables; this tool has no traffic reservation")
    backend = backend or BackendReader(args.backend, client.evidence)
    before = preflight(client, args)
    client.evidence.write_json("preflight.json", before)
    robot_map = read_robot_map(client, args)
    traffic = validate_context(backend.read(), args, robot_map)
    task_ids = segment_ids(args.task_prefix, len(args.route)-1)
    moves = []
    for index, (source, target, task_id) in enumerate(
        zip(args.route, args.route[1:], task_ids), 1
    ):
        moves.append(
            {
                "source_id": source,
                "id": target,
                "task_id": task_id,
                "max_speed": args.max_speed,
                "max_wspeed": args.max_wspeed,
                "max_acc": args.max_acc,
                "max_wacc": args.max_wacc,
            }
        )

    payload = {"move_task_list": moves}
    client.evidence.write_json("intended_route.json", payload)
    # Re-read instead of accepting a persisted candidate/old snapshot. There is
    # deliberately no override flag for a changed map, closure, or occupancy.
    latest = preflight(client, args)
    if validate_context(backend.read(), args, robot_map) != traffic:
        raise Refused("backend map, closure or configuration changed before send")
    state = client.request(1100, STATUS_PORT, {})
    if (not stopped(state) or state_problem(state, args) is not None
            or state.get("current_station") != args.route[0]
            or state.get("task_id") != latest["runtime"].get("task_id")
            or state.get("task_status") != latest["runtime"].get("task_status")
            or state.get("unfinished_path") != []
            or type(state.get("script_status")) is not int or state["script_status"] != 0
            or type(state.get("dispatch_mode")) is not int or state["dispatch_mode"] != 0
            or state.get("blocked") is not False
            or state.get("current_lock", {}).get("locked") is not False):
        raise Refused("vehicle changed after the final traffic observation")
    try:
        send_response = client.request(3066, NAV_PORT, payload, mutation=True)
    except ResultUnknown as exc:
        return {"outcome": "unknown", "route": args.route, "segment_ids": task_ids,
                "error": str(exc), "original_task_query": query_original(client, task_ids),
                "message": "3066 result is unknown; query these original IDs. No resend or cancellation was attempted."}
    except Refused as exc:
        return {"outcome": "detour_rejected", "route": args.route, "segment_ids": task_ids,
                "error": str(exc), "message": "3066 was explicitly rejected; no resend was attempted."}
    try:
        return track_detour(client, args, task_ids, send_response, before, traffic)
    except Exception as exc:
        # Even a local disk/evidence error after acceptance must not lose the
        # original chain or imply the move was never submitted.
        return {"outcome": "unknown", "route": args.route, "segment_ids": task_ids,
                "send_response": send_response, "error": str(exc),
                "original_task_query": query_original(client, task_ids),
                "message": "Post-send tracking/evidence failed; query the original IDs. No resend or cancellation was attempted."}


def track_detour(client, args, task_ids, send_response, before, traffic):
    started = time.monotonic()
    deadline = started + args.track_timeout
    blocked_since = None
    tracking = []
    statuses = {}

    while time.monotonic() < deadline:
        try:
            sample_started = time.monotonic()
            state = client.request(1100, STATUS_PORT, {})
            response = client.request(1110, STATUS_PORT, {"task_ids": task_ids})
            # Never merge an earlier completed row into a partial newer response.
            statuses = {key: value for key, value in task_rows(response).items() if key in task_ids}
            if len(statuses) != len(task_ids):
                raise Refused("1110 did not freshly report every original segment ID")
            if time.monotonic()-sample_started > MAX_CONTEXT_AGE_SECONDS:
                raise Refused("paired runtime/task observation is stale")
        except Exception as exc:
            return {"outcome": "unknown", "route": args.route, "segment_ids": task_ids,
                    "send_response": send_response, "error": str(exc),
                    "original_task_query": query_original(client, task_ids),
                    "message": "Tracking is unknown; query the same original chain. No resend or cancellation was attempted."}
        sample = {
            "host_utc": utc_now(),
            "statuses": dict(statuses),
            "x": state.get("x"),
            "y": state.get("y"),
            "angle": state.get("angle"),
            "station": state.get("current_station"),
            "blocked": state.get("blocked"),
            "errors": state.get("errors"),
            "fatals": state.get("fatals"),
            "all_task_rows_fresh": len(statuses) == len(task_ids),
        }
        tracking.append(sample)
        client.evidence.replace_json("tracking.json", tracking)

        failures = [
            task_id for task_id in task_ids
            if statuses.get(task_id) in TERMINAL_FAILURES
        ]
        completed = (
            all(statuses.get(task_id) == 4 for task_id in task_ids)
            and state.get("current_station") == args.route[-1]
            and state.get("task_id") == task_ids[-1]
            and state.get("task_status") == 4
            and stopped(state)
            and state.get("blocked") is False
            and state_problem(state, args) is None
        )
        if completed:
            return {
                "outcome": "detour_completed",
                "route": args.route,
                "segment_ids": task_ids,
                "max_speed_mps": args.max_speed,
                "traffic_observation": traffic,
                "traffic_reservation": False,
                "edges": before["edges"],
                "send_response": send_response,
                "final_statuses": statuses,
                "final_state": terminal_summary(
                    state, statuses, task_ids, args.route[-1]
                ),
            }
        if failures:
            return {
                "outcome": "detour_failed",
                "route": args.route,
                "segment_ids": task_ids,
                "send_response": send_response,
                "failed_task_ids": failures,
                "final_statuses": statuses,
                "final_state": terminal_summary(
                    state, statuses, task_ids, args.route[-1]
                ),
            }

        problem = state_problem(state, args)
        if problem:
            return {"outcome": "attention_required", "route": args.route, "segment_ids": task_ids,
                    "send_response": send_response, "final_statuses": statuses,
                    "final_state": terminal_summary(state, statuses, task_ids, args.route[-1]),
                    "message": problem + "; original chain may still be active. Query before recovery; no resend or cancellation was attempted."}

        if state.get("blocked") is True:
            blocked_since = blocked_since or time.monotonic()
            if time.monotonic() - blocked_since >= args.blocked_timeout:
                return {
                    "outcome": "blocked_waiting",
                    "route": args.route,
                    "segment_ids": task_ids,
                    "send_response": send_response,
                    "final_statuses": statuses,
                    "final_state": terminal_summary(
                        state, statuses, task_ids, args.route[-1]
                    ),
                    "message": "Task remains accepted; move the obstruction and query the same task chain.",
                }
        else:
            blocked_since = None
        time.sleep(args.poll_seconds)

    original = query_original(client, task_ids)
    return {
        "outcome": "tracking_timeout",
        "route": args.route,
        "segment_ids": task_ids,
        "send_response": send_response,
        "original_task_query": original,
        "last_observed_statuses": statuses,
        "message": "No resend was attempted; query the same task chain before any recovery.",
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True, type=ipv4)
    parser.add_argument("--vehicle-id", required=True)
    parser.add_argument("--backend", type=backend_url, default="http://127.0.0.1:5199")
    parser.add_argument("--isolated-route-confirmed", action="store_true",
                        help="all route swept areas, including disabled vehicles and cables, are clear; no concurrent dispatch")
    parser.add_argument("--parked-clearance-m", type=float,
                        help="verified combined vehicle/load envelope radii plus margin; required when other vehicles are enabled")
    parser.add_argument("--expected-id", required=True)
    parser.add_argument("--route", required=True, type=route_value)
    parser.add_argument("--expected-map", required=True)
    parser.add_argument("--expected-md5", required=True)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--task-prefix")
    parser.add_argument("--max-speed", type=float, default=0.3)
    parser.add_argument("--max-wspeed", type=float, default=0.3)
    parser.add_argument("--max-acc", type=float, default=0.5)
    parser.add_argument("--max-wacc", type=float, default=0.5)
    parser.add_argument("--poll-seconds", type=float, default=0.5)
    parser.add_argument("--blocked-timeout", type=float, default=30.0)
    parser.add_argument("--track-timeout", type=float, default=150.0)
    args = parser.parse_args(argv)
    if not 0.05 <= args.max_speed <= 0.5:
        parser.error("--max-speed must be between 0.05 and 0.5")
    if not 0.1 <= args.max_wspeed <= 1.0:
        parser.error("--max-wspeed must be between 0.1 and 1.0")
    if not 0.1 <= args.poll_seconds <= 2.0:
        parser.error("--poll-seconds must be between 0.1 and 2.0")
    if not 10.0 <= args.blocked_timeout <= 120.0:
        parser.error("--blocked-timeout must be between 10 and 120")
    if not 30.0 <= args.track_timeout <= 300.0:
        parser.error("--track-timeout must be between 30 and 300")
    for name in ("max_acc", "max_wacc"):
        if not finite(getattr(args, name)) or not 0.1 <= getattr(args, name) <= 0.5:
            parser.error("--" + name.replace("_", "-") + " must be between 0.1 and 0.5")
    if args.parked_clearance_m is not None and (not finite(args.parked_clearance_m)
                                               or not 0.1 <= args.parked_clearance_m <= 10):
        parser.error("--parked-clearance-m must be between 0.1 and 10")

    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ_") + uuid.uuid4().hex[:12]
    evidence = Evidence(args.evidence_dir, run_id)
    client = Robokit(args.robot, evidence)
    try:
        result = run_detour(client, args, run_id)
        exit_code = 0 if result["outcome"] == "detour_completed" else 3 if result["outcome"] == "unknown" else 2
    except ResultUnknown as exc:
        result = {
            "outcome": "unknown",
            "error": str(exc),
            "message": "No resend was attempted.",
        }
        exit_code = 3
    except Exception as exc:
        result = {"outcome": "failed", "error": str(exc)}
        exit_code = 2
    result.update(
        {
            "robot": args.robot,
            "run_id": run_id,
            "evidence_directory": str(evidence.directory),
            "host_utc": utc_now(),
        }
    )
    evidence.write_json("result.json", result)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
