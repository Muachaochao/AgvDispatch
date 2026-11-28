"""Round 8: build the V5 throughput map and the two comparison schemes.

The V4F/V4FS10 map marks every main-lane edge bidirectional.  The runtime then
adds a capacity-one AUTO-CORRIDOR guard to each of those edges, so two vehicles
meeting on the two parallel vertical lanes still wait for each other.  V5 keeps
the CAD geometry and the 30 safe stopping points, but:

* main-lane edges (eastbound/westbound/northbound/southbound) become one-way;
* every one-way main-lane edge gets an explicit capacity-one LANE-* resource,
  because Enrich no longer adds an automatic guard to a one-way edge;
* crossover / cnc-access / waiting / charging / terminal connectors stay
  bidirectional and keep their automatic guards.

It also derives two schemes from the round-6 charging scheme:

* throughput: all vehicles start healthy, target 65%, two charging bays;
* charging:   two deliberate low-battery vehicles (30/18/22/86/86), target
  65%, two charging bays.

The V5 map keeps the dumper equipment, the final approach segment and the
waiting bays as separate capacity-one resources.  CHARGE-02 gets a dedicated
exit towards CAD2-J0631 instead of sharing the dumper-side J0651 throat.  The
exit is a simulation candidate; its geometry still requires CAD/field
confirmation before the map can be used in production.

Usage:
  python make_round8_variants.py <V4FS10.agvmap.json> <charging-scheme.json> <out_dir>
"""

from __future__ import annotations

import argparse
import copy
import heapq
import json
import math
from collections import defaultdict
from pathlib import Path

MAIN_LANES = ("eastbound", "westbound", "northbound", "southbound")
STOP_TYPES = {"Station", "WaitingPoint", "ChargingPoint", "Intersection"}
DUMPER_ENTRY_RESOURCE = "DUMP-ENTRY"
CHARGE_02_EXIT_EDGE = "SIM-C02-EXIT"
DUAL_LANE_MARKER = "DUAL-LANE-B07-V5"
STAGING_ORIGINS = (
    "WAIT-01", "WAIT-02", "WAIT-03", "WAIT-04", "WAIT-05",
    "SIM-SAFE-CAD2-E00017", "SIM-SAFE-CAD2-E00018", "SIM-SAFE-CAD2-E00030",
    "CHARGE-01", "CHARGE-02",
)


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")


def graph(map_definition: dict) -> dict[str, list[tuple[str, float]]]:
    result: dict[str, list[tuple[str, float]]] = defaultdict(list)
    for edge in map_definition["edges"]:
        if not edge.get("enabled", True):
            continue
        result[edge["from"]].append((edge["to"], float(edge.get("distance") or 1)))
        if edge.get("bidirectional"):
            result[edge["to"]].append((edge["from"], float(edge.get("distance") or 1)))
    return result


def shortest_path(graph_index, source: str, target: str):
    distance = {source: 0.0}
    previous: dict[str, str] = {}
    queue = [(0.0, source)]
    while queue:
        cost, node = heapq.heappop(queue)
        if node == target:
            break
        if cost > distance.get(node, float("inf")):
            continue
        for nxt, weight in graph_index.get(node, ()):
            candidate = cost + weight
            if candidate < distance.get(nxt, float("inf")):
                distance[nxt] = candidate
                previous[nxt] = node
                heapq.heappush(queue, (candidate, nxt))
    if target not in distance:
        return None
    path = [target]
    while path[-1] != source:
        path.append(previous[path[-1]])
    return path[::-1]


def stable(node: dict) -> bool:
    return (node.get("enabled", True)
            and node.get("allowTrafficStop") is not False
            and node.get("type") in STOP_TYPES)


def route_pairs(map_definition: dict) -> list[tuple[str, str, str]]:
    """Every route family the dispatcher actually plans.

    Round 8 first measured only ``station -> WAIT-01`` and
    ``DUMPER-001 -> station``.  The live run then failed on the staging leg
    ``WAIT-01 -> CNC-075`` (67 edges, no interior safe stop).  Staging legs
    are what a waiting vehicle drives before it ever picks up a load, so they
    must be densified and counted here as well.
    """
    node_ids = {node["id"] for node in map_definition["nodes"]}
    stations = sorted(node_id for node_id in node_ids
                      if node_id.startswith("BUS-CNC-"))
    pairs: list[tuple[str, str, str]] = []
    for station in stations:
        pairs.append((station, "WAIT-01", "park"))
        pairs.append(("DUMPER-001", station, "pickup"))
        for origin in STAGING_ORIGINS:
            if origin in node_ids:
                pairs.append((origin, station, "staging"))
    return pairs


def window_stats(map_definition: dict) -> dict:
    nodes = {node["id"]: node for node in map_definition["nodes"]}
    graph_index = graph(map_definition)
    routes = windows = atomic = 0
    longest = 0
    longest_route: tuple[str, str, str] | None = None
    missing: list[tuple[str, str]] = []
    by_kind: dict[str, dict] = {}
    for source, target, kind in route_pairs(map_definition):
        path = shortest_path(graph_index, source, target)
        if not path:
            missing.append((source, target))
            continue
        routes += 1
        bucket = by_kind.setdefault(kind, {"routes": 0, "windows": 0, "longest": 0})
        bucket["routes"] += 1
        start = 0
        route_windows = 0
        route_longest = 0
        while start < len(path) - 1:
            remaining = len(path) - 1 - start
            if remaining <= 2:
                route_windows += 1
                route_longest = max(route_longest, remaining)
                break
            boundary = next((index for index in range(start + 2, len(path) - 1)
                             if stable(nodes[path[index]])), -1)
            route_windows += 1
            if boundary < 0:
                route_longest = max(route_longest, remaining)
                break
            route_longest = max(route_longest, boundary - start)
            start = boundary
        windows += route_windows
        bucket["windows"] += route_windows
        bucket["longest"] = max(bucket["longest"], route_longest)
        atomic += route_windows == 1
        if route_longest > longest:
            longest = route_longest
            longest_route = (source, target, kind)
    for bucket in by_kind.values():
        bucket["avgWindowsPerRoute"] = round(bucket["windows"] / bucket["routes"], 2) if bucket["routes"] else 0
    return dict(routes=routes, missing=len(missing), windows=windows,
                atomic=atomic,
                avgWindowsPerRoute=round(windows / routes, 2) if routes else 0,
                longestWindowEdges=longest,
                longestRoute=" -> ".join(longest_route) if longest_route else None,
                byKind=by_kind)


def add_dual_lane_marker(map_definition: dict) -> None:
    """Opt out of the runtime AUTO-CORRIDOR guards.

    ``MapCorridorResources.Enrich`` returns the authored topology unchanged as
    soon as it sees a ``DUAL-LANE-`` resource.  V5 already gives every main-lane
    edge an explicit capacity-one ``LANE-*`` resource and every node an explicit
    ``X-*`` resource, so the extra WASM guard would only duplicate the same gate
    and keep it leased at neighbouring safe points.
    """
    resources = {resource["id"]: resource for resource in map_definition["resources"]}
    if DUAL_LANE_MARKER in resources:
        return
    xs = [node["x"] for node in map_definition["nodes"]]
    ys = [node["y"] for node in map_definition["nodes"]]
    resources[DUAL_LANE_MARKER] = {
        "id": DUAL_LANE_MARKER,
        "type": "Intersection",
        "capacity": 1000,
        "layerId": "navigation",
        "boundary": [
            {"x": min(xs), "y": min(ys)},
            {"x": max(xs), "y": min(ys)},
            {"x": max(xs), "y": max(ys)},
            {"x": min(xs), "y": max(ys)},
        ],
    }
    map_definition["resources"] = list(resources.values())


def add_lane_resources(map_definition: dict, lane_mode: str = "oneway") -> list[str]:
    nodes = {node["id"]: node for node in map_definition["nodes"]}
    resources = {resource["id"]: resource for resource in map_definition["resources"]}
    changed: list[str] = []
    for edge in map_definition["edges"]:
        if edge.get("laneRole") not in MAIN_LANES:
            continue
        edge["bidirectional"] = lane_mode == "twoway"
        resource_id = f"LANE-{edge['id']}"
        if resource_id not in edge["resourceIds"]:
            edge["resourceIds"].append(resource_id)
        if resource_id not in resources:
            start = nodes[edge["from"]]
            end = nodes[edge["to"]]
            padding = 0.6
            min_x = min(start["x"], end["x"]) - padding
            max_x = max(start["x"], end["x"]) + padding
            min_y = min(start["y"], end["y"]) - padding
            max_y = max(start["y"], end["y"]) + padding
            resources[resource_id] = {
                "id": resource_id,
                "type": "Intersection",
                "capacity": 1,
                "layerId": "navigation",
                "boundary": [
                    {"x": min_x, "y": min_y},
                    {"x": max_x, "y": min_y},
                    {"x": max_x, "y": max_y},
                    {"x": min_x, "y": max_y},
                ],
            }
        changed.append(edge["id"])
    map_definition["resources"] = list(resources.values())
    return changed


def segment_resource(resource_id: str, start: dict, end: dict,
                     padding: float = 0.5) -> dict:
    return {
        "id": resource_id,
        "type": "Intersection",
        "capacity": 1,
        "layerId": "navigation",
        "boundary": [
            {"x": min(start["x"], end["x"]) - padding, "y": min(start["y"], end["y"]) - padding},
            {"x": max(start["x"], end["x"]) + padding, "y": min(start["y"], end["y"]) - padding},
            {"x": max(start["x"], end["x"]) + padding, "y": max(start["y"], end["y"]) + padding},
            {"x": min(start["x"], end["x"]) - padding, "y": max(start["y"], end["y"]) + padding},
        ],
    }


def add_dumper_entry_resource(map_definition: dict) -> str | None:
    """Keep the dumper equipment and its final approach as separate resources.

    The waiting node already owns a capacity-one WAIT-* resource.  Adding
    DUMP-ENTRY to only the final dumper-access edge gives the R19 admission
    path three independently visible stages: waiting bay, entry segment,
    dumper equipment.  Upstream waiting/collector segments remain ordinary
    rolling-window resources and do not have to be empty at once.
    """
    nodes = {node["id"]: node for node in map_definition["nodes"]}
    dumper = nodes.get("DUMPER-001")
    if dumper is None:
        return None
    approach = [edge for edge in map_definition["edges"]
                if edge.get("enabled", True)
                and edge.get("laneRole") == "dumper-access"
                and "DUMPER-001" in (edge["from"], edge["to"])]
    if not approach:
        return None
    resources = {resource["id"]: resource for resource in map_definition["resources"]}
    if DUMPER_ENTRY_RESOURCE not in resources:
        ends = [nodes[edge["from"]] for edge in approach] + [nodes[edge["to"]] for edge in approach]
        resource = segment_resource(DUMPER_ENTRY_RESOURCE,
                                    {"x": min(node["x"] for node in ends),
                                     "y": min(node["y"] for node in ends)},
                                    {"x": max(node["x"] for node in ends),
                                     "y": max(node["y"] for node in ends)})
        resources[DUMPER_ENTRY_RESOURCE] = resource
    for edge in approach:
        if DUMPER_ENTRY_RESOURCE not in edge["resourceIds"]:
            edge["resourceIds"].append(DUMPER_ENTRY_RESOURCE)
    map_definition["resources"] = list(resources.values())
    return DUMPER_ENTRY_RESOURCE


def add_charge02_exit(map_definition: dict) -> bool:
    """Give CHARGE-02 an exit that does not pass the dumper throat.

    The existing J0651 -> CHARGE-02 edge remains the entrance.  The reverse
    edge through J0651 is disabled and replaced by a direct simulation edge to
    CAD2-J0631.  This is intentionally marked as a candidate because the CAD
    clearance around the new diagonal still needs field verification.
    """
    nodes = {node["id"]: node for node in map_definition["nodes"]}
    charge = nodes.get("CHARGE-02")
    exit_node = nodes.get("CAD2-J0631")
    if charge is None or exit_node is None:
        return False
    reverse = next((edge for edge in map_definition["edges"]
                    if edge.get("enabled", True)
                    and edge.get("laneRole") == "charging-access"
                    and edge["from"] == "CHARGE-02"
                    and edge["to"] == "CAD2-J0651"), None)
    if reverse is not None:
        reverse["enabled"] = False
        if "SIM-ONLY-CHARGE02-SEPARATE-EXIT" not in reverse.setdefault("notes", []):
            reverse["notes"].append("SIM-ONLY-CHARGE02-SEPARATE-EXIT")
    if any(edge["id"] == CHARGE_02_EXIT_EDGE for edge in map_definition["edges"]):
        return True
    distance = ((charge["x"] - exit_node["x"]) ** 2
                + (charge["y"] - exit_node["y"]) ** 2) ** 0.5
    map_definition["edges"].append({
        "id": CHARGE_02_EXIT_EDGE,
        "from": "CHARGE-02",
        "to": "CAD2-J0631",
        "distance": distance,
        "bidirectional": False,
        "laneRole": "charging-exit",
        "resourceIds": ["CHARGE-02", "SEG-" + CHARGE_02_EXIT_EDGE, "X-CAD2-J0631"],
        "enabled": True,
        "notes": ["SIM-ONLY candidate exit; CAD clearance not yet verified"],
    })
    resources = {resource["id"]: resource for resource in map_definition["resources"]}
    for resource_id in (CHARGE_02_EXIT_EDGE, "SEG-" + CHARGE_02_EXIT_EDGE):
        resources[resource_id] = segment_resource(resource_id, charge, exit_node)
    map_definition["resources"] = list(resources.values())
    return True


def boundary_box(boundary, name):
    if not isinstance(boundary, list) or len(boundary) < 3:
        raise ValueError(f"{name}缺少地图边界。")
    if any(not isinstance(point, dict) for point in boundary):
        raise ValueError(f"{name}边界坐标无效。")
    x = [point.get("x") for point in boundary]
    y = [point.get("y") for point in boundary]
    if not all(type(value) in (float, int) and math.isfinite(value) for value in x + y):
        raise ValueError(f"{name}边界坐标无效。")
    if min(x) == max(x) or min(y) == max(y):
        raise ValueError(f"{name}边界没有面积。")
    return ((min(x), max(x)), (min(y), max(y)))


def meets_expanded_box(start, end, box, radius):
    """Conservative overlap of a straight edge's swept circle and a polygon AABB."""
    entry, leave = 0.0, 1.0
    for axis, (low, high) in zip(("x", "y"), box):
        origin = start[axis]
        delta = end[axis] - origin
        if delta == 0:
            if not low - radius <= origin <= high + radius:
                return False
            continue
        first, last = sorted(((low - radius - origin) / delta, (high + radius - origin) / delta))
        entry, leave = max(entry, first), min(leave, last)
        if entry > leave:
            return False
    return True


def segment_distance(point, start, end):
    dx, dy = end["x"] - start["x"], end["y"] - start["y"]
    px, py = point["x"] - start["x"], point["y"] - start["y"]
    fraction = max(0., min(1., (px * dx + py * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - fraction * dx, py - fraction * dy)


def validate_charging_exit_turn_clearance(definition, charging, waiting_sites, vehicles):
    from footprint import rectangles_overlap

    if definition.get("unit") != "m":
        return
    nodes = {node["id"]: node for node in definition["nodes"] if node.get("enabled", True)}
    approaches = defaultdict(set)
    for edge in definition["edges"]:
        if (edge.get("enabled", True) and edge.get("laneRole") == "waiting-bay" and
                edge.get("geometryKind", "Line") == "Line" and edge["from"] in nodes and edge["to"] in nodes):
            approaches[edge["from"]].add(edge["to"])
            approaches[edge["to"]].add(edge["from"])
    stops = set(waiting_sites) | set(charging["waitingSiteIds"])
    stops.update(vehicle["initialSite"] for vehicle in vehicles)
    stops.update(site["exitSiteId"] for site in charging["sites"])
    length = max(vehicle["lengthMeters"] for vehicle in vehicles)
    width = max(vehicle["widthMeters"] for vehicle in vehicles)

    def pose_at(node_id, angle):
        node = nodes[node_id]
        return dict(x=node["x"], y=node["y"], angle=angle, lengthMeters=length, widthMeters=width)

    def heading(junction_id, bay_id):
        junction, bay = nodes[junction_id], nodes[bay_id]
        return math.atan2(bay["y"] - junction["y"], bay["x"] - junction["x"])

    for site in charging["sites"]:
        exit_id = site["exitSiteId"]
        for junction in sorted(approaches[exit_id]):
            parked = pose_at(exit_id, heading(junction, exit_id))
            for other in sorted((approaches[junction] & stops) - {exit_id}):
                turning = pose_at(junction, heading(junction, other))
                if rectangles_overlap(parked, turning, margin=0.05):
                    raise ValueError(f"充电出口 {exit_id} 的停车包络占用 {junction} → {other} 的转向空间。"
                                     "请调整充电出口或候车位配置，再运行仿真。")
                other_parked = pose_at(other, heading(junction, other))
                turning_to_exit = pose_at(junction, heading(junction, exit_id))
                if rectangles_overlap(other_parked, turning_to_exit, margin=0.05):
                    raise ValueError(f"停靠位 {other} 的停车包络占用 {junction} → {exit_id} 的充电退出空间。"
                                     "请调整充电出口或候车位配置，再运行仿真。")


def validate_charge02_vertical_inlet(definition, configuration):
    nodes = {node["id"]: node for node in definition["nodes"] if node.get("enabled", True)}
    edges = {edge["id"]: edge for edge in definition["edges"]}
    equipment = next(resource for resource in definition["resources"] if resource["id"] == "DUMPER-001")
    entry, crossing, dock, other, dumper = (nodes[key] for key in
        ("CAD2-J0652", "CAD2-J0651", "CHARGE-02", "CHARGE-01", "DUMPER-001"))
    trunk, inlet = (edges[key] for key in ("CAD2-E01834", "CAD2-E01859"))
    platform = next(area for area in definition.get("areaAnnotations", [])
                    if area["id"] == "CAD-TERMINAL-PLATFORM")
    operating = boundary_box(equipment.get("boundary"), "倒料作业区")
    fixed = boundary_box(platform.get("boundary"), "固定平台")
    radius = max(max(vehicle["radiusMeters"], math.hypot(
        vehicle.get("lengthMeters", 0), vehicle.get("widthMeters", 0)) / 2)
        for vehicle in configuration["vehicles"])
    if (definition.get("unit") != "m" or not 0 < radius < math.inf
            or configuration["dumper"]["resourceId"] != equipment["id"]
            or configuration["dumper"]["dumpingSite"] != dumper["id"]
            or equipment["capacity"] != 1 or equipment["id"] not in dumper["resourceIds"]
            or equipment["id"] in dock["resourceIds"]):
        raise ValueError("二号桩竖线缺少有效的米制包络或倒料独占资源绑定。")
    if ((trunk["from"], trunk["to"]) != (crossing["id"], entry["id"]) or not trunk.get("bidirectional")
            or (inlet["from"], inlet["to"]) != (crossing["id"], dock["id"]) or inlet.get("bidirectional")
            or any(not edge.get("enabled", True) or edge.get("geometryKind", "Line") != "Line"
                   or equipment["id"] not in edge["resourceIds"] for edge in (trunk, inlet))
            or any(edge.get("enabled", True) and (edge["from"], edge["to"]) == (dock["id"], crossing["id"])
                   for edge in definition["edges"])
            or any(node.get("allowTrafficStop") is not False for node in (entry, crossing))):
        raise ValueError("二号桩竖线方向、独占资源或不可停车条件不成立。")
    if (not math.isclose(entry["x"], crossing["x"], abs_tol=1e-6)
            or not math.isclose(dock["x"], crossing["x"], abs_tol=1e-6)
            or not dock["y"] < crossing["y"] < entry["y"]):
        raise ValueError("二号桩竖线几何与核对路径不一致。")
    if meets_expanded_box(entry, dock, fixed, radius) or segment_distance(other, entry, dock) <= 2 * radius:
        raise ValueError("二号桩竖线包络与固定平台或一号桩停靠车体相交。")
    if (any(meets_expanded_box(node, node, operating, radius) for node in (entry, dock))
            or not meets_expanded_box(crossing, crossing, operating, radius)
            or math.hypot(dock["x"] - dumper["x"], dock["y"] - dumper["y"]) <= 2 * radius):
        raise ValueError("二号桩竖线无法在进入前及停稳后让出倒料作业区。")
    return dict(edgeIds=[trunk["id"], inlet["id"]], fromSite=entry["id"],
                toSite=dock["id"], radiusMeters=radius, sharedDumperResource=equipment["id"],
                distanceFromDumperMeters=segment_distance(dumper, entry, dock),
                separationFromOtherParkedCenterMeters=segment_distance(other, entry, dock))


def charge02_shared_access(source_map, source_scheme, *, vertical_inlet=False):
    """Derive a B07 simulation candidate with checked, guarded diagonal access."""
    definition = copy.deepcopy(source_map)
    scheme = copy.deepcopy(source_scheme)
    configuration = scheme["configuration"]
    nodes = {node["id"]: node for node in definition["nodes"] if node.get("enabled", True)}
    resources = {row["id"]: row for row in definition["resources"]}
    platform = next((area for area in definition.get("areaAnnotations", [])
                     if area["id"] == "CAD-TERMINAL-PLATFORM"), {})
    dumper = configuration["dumper"]
    equipment = resources.get(dumper["resourceId"], {})
    if (definition.get("unit") != "m" or equipment.get("capacity") != 1
            or dumper["resourceId"] not in nodes.get(dumper["dumpingSite"], {}).get("resourceIds", [])):
        raise ValueError("需要米制地图和绑定到倒料位的独占倒料资源。")
    sites = configuration.get("chargingPriorityTest", {})
    site_ids = {site["chargingSiteId"]: site["resourceId"] for site in sites.get("sites", [])}
    if (not sites.get("enabled") or set(site_ids) != {"CHARGE-01", "CHARGE-02"}
            or len(set(site_ids.values())) != 2
            or any(resources.get(resource, {}).get("capacity") != 1
                   or resources.get(resource, {}).get("type") != "ChargingBay"
                   or resource not in nodes.get(site, {}).get("resourceIds", [])
                   for site, resource in site_ids.items())):
        raise ValueError("需要两个已启用且分别绑定独占资源的充电位。")
    radius = max(max(vehicle.get("radiusMeters", 0),
                     math.hypot(vehicle.get("lengthMeters", 0), vehicle.get("widthMeters", 0)) / 2)
                 for vehicle in configuration["vehicles"])
    if not math.isfinite(radius) or radius <= 0:
        raise ValueError("车辆包络半径无效。")

    start, dock, other = (nodes[key] for key in ("CAD2-J0631", "CHARGE-02", "CHARGE-01"))
    diagonal = next((edge for edge in definition["edges"] if edge["id"] == CHARGE_02_EXIT_EDGE), None)
    if (diagonal is None or not diagonal.get("enabled", True)
            or (diagonal["from"], diagonal["to"]) != (dock["id"], start["id"])
            or diagonal.get("geometryKind", "Line") != "Line"):
        raise ValueError("未找到 B07 二号桩的有效直线斜接驳。")
    if (start["x"], start["y"]) == (dock["x"], dock["y"]):
        raise ValueError("二号桩斜接驳端点重合。")
    fixed = boundary_box(platform.get("boundary"), "固定平台")
    operating = boundary_box(equipment.get("boundary"), "倒料作业区")
    if meets_expanded_box(start, dock, fixed, radius):
        raise ValueError("二号桩斜线包络侵入固定平台，禁止通行。")
    if segment_distance(other, start, dock) <= 2 * radius:
        raise ValueError("二号桩斜线包络与一号桩停靠车体相交，禁止通行。")
    if meets_expanded_box(dock, dock, operating, radius):
        raise ValueError("二号桩停稳后车体仍占用倒料作业区，无法释放共享路权。")
    if meets_expanded_box(start, start, operating, radius):
        raise ValueError("二号桩斜线入口会停在倒料作业区内，不能作为接驳边界。")
    shared = equipment["id"] if meets_expanded_box(start, dock, operating, radius) else None
    if shared in dock.get("resourceIds", []):
        raise ValueError("二号桩停稳后仍会持有倒料资源。")
    diagonal["bidirectional"] = True
    diagonal["laneRole"] = "charging-access"
    diagonal["displayName"] = "二号桩斜线接驳"
    diagonal["notes"] = ["SIM-ONLY checked against modeled platform and dumper boundaries"]
    diagonal["resourceIds"] = list(dict.fromkeys(
        diagonal["resourceIds"] + start["resourceIds"] + [site_ids["CHARGE-02"]]
        + ([shared] if shared else [])
    ))
    for edge in definition["edges"]:
        if {edge["from"], edge["to"]} == {"CAD2-J0651", "CHARGE-02"}:
            edge["enabled"] = False
    validate_resource_references(definition)
    definition["name"] = f'{source_map["name"]}·二号桩斜线'
    scheme.pop("id", None)
    scheme.pop("updatedAt", None)
    scheme["name"] = "B07·五车十单·双桩斜线进出"
    configuration.update(name=scheme["name"], mapDefinition=definition, mapIdentity={
        "mapId": "", "mapVersionId": "", "sourceName": definition["name"],
    })
    audit = dict(edgeId=diagonal["id"], fromSite=start["id"], toSite=dock["id"],
                 radiusMeters=radius, sharedDumperResource=shared, resourceIds=diagonal["resourceIds"],
                 separationFromOtherParkedCenterMeters=segment_distance(other, start, dock))
    if vertical_inlet:
        if shared != "DUMPER-001":
            raise ValueError("当前方案不具备已核对的二号桩共享接驳。")
        edges = {edge["id"]: edge for edge in definition["edges"]}
        for key in ("CAD2-E01834", "CAD2-E01859"):
            edge = edges[key]
            edge["enabled"] = True
            edge["resourceIds"] = list(dict.fromkeys(edge["resourceIds"] + [shared]))
        edges["CAD2-E01859"]["laneRole"] = "charging-vertical-inlet"
        audit["verticalInlet"] = validate_charge02_vertical_inlet(definition, configuration)
        definition["name"] = f'{source_map["name"]}·二号桩竖线进桩'
        scheme["name"] = "B07·五车十单·双桩竖线进桩"
        configuration.update(name=scheme["name"], mapIdentity={
            "mapId": "", "mapVersionId": "", "sourceName": definition["name"],
        })
    return scheme, audit


def decouple_charge02_approach(map_definition: dict) -> str | None:
    """Let CHARGE-02 be entered from the north without crossing J0631.

    Both charging bays are side by side.  CHARGE-01 is entered through
    J0632 -> J0631, while CHARGE-02 could only be reached through the same
    J0631 throat because CAD2-E01834 (J0651 -> J0652) was one-way.  While one
    vehicle used BAY-01, BAY-02 stayed physically free but had no route, which
    is exactly the "second bay never used" symptom in the round-8 charging run.
    Making that short terminal link bidirectional gives BAY-02 its own approach
    through J0652 -> J0651 without adding a duplicate reverse edge.
    """
    target = next((edge for edge in map_definition["edges"]
                   if edge.get("id") == "CAD2-E01834"), None)
    if target is None or target.get("bidirectional"):
        return None
    reverse = next((edge for edge in map_definition["edges"]
                    if edge is not target and edge.get("enabled", True)
                    and edge.get("from") == target["to"]
                    and edge.get("to") == target["from"]), None)
    if reverse is not None:
        return None
    target["bidirectional"] = True
    notes = target.setdefault("notes", [])
    note = "SIM-ONLY-CHARGE02-INDEPENDENT-APPROACH"
    if note not in notes:
        notes.append(note)
    return target["id"]


def route_paths(map_definition: dict) -> list[list[str]]:
    graph_index = graph(map_definition)
    paths: list[list[str]] = []
    for source, target, _ in route_pairs(map_definition):
        path = shortest_path(graph_index, source, target)
        if path:
            paths.append(path)
    return paths


def validate_resource_references(map_definition: dict) -> None:
    resource_ids = {resource["id"] for resource in map_definition["resources"]}
    missing = []
    for node in map_definition["nodes"]:
        missing.extend((node["id"], resource_id) for resource_id in node.get("resourceIds") or []
                       if resource_id not in resource_ids)
    for edge in map_definition["edges"]:
        missing.extend((edge["id"], resource_id) for resource_id in edge.get("resourceIds") or []
                       if resource_id not in resource_ids)
    if missing:
        sample = "；".join(f"{owner}->{resource_id}" for owner, resource_id in missing[:5])
        raise SystemExit(f"V5 references {len(missing)} undefined resources: {sample}")


def windows_of(path: list[str], nodes: dict[str, dict], minimum: int = 2):
    start = 0
    while start < len(path) - 1:
        remaining = len(path) - 1 - start
        if remaining <= minimum:
            yield start, len(path) - 1
            return
        boundary = next((index for index in range(start + minimum, len(path) - 1)
                         if stable(nodes[path[index]])), -1)
        if boundary < 0:
            yield start, len(path) - 1
            return
        yield start, boundary
        start = boundary


def ensure_resource(map_definition: dict, node: dict) -> None:
    if node.get("resourceIds"):
        return
    resource_id = f"SAFE-{node['id']}"
    node["resourceIds"] = [resource_id]
    padding = 0.6
    map_definition["resources"].append({
        "id": resource_id,
        "type": "Intersection",
        "capacity": 1,
        "layerId": "navigation",
        "boundary": [
            {"x": node["x"] - padding, "y": node["y"] - padding},
            {"x": node["x"] + padding, "y": node["y"] - padding},
            {"x": node["x"] + padding, "y": node["y"] + padding},
            {"x": node["x"] - padding, "y": node["y"] + padding},
        ],
    })


def densify_safe_points(map_definition: dict, max_edges: int) -> list[str]:
    nodes = {node["id"]: node for node in map_definition["nodes"]}
    promoted: list[str] = []
    # Edge geometry does not change while points are promoted, so one shortest
    # path per route family is enough for the whole densification loop.
    paths = route_paths(map_definition)
    for _ in range(8):
        added = 0
        for path in paths:
            for start, end in windows_of(path, nodes):
                if end - start <= max_edges:
                    continue
                middle = path[(start + end) // 2]
                # Prefer the exact midpoint, but walk outwards when that node is
                # already stable or is not a promotable Waypoint.
                span = max(1, (end - start) // 4)
                candidates = sorted(
                    range(max(start + 1, (start + end) // 2 - span),
                          min(end, (start + end) // 2 + span) + 1),
                    key=lambda index: abs(index - (start + end) // 2))
                chosen = next((path[index] for index in candidates
                               if not stable(nodes[path[index]])
                               and nodes[path[index]].get("type") == "Waypoint"), None)
                if chosen is None:
                    continue
                node = nodes[chosen]
                node["type"] = "Intersection"
                node["allowTrafficStop"] = True
                ensure_resource(map_definition, node)
                promoted.append(chosen)
                added += 1
        if added == 0:
            break
    return promoted


def build_map(source_map: dict, max_window_edges: int,
              lane_mode: str = "oneway") -> tuple[dict, list[str], list[str]]:
    map_definition = copy.deepcopy(source_map)
    changed = add_lane_resources(map_definition, lane_mode)
    promoted = densify_safe_points(map_definition, max_window_edges)
    add_dumper_entry_resource(map_definition)
    add_charge02_exit(map_definition)
    decouple_charge02_approach(map_definition)
    add_dual_lane_marker(map_definition)
    suffix = "单行主干" if lane_mode == "oneway" else "双向主干"
    map_definition["name"] = f"B07-V5·{suffix}+安全点+独立倒料入口"
    return map_definition, changed, promoted


def scheme_with(charging_scheme: dict, map_definition: dict, *,
                batteries: list[float], target: float, name: str,
                charging_enabled: bool) -> dict:
    scheme = copy.deepcopy(charging_scheme)
    scheme["name"] = name
    configuration = scheme["configuration"]
    configuration["name"] = scheme["name"]
    configuration["mapDefinition"] = copy.deepcopy(map_definition)
    # The scheme is a full configuration snapshot, not only a map blob.  Keep
    # the identity in sync with the embedded definition so the controller does
    # not keep advertising the previous V4F source name/hash.
    configuration["mapIdentity"] = {
        "mapId": "",
        "mapVersionId": "",
        "sourceName": map_definition["name"],
    }

    vehicles = configuration["vehicles"]
    initial_sites = ["WAIT-01", "WAIT-02", "WAIT-03",
                     "SIM-SAFE-CAD2-E00018", "SIM-SAFE-CAD2-E00030"]
    for index, vehicle in enumerate(vehicles):
        vehicle["initialSite"] = initial_sites[index]
        vehicle["batteryPercent"] = batteries[index]
        vehicle["initialLoaded"] = False

    configuration["chargingPriorityTest"] = {
        "enabled": charging_enabled,
        "startBatteryPercent": 25,
        "criticalBatteryPercent": 15,
        "targetBatteryPercent": target,
        "returnToStandbyAfterCharging": True,
        "defaultStandbySiteId": "SIM-SAFE-CAD2-E00017",
        "minimumAvailableVehicles": 3,
        "maximumVehiclesOutOfService": 2,
        "preemptionReleaseBatteryPercent": 60,
        "sites": [
            {
                "id": "BAY-01",
                "chargingSiteId": "CHARGE-01",
                "exitSiteId": "WAIT-05",
                "resourceId": "CHARGE-01",
                "allowedVehicleIds": [vehicle["id"] for vehicle in vehicles],
            },
            {
                "id": "BAY-02",
                "chargingSiteId": "CHARGE-02",
                "exitSiteId": "WAIT-04",
                "resourceId": "CHARGE-02",
                "allowedVehicleIds": [vehicle["id"] for vehicle in vehicles],
            },
        ],
    }
    # Scenario names are part of the controller's closed vocabulary; keep the
    # dispatch-backlog runner and put the human-readable V5 label on the scheme.
    scheme["scenario"]["name"] = "dispatch-backlog"
    scheme["scenario"]["timeoutSeconds"] = 9000
    return scheme


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("map")
    parser.add_argument("scheme")
    parser.add_argument("out")
    parser.add_argument("--max-window-edges", type=int, default=24)
    parser.add_argument("--lane-mode", choices=("oneway", "twoway"), default="oneway")
    parser.add_argument("--charge02-shared-access", action="store_true")
    parser.add_argument("--charge02-vertical-inlet", action="store_true")
    args = parser.parse_args()
    if args.charge02_vertical_inlet and not args.charge02_shared_access:
        parser.error("--charge02-vertical-inlet requires --charge02-shared-access")

    source_map = load(Path(args.map))
    charging_scheme = load(Path(args.scheme))
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    if args.charge02_shared_access:
        candidate, audit = charge02_shared_access(source_map, charging_scheme, vertical_inlet=args.charge02_vertical_inlet)
        variant = "双桩竖线进桩" if args.charge02_vertical_inlet else "双桩斜线"
        write(out / f"B07-{variant}.agvmap.json", candidate["configuration"]["mapDefinition"])
        write(out / f"方案-B07-五车十单-{variant}.json", candidate)
        write(out / "二号桩接驳核对.json", audit)
        return

    map_definition, changed, promoted = build_map(source_map, args.max_window_edges, args.lane_mode)
    stats = window_stats(map_definition)
    source_stats = window_stats(source_map)
    validate_resource_references(map_definition)
    print("V5 lane edges changed:", len(changed))
    print("V5 safe points added:", len(promoted))
    print("V5 window stats:", stats)
    if stats["missing"]:
        raise SystemExit(f"V5 has {stats['missing']} unreachable routes; refusing to write.")
    write(out / "B07-V5.agvmap.json", map_definition)

    throughput = scheme_with(charging_scheme, map_definition,
                             batteries=[86, 86, 86, 86, 86], target=65,
                             name="B07-V5·双桩吞吐对照", charging_enabled=False)
    charging = scheme_with(charging_scheme, map_definition,
                           batteries=[30, 18, 22, 86, 86], target=65,
                           name="B07-V5·双桩充电对照", charging_enabled=True)
    write(out / "方案-B07-V5-双桩吞吐对照.json", throughput)
    write(out / "方案-B07-V5-双桩充电对照.json", charging)
    write(out / "v5-window-stats.json", stats)
    write(out / "v5-map-config-summary.json", {
        "map": {
            "name": map_definition["name"],
            "laneMode": args.lane_mode,
            "nodes": len(map_definition["nodes"]),
            "edges": len(map_definition["edges"]),
            "resources": len(map_definition["resources"]),
            "oneWayMainLaneEdges": len(changed),
            "safePointsAddedThisRun": promoted,
            "dumperEntryResource": DUMPER_ENTRY_RESOURCE,
            "charge02ExitEdge": CHARGE_02_EXIT_EDGE,
            "charge02LegacyExitDisabled": "CAD2-E01860",
            "charge02IndependentApproach": "CAD2-E01834",
        },
        "windows": {"source": source_stats, "v5": stats},
        "charging": {
            "startBatteryPercent": 25,
            "criticalBatteryPercent": 15,
            "targetBatteryPercent": 65,
            "throughputInitialBatteryPercent": [86, 86, 86, 86, 86],
            "chargingInitialBatteryPercent": [30, 18, 22, 86, 86],
            "chargingSites": 2,
            "minimumAvailableVehicles": 3,
            "maximumVehiclesOutOfService": 2,
            "preemptionReleaseBatteryPercent": 60,
            "bay01Exit": "WAIT-05",
            "bay02Exit": "WAIT-04",
        },
        "scenarioTimeoutSeconds": 9000,
        "boundary": "Simulation candidate. CHARGE-02 dedicated exit, CAD clearance and production acceptance remain unverified.",
    })
    print("written:", out)


if __name__ == "__main__":
    main()
