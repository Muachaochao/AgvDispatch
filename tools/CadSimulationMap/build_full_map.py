#!/usr/bin/env python3
"""Build the full B07-1F factory simulation map from the reviewed CAD.

The 305 AGV-served CNC positions are detected from the reviewed lower-layout
preview using its known CAD render view box. The first two repeated equipment
rows shown by the drawing are not part of the 305-CNC scope: the remaining nine
rows contain exactly 305 machine symbols, matching the approved scheme.

The route graph follows the CAD corridor bands around those rows and the south
terminal area. It is a simulation topology, not a supplier SMAP export and not a
claim that every generated lane centreline has been physically surveyed.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import heapq
import io
import json
import math
import re
import shutil
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "WebSingleVehicleLab"))
from footprint import swept_rectangles_overlap


MAP_NAME = "B07-1F·全厂305CNC·五车仿真图"
DEFAULT_VIEWBOX = {
    "minX": 856223.3847910452,
    "minY": -459324.8267135731,
    "width": 194437.40563101223,
    "height": 124995.47504850785,
    "pixelWidth": 2800,
    "pixelHeight": 1800,
}
EXPECTED_ROW_COUNTS = [26, 36, 37, 36, 36, 37, 24, 37, 36]
GROUPS = ((0, 1), (2, 3), (4, 5), (6, 7), (8,))
MAP_GROUP_NAMES = ("一组", "二组", "三组", "四组", "末组")

# The active working area's CAD geometry bounds. These are corridor boundaries
# visible in the reviewed drawing, not inferred equipment outlines.
WEST_MM = 910916.525
EAST_MM = 1031462.795
NORTH_MM = -390015.223
SOUTH_MM = -425445.634

# The five AGV text locations and two charger text locations are taken directly
# from the reviewed lower-layout DXF.
WAIT_MM = [
    (984450.8609652442, -428561.59923885955),
    (985825.6767751111, -428578.72290093364),
    (987181.4261508023, -430295.2984376564),
    (985818.8446768655, -430290.1226494930),
    (987209.3125845995, -428608.67235489975),
]
CHARGER_MM = [
    (981426.4864161902, -433062.96251952526),
    (983045.5255125582, -433098.0437090481),
]
DUMPER_MM = (984477.3124495584, -432704.6381259541)
INSPECTION_MM = (987364.0430463131, -431900.0000000000)

# Active lower-layout CAD labels that are part of the deployment drawing but are
# not all AGV-service stations. Keep them as traceable reference points so the
# simulation view does not silently drop factory equipment or imply that a
# visual marker is an approved navigation destination.
CAD_REFERENCE_POINTS = [
    # Ten east-side dock labels, ordered from north to south.
    {"id": "CAD-DOCK-01", "category": "dock", "text": "东侧码头1", "xMm": 1044820.796, "yMm": -386042.345, "handle": "82CD3"},
    {"id": "CAD-DOCK-02", "category": "dock", "text": "东侧码头2", "xMm": 1044820.796, "yMm": -389642.345, "handle": "82CD4"},
    {"id": "CAD-DOCK-03", "category": "dock", "text": "东侧码头3", "xMm": 1044820.796, "yMm": -394393.643, "handle": "82CFE"},
    {"id": "CAD-DOCK-04", "category": "dock", "text": "东侧码头4", "xMm": 1044820.796, "yMm": -397993.643, "handle": "82CF7"},
    {"id": "CAD-DOCK-05", "category": "dock", "text": "东侧码头5", "xMm": 1044820.796, "yMm": -401642.345, "handle": "82CF6"},
    {"id": "CAD-DOCK-06", "category": "dock", "text": "东侧码头6", "xMm": 1044820.796, "yMm": -406393.643, "handle": "82CF9"},
    {"id": "CAD-DOCK-07", "category": "dock", "text": "东侧码头7", "xMm": 1044820.796, "yMm": -409993.643, "handle": "82CF8"},
    {"id": "CAD-DOCK-08", "category": "dock", "text": "东侧码头8", "xMm": 1044820.796, "yMm": -413642.345, "handle": "82CF5"},
    {"id": "CAD-DOCK-09", "category": "dock", "text": "东侧码头9", "xMm": 1044820.796, "yMm": -418393.643, "handle": "82D04"},
    {"id": "CAD-DOCK-10", "category": "dock", "text": "东侧码头10", "xMm": 1044820.796, "yMm": -421993.643, "handle": "82CFF"},
    # Eight 5-ton handling positions around the western and eastern sides.
    {"id": "CAD-LIFT-WN-T2", "category": "lift5t", "text": "西侧北段 5T·T2", "xMm": 883972.240, "yMm": -374741.311, "handle": "82FB3"},
    {"id": "CAD-LIFT-WN-T1", "category": "lift5t", "text": "西侧北段 5T·T1", "xMm": 888361.807, "yMm": -374741.311, "handle": "82FB5"},
    {"id": "CAD-LIFT-EN-T2", "category": "lift5t", "text": "东侧北段 5T·T2", "xMm": 1037572.240, "yMm": -374741.311, "handle": "82CCC"},
    {"id": "CAD-LIFT-EN-T1", "category": "lift5t", "text": "东侧北段 5T·T1", "xMm": 1041961.807, "yMm": -374741.311, "handle": "82CD7"},
    {"id": "CAD-LIFT-WS-T2", "category": "lift5t", "text": "西侧南段 5T·T2", "xMm": 883972.240, "yMm": -433174.677, "handle": "82FD1"},
    {"id": "CAD-LIFT-WS-T1", "category": "lift5t", "text": "西侧南段 5T·T1", "xMm": 888361.807, "yMm": -433174.677, "handle": "82FD3"},
    {"id": "CAD-LIFT-ES-T6", "category": "lift5t", "text": "东侧南段 5T·T6", "xMm": 1037822.785, "yMm": -432589.586, "handle": "82CF3"},
    {"id": "CAD-LIFT-ES-T5", "category": "lift5t", "text": "东侧南段 5T·T5", "xMm": 1042022.785, "yMm": -432589.586, "handle": "82D02"},
    # Scheduler and terminal process equipment from the active lower CAD layout.
    {"id": "CAD-CONTROL-01", "category": "control", "text": "总控/调度", "xMm": 984359.972, "yMm": -433098.044, "handle": "8CB93"},
    {"id": "CAD-PROCESS-DUMPER", "category": "process", "text": "提升倒料机", "xMm": 986630.667, "yMm": -432704.638, "handle": "8B6CA"},
    {"id": "CAD-PROCESS-WASH", "category": "process", "text": "铝屑清洗喷头", "xMm": 988291.371, "yMm": -432686.254, "handle": "8B6C9"},
    {"id": "CAD-PROCESS-CONVEYOR-S", "category": "process", "text": "排屑机（南）", "xMm": 989382.040, "yMm": -433081.845, "handle": "8B6C8"},
    {"id": "CAD-PROCESS-DRYER", "category": "process", "text": "甩干机", "xMm": 990433.711, "yMm": -433087.046, "handle": "8B6C7"},
    {"id": "CAD-PROCESS-CONVEYOR-N", "category": "process", "text": "排屑机（北）", "xMm": 989310.440, "yMm": -430307.285, "handle": "8B6C6"},
    {"id": "CAD-PROCESS-BOX-N", "category": "process", "text": "铝屑箱（北）", "xMm": 990225.637, "yMm": -428632.273, "handle": "8B6C5"},
]

CAD_REFERENCE_STYLE = {
    "dock": ("#60a5fa", "#1d4ed8"),
    "lift5t": ("#fbbf24", "#b45309"),
    "control": ("#f472b6", "#9d174d"),
    "process": ("#fb7185", "#be123c"),
}


@dataclass(frozen=True)
class StationPoint:
    station_id: str
    row_index: int
    column_index: int
    pixel_x: float
    pixel_y: float
    cad_x_mm: float
    cad_y_mm: float


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n",
        encoding="utf-8",
    )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def rectangle(x: float, y: float, half_width: float, half_height: float) -> list[dict[str, float]]:
    return [
        {"x": x - half_width, "y": y - half_height},
        {"x": x + half_width, "y": y - half_height},
        {"x": x + half_width, "y": y + half_height},
        {"x": x - half_width, "y": y + half_height},
    ]


def cad_to_map_mm(x_mm: float, y_mm: float) -> tuple[float, float]:
    # One map unit is one metre. Absolute CAD metre coordinates make later
    # two-point underlay calibration a simple 0.001 scale with no offset.
    return x_mm / 1000.0, y_mm / 1000.0


def detect_station_points(image_path: Path, viewbox: dict[str, float]) -> tuple[list[StationPoint], list[dict[str, Any]]]:
    image = np.asarray(Image.open(image_path).convert("RGB"))
    crop_y0, crop_y1, crop_x0, crop_x1 = 400, 1360, 650, 2700
    crop = image[crop_y0:crop_y1, crop_x0:crop_x1]
    maximum = crop.max(axis=2)
    minimum = crop.min(axis=2)
    white = (
        (crop[:, :, 0] > 100)
        & (crop[:, :, 1] > 100)
        & (crop[:, :, 2] > 100)
        & ((maximum - minimum) < 70)
    )
    merged = ndimage.binary_dilation(white, iterations=1)
    labels, _ = ndimage.label(merged)

    components: list[tuple[float, float]] = []
    for index, bounds in enumerate(ndimage.find_objects(labels), 1):
        if bounds is None:
            continue
        yy, xx = bounds
        height = yy.stop - yy.start
        width = xx.stop - xx.start
        area = int((labels[bounds] == index).sum())
        if 18 <= width <= 60 and 12 <= height <= 45 and area >= 40:
            components.append(
                (
                    (xx.start + xx.stop) / 2 + crop_x0,
                    (yy.start + yy.stop) / 2 + crop_y0,
                )
            )

    y_groups: list[list[float]] = []
    for y in sorted(point[1] for point in components):
        if not y_groups or y - y_groups[-1][-1] > 8:
            y_groups.append([y])
        else:
            y_groups[-1].append(y)
    row_groups = [group for group in y_groups if len(group) >= 20]
    if len(row_groups) != 11:
        raise ValueError(f"CAD 机台行识别应为 11 行，实际 {len(row_groups)} 行")

    selected_groups = row_groups[2:]
    rows: list[dict[str, Any]] = []
    stations: list[StationPoint] = []
    sequence = 0
    for row_index, y_group in enumerate(selected_groups):
        center_y = sum(y_group) / len(y_group)
        row_points = sorted(
            (point for point in components if abs(point[1] - center_y) <= 2),
            key=lambda point: point[0],
        )
        expected = EXPECTED_ROW_COUNTS[row_index]
        if len(row_points) != expected:
            raise ValueError(
                f"第 {row_index + 1} 个纳入机台行应有 {expected} 台，实际 {len(row_points)} 台"
            )
        cad_points = []
        for column_index, (pixel_x, pixel_y) in enumerate(row_points, 1):
            cad_x_mm = viewbox["minX"] + pixel_x * viewbox["width"] / viewbox["pixelWidth"]
            cad_y_mm = viewbox["minY"] + (
                viewbox["pixelHeight"] - pixel_y
            ) * viewbox["height"] / viewbox["pixelHeight"]
            sequence += 1
            station = StationPoint(
                station_id=f"CNC-{sequence:03d}",
                row_index=row_index,
                column_index=column_index,
                pixel_x=pixel_x,
                pixel_y=pixel_y,
                cad_x_mm=cad_x_mm,
                cad_y_mm=cad_y_mm,
            )
            stations.append(station)
            cad_points.append(
                {
                    "stationId": station.station_id,
                    "column": column_index,
                    "pixel": {"x": round(pixel_x, 3), "y": round(pixel_y, 3)},
                    "cadMm": {"x": round(cad_x_mm, 3), "y": round(cad_y_mm, 3)},
                }
            )
        rows.append(
            {
                "scopeRow": row_index + 1,
                "pixelCenterY": round(center_y, 3),
                "count": len(row_points),
                "points": cad_points,
            }
        )
    if len(stations) != 305:
        raise ValueError(f"纳入点位数应为 305，实际 {len(stations)}")
    return stations, rows


def build_graph(stations: list[StationPoint]) -> tuple[dict[str, Any], dict[str, Any]]:
    nodes: list[dict[str, Any]] = []
    edges: list[dict[str, Any]] = []
    resources: list[dict[str, Any]] = []
    annotations: list[dict[str, Any]] = []
    relations: dict[str, dict[str, Any]] = {}
    edge_sequence = 0
    station_by_group: dict[int, list[StationPoint]] = defaultdict(list)
    row_centers_mm: list[float] = []
    for row_index in range(9):
        values = [s.cad_y_mm for s in stations if s.row_index == row_index]
        if not values:
            raise ValueError(f"第 {row_index + 1} 行没有机台点")
        row_centers_mm.append(sum(values) / len(values))

    def add_node(
        node_id: str,
        x: float,
        y: float,
        node_type: str,
        site_id: str,
        resource_ids: list[str] | None = None,
        layer_id: str = "navigation",
        allow_transit_through: bool = False,
    ) -> None:
        nodes.append(
            {
                "id": node_id,
                "x": x,
                "y": y,
                "type": node_type,
                "enabled": True,
                "resourceIds": resource_ids or [],
                "agvSiteId": site_id,
                "layerId": layer_id,
                "allowTransitThrough": allow_transit_through,
                # CAD junctions have no surveyed off-line waiting envelope.
                # Keep their graph role, but reserve through to a business bay
                # rather than stopping two opposing windows in a shared aisle.
                "allowTrafficStop": node_type in ("Station", "WaitingPoint", "ChargingPoint"),
            }
        )

    def add_resource(
        resource_id: str,
        resource_type: str,
        x: float,
        y: float,
        half_width: float,
        half_height: float,
        layer_id: str = "navigation",
    ) -> None:
        resources.append(
            {
                "id": resource_id,
                "type": resource_type,
                "capacity": 1,
                "boundary": rectangle(x, y, half_width, half_height),
                "layerId": layer_id,
            }
        )

    def add_edge(source: dict[str, Any], target: dict[str, Any], resource_ids: list[str] | None = None) -> None:
        nonlocal edge_sequence
        source_by_id = source["id"]
        target_by_id = target["id"]
        sx, sy = float(source["x"]), float(source["y"])
        tx, ty = float(target["x"]), float(target["y"])
        distance = math.hypot(tx - sx, ty - sy)
        if distance <= 1e-6:
            raise ValueError(f"道路长度为零：{source['id']} -> {target['id']}")
        for from_id, to_id in ((source_by_id, target_by_id), (target_by_id, source_by_id)):
            edges.append(
                {
                    "id": f"CAD-PATH-{edge_sequence:05d}",
                    "from": from_id,
                    "to": to_id,
                    "distance": round(distance, 6),
                    "speedLimit": 0.75,
                    "bidirectional": False,
                    "enabled": True,
                    "resourceIds": list(resource_ids or []),
                    "layerId": "navigation",
                    "geometryKind": "Line",
                }
            )
            edge_sequence += 1

    node_by_id: dict[str, dict[str, Any]] = {}

    def remember(node_id: str) -> dict[str, Any]:
        node = next(node for node in nodes if node["id"] == node_id)
        node_by_id[node_id] = node
        return node

    # CNC station stubs and an approach lane through each back-to-back row pair.
    for group_index, row_pair in enumerate(GROUPS):
        if len(row_pair) == 2:
            aisle_y_mm = (row_centers_mm[row_pair[0]] + row_centers_mm[row_pair[1]]) / 2
        else:
            aisle_y_mm = (row_centers_mm[row_pair[0]] + SOUTH_MM) / 2
        aisle_x, aisle_y = cad_to_map_mm(WEST_MM, aisle_y_mm)
        west = add_node(
            f"LANE-G{group_index + 1}-W",
            aisle_x,
            aisle_y,
            "Intersection",
            f"CAD-G{group_index + 1}-W",
            [f"TRAFFIC-G{group_index + 1}-W"],
        )
        remember(f"LANE-G{group_index + 1}-W")
        add_resource(
            f"TRAFFIC-G{group_index + 1}-W",
            "Intersection",
            aisle_x,
            aisle_y,
            0.65,
            0.65,
        )
        east_x, _ = cad_to_map_mm(EAST_MM, aisle_y_mm)
        add_node(
            f"LANE-G{group_index + 1}-E",
            east_x,
            aisle_y,
            "Intersection",
            f"CAD-G{group_index + 1}-E",
            [f"TRAFFIC-G{group_index + 1}-E"],
        )
        remember(f"LANE-G{group_index + 1}-E")
        add_resource(
            f"TRAFFIC-G{group_index + 1}-E",
            "Intersection",
            east_x,
            aisle_y,
            0.65,
            0.65,
        )

        lane_nodes: list[dict[str, Any]] = []
        for while_row in row_pair:
            station_by_group[group_index].extend(
                sorted(
                    (station for station in stations if station.row_index == while_row),
                    key=lambda station: station.cad_x_mm,
                )
            )
        sorted_group = sorted(station_by_group[group_index], key=lambda station: station.cad_x_mm)
        for station in sorted_group:
            sx, sy = cad_to_map_mm(station.cad_x_mm, station.cad_y_mm)
            direction = 1.0 if aisle_y_mm >= station.cad_y_mm else -1.0
            pickup_y_mm = station.cad_y_mm + direction * 1100.0
            pickup_x, pickup_y = cad_to_map_mm(station.cad_x_mm, pickup_y_mm)
            approach_x, approach_y = cad_to_map_mm(station.cad_x_mm, aisle_y_mm)
            add_node(
                f"BUS-{station.station_id}",
                pickup_x,
                pickup_y,
                "Station",
                station.station_id,
                [station.station_id],
            )
            add_resource(
                station.station_id,
                "Station",
                pickup_x,
                pickup_y,
                0.55,
                0.4,
            )
            lane_id = f"APPROACH-{station.station_id}"
            add_node(
                lane_id,
                approach_x,
                approach_y,
                "Waypoint",
                lane_id,
                allow_transit_through=True,
            )
            lane_node = remember(lane_id)
            add_edge(remember(f"BUS-{station.station_id}"), lane_node, [station.station_id])
            lane_nodes.append(lane_node)
            relations[station.station_id] = {
                "stationNode": f"BUS-{station.station_id}",
                "approachNode": lane_id,
                "row": station.row_index + 1,
                "column": station.column_index,
                "cadStationMm": {"x": round(station.cad_x_mm, 3), "y": round(station.cad_y_mm, 3)},
                "cadPickupMm": {"x": round(station.cad_x_mm, 3), "y": round(pickup_y_mm, 3)},
            }

        lane_nodes = sorted(lane_nodes, key=lambda row: (float(row["x"]), float(row["y"])))
        chain = [remember(f"LANE-G{group_index + 1}-W"), *lane_nodes, remember(f"LANE-G{group_index + 1}-E")]
        for source, target in zip(chain, chain[1:]):
            if abs(float(source["x"]) - float(target["x"])) > 1e-7:
                add_edge(source, target)

        x0, y0 = cad_to_map_mm(min(s.cad_x_mm for s in sorted_group), min(s.cad_y_mm for s in sorted_group))
        x1, y1 = cad_to_map_mm(max(s.cad_x_mm for s in sorted_group), max(s.cad_y_mm for s in sorted_group))
        add_annotation = {
            "id": f"SIM-AREA-G{group_index + 1}",
            "sourceType": "CadSimulationEvidence",
            "text": f"{MAP_GROUP_NAMES[group_index]}取货通道 · {len(sorted_group)}台CNC",
            "boundary": [
                {"x": x0 - 1.0, "y": y0 - 1.1},
                {"x": x1 + 1.0, "y": y0 - 1.1},
                {"x": x1 + 1.0, "y": y1 + 1.1},
                {"x": x0 - 1.0, "y": y1 + 1.1},
            ],
            "strokeColor": ["#38bdf8", "#34d399", "#fbbf24", "#f472b6", "#a78bfa"][group_index],
            "fillColor": ["#0ea5e9", "#10b981", "#f59e0b", "#ec4899", "#8b5cf6"][group_index],
            "fillOpacity": 0.06,
            "layerId": "cad-zones",
            "sourceJson": "",
        }
        annotations.append(add_annotation)

    # Two perimeter spines connect every machine aisle.
    for side in ("W", "E"):
        for group_index in range(4):
            add_edge(
                remember(f"LANE-G{group_index + 1}-{side}"),
                remember(f"LANE-G{group_index + 2}-{side}"),
            )

    # Full south service corridor follows the lower CAD cage boundary.
    sw_x, south_y = cad_to_map_mm(WEST_MM, SOUTH_MM)
    se_x, _ = cad_to_map_mm(EAST_MM, SOUTH_MM)
    add_node("SOUTH-W", sw_x, south_y, "Intersection", "CAD-SOUTH-W", ["TRAFFIC-SOUTH-W"])
    remember("SOUTH-W")
    add_resource("TRAFFIC-SOUTH-W", "Intersection", sw_x, south_y, 0.65, 0.65)
    add_node("SOUTH-E", se_x, south_y, "Intersection", "CAD-SOUTH-E", ["TRAFFIC-SOUTH-E"])
    remember("SOUTH-E")
    add_resource("TRAFFIC-SOUTH-E", "Intersection", se_x, south_y, 0.65, 0.65)
    add_edge(remember("LANE-G5-W"), remember("SOUTH-W"))
    add_edge(remember("LANE-G5-E"), remember("SOUTH-E"))

    south_nodes = []
    for index, x_mm in enumerate((986500.0,)):
        x, y = cad_to_map_mm(x_mm, SOUTH_MM)
        node_id = f"SOUTH-{index + 1}"
        add_node(node_id, x, y, "Intersection", f"CAD-{node_id}")
        south_nodes.append(remember(node_id))
    south_chain = [remember("SOUTH-W"), *south_nodes, remember("SOUTH-E")]
    for source, target in zip(south_chain, south_chain[1:]):
        add_edge(source, target)

    # Terminal access: a central south corridor plus two grouped waiting hubs.
    terminal_nodes = {}
    for node_id, x_mm, y_mm, node_type in (
        ("TERM-N", 986500.0, -426200.0, "Intersection"),
        ("WAIT-HUB-N", 986500.0, -427350.0, "Intersection"),
        ("WAIT-BYPASS-N", 986500.0, -426300.0, "Waypoint"),
        # The east bypass runs outside the five parked CAD vehicle envelopes.
        # Keeping the loop inside the waiting cluster makes a short diagonal
        # sweep through AGV-05/AGV-03 even when those cars are stationary.
        ("WAIT-LOOP-E", 990000.0, -429300.0, "Intersection"),
        ("WAIT-BYPASS-S", 986500.0, -432000.0, "Waypoint"),
        ("WAIT-HUB-S", 986500.0, -431200.0, "Intersection"),
        ("DUMP-APPROACH", 984477.3, -431950.0, "Intersection"),
        ("CHARGE-HUB", 982200.0, -432200.0, "Intersection"),
    ):
        x, y = cad_to_map_mm(x_mm, y_mm)
        resource_ids = [f"TRAFFIC-{node_id}"] if node_type == "Intersection" else []
        add_node(node_id, x, y, node_type, f"CAD-{node_id}", resource_ids, allow_transit_through=(node_type != "Intersection"))
        if resource_ids:
            add_resource(resource_ids[0], "Intersection", x, y, 0.65, 0.65)
        terminal_nodes[node_id] = remember(node_id)

    add_edge(remember("SOUTH-1"), terminal_nodes["TERM-N"])
    add_edge(terminal_nodes["TERM-N"], terminal_nodes["WAIT-BYPASS-N"])
    add_edge(terminal_nodes["WAIT-HUB-N"], terminal_nodes["WAIT-BYPASS-N"])
    add_edge(terminal_nodes["WAIT-HUB-S"], terminal_nodes["WAIT-BYPASS-S"])

    # Each CAD waiting anchor keeps its measured coordinate. A dedicated exit
    # waypoint and north/south collection spine prevent a vehicle leaving a
    # neighbouring occupied bay from cutting diagonally through another parked
    # 0.8 m x 0.52 m vehicle. This preserves the one-to-one CAD stop positions
    # while making the simulator topology physically executable with all five
    # vehicles parked at once.
    wait_top_spine_x, wait_top_spine_y = cad_to_map_mm(990000.0, -426300.0)
    add_node(
        "WAIT-TOP-SPINE",
        wait_top_spine_x,
        wait_top_spine_y,
        "Waypoint",
        "CAD-WAIT-TOP-SPINE",
        allow_transit_through=True,
    )
    wait_top_spine = remember("WAIT-TOP-SPINE")
    wait_bottom_spine_x, wait_bottom_spine_y = cad_to_map_mm(990000.0, -432000.0)
    add_node(
        "WAIT-BOTTOM-SPINE",
        wait_bottom_spine_x,
        wait_bottom_spine_y,
        "Waypoint",
        "CAD-WAIT-BOTTOM-SPINE",
        allow_transit_through=True,
    )
    wait_bottom_spine = remember("WAIT-BOTTOM-SPINE")
    add_edge(wait_top_spine, terminal_nodes["WAIT-LOOP-E"])
    add_edge(terminal_nodes["WAIT-LOOP-E"], wait_bottom_spine)

    wait_ids = []
    top_exits: list[dict[str, Any]] = []
    bottom_exits: list[dict[str, Any]] = []
    for index, (x_mm, y_mm) in enumerate(WAIT_MM, 1):
        node_id = f"WAIT-{index:02d}"
        x, y = cad_to_map_mm(x_mm, y_mm)
        add_node(node_id, x, y, "WaitingPoint", node_id, [node_id])
        add_resource(node_id, "Station", x, y, 0.55, 0.42)
        wait_node = remember(node_id)
        wait_ids.append(node_id)
        is_top = index in (1, 2, 5)
        exit_node_id = f"{node_id}-EXIT"
        exit_y_mm = -426300.0 if is_top else -432000.0
        exit_x, exit_y = cad_to_map_mm(x_mm, exit_y_mm)
        add_node(
            exit_node_id,
            exit_x,
            exit_y,
            "Waypoint",
            f"CAD-{exit_node_id}",
            allow_transit_through=True,
        )
        exit_node = remember(exit_node_id)
        # The node owns the capacity-one wait resource. Binding the long inbound
        # edge to the small stop polygon would make the editor report a false
        # "resource outside boundary" warning.
        add_edge(wait_node, exit_node)
        (top_exits if is_top else bottom_exits).append(exit_node)

    # Join same-row exits as a Manhattan chain instead of drawing overlapping
    # star edges. All crossings are therefore explicit shared nodes, which is
    # required by the map editor's independent geometry audit.
    top_chain = sorted(
        [*top_exits, terminal_nodes["WAIT-BYPASS-N"]],
        key=lambda node: float(node["x"]),
    ) + [wait_top_spine]
    for source, target in zip(top_chain, top_chain[1:]):
        add_edge(source, target)
    bottom_chain = sorted(
        [*bottom_exits, terminal_nodes["WAIT-BYPASS-S"]],
        key=lambda node: float(node["x"]),
    ) + [wait_bottom_spine]
    for source, target in zip(bottom_chain, bottom_chain[1:]):
        add_edge(source, target)
    wait_04_exit = next(node for node in bottom_exits if node["id"] == "WAIT-04-EXIT")
    add_edge(terminal_nodes["WAIT-HUB-S"], wait_04_exit)
    add_edge(wait_04_exit, terminal_nodes["DUMP-APPROACH"])
    add_edge(terminal_nodes["DUMP-APPROACH"], terminal_nodes["CHARGE-HUB"])

    charger_ids = []
    for index, (x_mm, y_mm) in enumerate(CHARGER_MM, 1):
        node_id = f"CHARGE-{index:02d}"
        x, y = cad_to_map_mm(x_mm, y_mm)
        add_node(node_id, x, y, "ChargingPoint", node_id, [node_id])
        add_resource(node_id, "ChargingBay", x, y, 0.75, 0.65)
        charge_node = remember(node_id)
        charger_ids.append(node_id)
        add_edge(terminal_nodes["CHARGE-HUB"], charge_node)

    dump_x, dump_y = cad_to_map_mm(*DUMPER_MM)
    add_node("DUMPER-001", dump_x, dump_y, "Station", "DUMPER-001", ["DUMPER-001"])
    add_resource("DUMPER-001", "Station", dump_x, dump_y, 1.4, 1.1)
    add_edge(terminal_nodes["DUMP-APPROACH"], remember("DUMPER-001"), ["DUMPER-001"])

    inspect_x, inspect_y = cad_to_map_mm(*INSPECTION_MM)
    add_node("INSPECT-01", inspect_x, inspect_y, "Station", "INSPECT-01", ["INSPECT-01"])
    add_resource("INSPECT-01", "Station", inspect_x, inspect_y, 0.7, 0.55)
    inspection_access = next(
        node for node in bottom_exits if node["id"] == "WAIT-03-EXIT"
    )
    add_edge(inspection_access, remember("INSPECT-01"))

    annotations.extend(
        [
            {
                "id": "SIM-CAD-RASTER",
                "sourceType": "CadRasterBackground",
                "text": "B07-1F下方有效CAD",
                "boundary": [
                    {"x": (DEFAULT_VIEWBOX["minX"]) / 1000, "y": (DEFAULT_VIEWBOX["minY"]) / 1000},
                    {"x": (DEFAULT_VIEWBOX["minX"] + DEFAULT_VIEWBOX["width"]) / 1000, "y": (DEFAULT_VIEWBOX["minY"]) / 1000},
                    {"x": (DEFAULT_VIEWBOX["minX"] + DEFAULT_VIEWBOX["width"]) / 1000, "y": (DEFAULT_VIEWBOX["minY"] + DEFAULT_VIEWBOX["height"]) / 1000},
                    {"x": (DEFAULT_VIEWBOX["minX"]) / 1000, "y": (DEFAULT_VIEWBOX["minY"] + DEFAULT_VIEWBOX["height"]) / 1000},
                ],
                "strokeColor": "#1e6681",
                "fillColor": "#0b2534",
                "fillOpacity": 0.01,
                "layerId": "terminal-zones",
                "sourceJson": json.dumps(
                    {
                        "asset": "cad/b07-1f-lower-layout-reference.png",
                        "opacity": 0.20,
                        "viewBox": DEFAULT_VIEWBOX,
                    },
                    ensure_ascii=False,
                    separators=(",", ":"),
                ),
            },
            {
                "id": "SIM-AREA-TERMINAL",
                "sourceType": "CadSimulationEvidence",
                "text": "铝屑终端处理与倒料区",
                "boundary": [
                    {"x": 979.5, "y": -433.8},
                    {"x": 990.5, "y": -433.8},
                    {"x": 990.5, "y": -427.2},
                    {"x": 979.5, "y": -427.2},
                ],
                "strokeColor": "#fb7185",
                "fillColor": "#e11d48",
                "fillOpacity": 0.08,
                "layerId": "terminal-zones",
                "sourceJson": "",
            },
            {
                "id": "SIM-AREA-WAIT",
                "sourceType": "CadSimulationEvidence",
                "text": "五车待命区",
                "boundary": [
                    {"x": 984.0, "y": -430.8},
                    {"x": 987.7, "y": -430.8},
                    {"x": 987.7, "y": -428.2},
                    {"x": 984.0, "y": -428.2},
                ],
                "strokeColor": "#22d3ee",
                "fillColor": "#0891b2",
                "fillOpacity": 0.10,
                "layerId": "terminal-zones",
                "sourceJson": "",
            },
            {
                "id": "SIM-AREA-CHARGE",
                "sourceType": "CadSimulationEvidence",
                "text": "双充电位",
                "boundary": [
                    {"x": 980.9, "y": -433.6},
                    {"x": 983.6, "y": -433.6},
                    {"x": 983.6, "y": -432.5},
                    {"x": 980.9, "y": -432.5},
                ],
                "strokeColor": "#a78bfa",
                "fillColor": "#8b5cf6",
                "fillOpacity": 0.11,
                "layerId": "terminal-zones",
                "sourceJson": "",
            },
        ]
    )
    annotations.extend(cad_reference_annotations())

    definition = {
        "name": MAP_NAME,
        "source": "",
        "coordinateSystem": "CadWorld",
        "unit": "m",
        "nodes": nodes,
        "edges": edges,
        "resources": resources,
        "areaAnnotations": annotations,
        "importWarnings": [
            "导航拓扑按2026-10-12下方有效CAD的机台点、通道边界和终端区位置生成；CAD毫米坐标除以1000后直接作为米制地图坐标。",
            "305台CNC由CAD预览中R3-R11九排识别得到，逐排数量26+36+37+36+36+37+24+37+36=305；R1-R2的73个重复设备图形不纳入AGV服务范围。",
            "五台AGV与两个充电桩位置取自CAD文字锚点；倒料工位位于南侧终端处理区。",
            "CAD未提供连续AGV中心线、单双向属性、转弯半径和真实避障边界。本图的横向巷道与外围联络线用于隔离仿真，不能被当作已验收车端导航路径。",
            "PLC点表为305工位隔离仿真契约，地址状态为LabOnly；不得按此表直接写入现场PLC。",
        ],
        "layers": [
            {
                "id": "navigation",
                "name": "全厂AGV导航",
                "visible": True,
                "isLocked": False,
                "isAllowSelect": True,
                "alpha": 1,
                "color": "#22d3ee",
                "strokeWidth": 2,
            },
            {
                "id": "cad-zones",
                "name": "CAD机台通道校核",
                "visible": True,
                "isLocked": True,
                "isAllowSelect": False,
                "alpha": 0.9,
                "color": "#38bdf8",
                "strokeWidth": 1,
            },
            {
                "id": "cad-deployment",
                "name": "CAD厂区部署参考点",
                "visible": True,
                "isLocked": True,
                "isAllowSelect": False,
                "alpha": 0.9,
                "color": "#fbbf24",
                "strokeWidth": 1,
            },
            {
                "id": "terminal-zones",
                "name": "终端、待命与充电区",
                "visible": True,
                "isLocked": True,
                "isAllowSelect": False,
                "alpha": 0.9,
                "color": "#fb7185",
                "strokeWidth": 1,
            },
        ],
        "editorSettings": {
            "worldWidth": (EAST_MM - WEST_MM) / 1000,
            "worldHeight": (NORTH_MM - SOUTH_MM) / 1000 + 10,
            "gridSize": 3.125,
            "showGrid": True,
            "snapToGrid": False,
            "snapToExisting": False,
            "display": None,
            "currentLayerId": "navigation",
            "snapToAlignment": True,
            "snapToGeometry": True,
        },
    }
    summary = {
        "nodeCount": len(nodes),
        "edgeCount": len(edges),
        "resourceCount": len(resources),
        "cncStationCount": len(stations),
        "vehicleWaitingPoints": wait_ids,
        "chargingPoints": charger_ids,
        "dumpingSite": "DUMPER-001",
        "inspectionSite": "INSPECT-01",
        "stationRelations": relations,
        "routeGroups": [
            {
                "group": index + 1,
                "name": MAP_GROUP_NAMES[index],
                "rows": list(row_pair),
                "stations": len(station_by_group[index]),
            }
            for index, row_pair in enumerate(GROUPS)
        ],
        "cadReferencePoints": {
            category: [
                point for point in CAD_REFERENCE_POINTS
                if point["category"] == category
            ]
            for category in CAD_REFERENCE_STYLE
        },
    }
    return definition, summary


def cad_reference_annotations() -> list[dict[str, Any]]:
    annotations = []
    for point in CAD_REFERENCE_POINTS:
        stroke, fill = CAD_REFERENCE_STYLE[point["category"]]
        x, y = cad_to_map_mm(point["xMm"], point["yMm"])
        half_width = 0.42 if point["category"] != "dock" else 0.58
        half_height = 0.24
        annotations.append(
            {
                "id": f"SIM-CAD-POINT-{point['id']}",
                "sourceType": "CadReferencePoint",
                "text": point["text"],
                "boundary": rectangle(x, y, half_width, half_height),
                "strokeColor": stroke,
                "fillColor": fill,
                "fillOpacity": 0.12,
                "layerId": "cad-deployment",
                "sourceJson": json.dumps(
                    {
                        "cadMm": {"x": point["xMm"], "y": point["yMm"]},
                        "handle": point["handle"],
                        "category": point["category"],
                        "navigationAuthority": "visual-reference-only",
                    },
                    ensure_ascii=False,
                    separators=(",", ":"),
                ),
            }
        )
    return annotations


def apply_dual_lane_topology(definition: dict[str, Any], lane_offset_m: float = 0.0) -> dict[str, Any]:
    """Label reciprocal main-road edges as two logical directional lanes.

    The CAD does not define lane width or a separated physical carriageway.
    Keeping the vetted centreline prevents invented geometry and intersection
    crossings while still exposing east/west and north/south lane roles to the
    UI and evidence report.
    """
    nodes = {node["id"]: node for node in definition["nodes"]}
    # The per-CNC approach points are deliberate lane junctions, not physical
    # endpoints. Treating them as Waypoints causes the planner to add a
    # capacity-one merge guard at every machine, effectively serialising the
    # whole factory. The station node itself remains capacity one.
    approach_junctions = 0
    for node in definition["nodes"]:
        if str(node.get("id", "")).startswith("APPROACH-") and node.get("type") == "Waypoint":
            node["type"] = "Intersection"
            approach_junctions += 1
    outgoing: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for edge in definition["edges"]:
        outgoing[edge["from"]].append(edge)

    blocked_node_types = {"Station", "WaitingPoint", "ChargingPoint"}
    lane_pair_ids: set[str] = set()
    resources_by_id = {resource["id"]: resource for resource in definition["resources"]}
    shared_main_intersections = 0
    head_on_guards = 0
    for resource in definition["resources"]:
        if str(resource.get("id", "")).startswith(("TRAFFIC-G", "TRAFFIC-SOUTH-")):
            resource["capacity"] = max(5, int(resource.get("capacity", 1)))
            shared_main_intersections += 1
    converted = 0
    for edge in definition["edges"]:
        reverse = next(
            (
                candidate for candidate in outgoing.get(edge["to"], [])
                if candidate["to"] == edge["from"]
            ),
            None,
        )
        if reverse is None:
            continue
        pair_key = "|".join(sorted((edge["id"], reverse["id"])))
        if pair_key in lane_pair_ids:
            continue
        lane_pair_ids.add(pair_key)
        source = nodes[edge["from"]]
        target = nodes[edge["to"]]
        if source.get("type") in blocked_node_types or target.get("type") in blocked_node_types:
            continue
        dx = float(target["x"]) - float(source["x"])
        dy = float(target["y"]) - float(source["y"])
        length = math.hypot(dx, dy)
        if length < 0.9:
            continue
        lane_resource_id = "DUAL-LANE-" + hashlib.sha256(pair_key.encode()).hexdigest()[:20].upper()
        if lane_resource_id not in resources_by_id:
            resource = {
                "id": lane_resource_id,
                "type": "NarrowAisle",
                "capacity": 5,
                "boundary": rectangle(
                    (float(source["x"]) + float(target["x"])) / 2,
                    (float(source["y"]) + float(target["y"])) / 2,
                    max(0.7, abs(dx) / 2 + 0.7),
                    max(0.7, abs(dy) / 2 + 0.7),
                ),
                "layerId": "navigation",
            }
            definition["resources"].append(resource)
            resources_by_id[lane_resource_id] = resource
        # The reviewed CAD has no separated physical carriageway. Both
        # directional lanes therefore still overlap on the same centreline.
        # Give that physical segment a capacity-one guard so opposing traffic
        # cannot acquire the segment simultaneously. This alone does not prevent
        # multi-segment hold-and-wait cycles.
        head_on_guard_id = "HEAD-ON-GUARD-" + hashlib.sha256(("guard|" + pair_key).encode()).hexdigest()[:20].upper()
        if head_on_guard_id not in resources_by_id:
            guard = {
                "id": head_on_guard_id,
                "type": "NarrowAisle",
                "capacity": 1,
                "boundary": rectangle(
                    (float(source["x"]) + float(target["x"])) / 2,
                    (float(source["y"]) + float(target["y"])) / 2,
                    max(0.7, abs(dx) / 2 + 0.7),
                    max(0.7, abs(dy) / 2 + 0.7),
                ),
                "layerId": "navigation",
            }
            definition["resources"].append(guard)
            resources_by_id[head_on_guard_id] = guard
            head_on_guards += 1
        for directed in (edge, reverse):
            from_node = nodes[directed["from"]]
            to_node = nodes[directed["to"]]
            vx = float(to_node["x"]) - float(from_node["x"])
            vy = float(to_node["y"]) - float(from_node["y"])
            directed["geometryKind"] = "Line"
            directed.pop("controlPoints", None)
            directed["laneRole"] = (
                "东向车道" if abs(vx) >= abs(vy) and vx > 0 else
                "西向车道" if abs(vx) >= abs(vy) else
                "北向车道" if vy > 0 else "南向车道"
            )
            directed["laneOffsetMeters"] = lane_offset_m
            directed["lanePairId"] = pair_key
            directed["laneResourceId"] = lane_resource_id
            directed["resourceIds"] = list(dict.fromkeys([
                *(directed.get("resourceIds") or []),
                lane_resource_id,
                head_on_guard_id,
            ]))
            directed["displayName"] = directed["laneRole"]
            converted += 1
    return {
        "lanePairCount": converted // 2,
        "directedLaneCount": converted,
        "laneOffsetMeters": lane_offset_m,
        "physicalSeparation": False,
        "logicalLaneResourceCapacity": 5,
        "headOnGuardCapacity": 1,
        "headOnGuardCount": head_on_guards,
        "approachJunctionsConverted": approach_junctions,
        "sharedMainIntersectionsCapacity": 5,
        "reason": "CAD does not provide verified lane width; logical direction lanes share the vetted centreline and a capacity-one head-on guard prevents simultaneous opposing use of each segment; multi-segment deadlock requires separate prevention.",
        "stationSpursRemainCentred": True,
        "terminalAndStationCapacity": 1,
    }


def station_envelope_conflicts(definition, length_m=0.8, width_m=0.52, margin_m=0.1):
    """Conservative rotation-independent parked-body clearance in metres.

    A stopped CNC vehicle can intrude into a neighbouring through lane even
    when the graph edges do not cross. Both vehicles' circumscribed radii plus
    a margin bound their possible headings; this trades throughput for safety.
    """
    if not all(math.isfinite(v) and v > 0 for v in (length_m, width_m, margin_m)):
        raise ValueError('车辆包络参数必须为正有限数。')
    radius = math.hypot(length_m, width_m) + margin_m
    nodes = {n['id']: n for n in definition['nodes']}
    stations = [n for n in nodes.values() if n.get('agvSiteId', '').startswith('CNC-')]
    for edge in definition['edges']:
        a, b = nodes[edge['from']], nodes[edge['to']]
        dx, dy = b['x']-a['x'], b['y']-a['y']
        squared = dx*dx+dy*dy
        for station in stations:
            t = max(0., min(1., ((station['x']-a['x'])*dx+(station['y']-a['y'])*dy)/squared)) if squared else 0.
            distance = math.hypot(station['x']-a['x']-t*dx, station['y']-a['y']-t*dy)
            if distance <= radius:
                yield edge, station['agvSiteId']


def apply_station_envelope_guards(definition):
    added = 0
    for edge, resource in station_envelope_conflicts(definition):
        resources = edge.setdefault('resourceIds', [])
        if resource not in resources:
            resources.append(resource)
            added += 1
    return dict(addedEdgeResourceBindings=added, lengthMeters=.8, widthMeters=.52,
                marginMeters=.1, method='two-circumscribed-radii',
                authority='simulation-design-only')


def valid_graph(definition: dict[str, Any], initial_sites: list[str]) -> dict[str, Any]:
    nodes = {node["id"]: node for node in definition["nodes"]}
    if len(nodes) != len(definition["nodes"]):
        raise ValueError("地图节点ID重复")
    if len({node["agvSiteId"] for node in definition["nodes"]}) != len(nodes):
        raise ValueError("地图厂家站点ID重复")
    edge_ids = {edge["id"] for edge in definition["edges"]}
    if len(edge_ids) != len(definition["edges"]):
        raise ValueError("地图道路ID重复")
    if any(edge["from"] not in nodes or edge["to"] not in nodes for edge in definition["edges"]):
        raise ValueError("地图道路引用不存在节点")
    graph: dict[str, set[str]] = defaultdict(set)
    for edge in definition["edges"]:
        if edge.get("enabled", True):
            graph[edge["from"]].add(edge["to"])
            if edge.get("bidirectional", False):
                graph[edge["to"]].add(edge["from"])
    targets = [
        node_id
        for node_id, node in nodes.items()
        if node["type"] == "Station" or node["id"].startswith(("WAIT-", "CHARGE-"))
    ]
    checks = 0
    for source in initial_sites:
        for target in targets:
            pending, seen = [source], set()
            while pending:
                current = pending.pop()
                if current == target:
                    break
                if current in seen:
                    continue
                seen.add(current)
                pending.extend(graph[current] - seen)
            else:
                raise ValueError(f"有向图不可达：{source} -> {target}")
            checks += 1
    station_resources = [
        resource for resource in definition["resources"] if resource["type"] == "Station"
    ]
    expected_station_resources = 311 + int(any(n["id"] == "INSPECT-01" for n in nodes.values()))
    if len(station_resources) != expected_station_resources:
        # 305 CNC + five waiting points + dumping; legacy graph has inspection.
        raise ValueError(f"Station资源数应为{expected_station_resources}，实际 {len(station_resources)}")
    charging_resources = [
        resource for resource in definition["resources"] if resource["type"] == "ChargingBay"
    ]
    if len(charging_resources) != 2:
        raise ValueError(f"充电资源应为2，实际 {len(charging_resources)}")
    return {
        "nodeCount": len(nodes),
        "edgeCount": len(definition["edges"]),
        "resourceCount": len(definition["resources"]),
        "directedReachabilityChecks": checks,
        "directedReachabilityPassed": True,
        "stationResourceCount": len(station_resources),
        "chargingResourceCount": len(charging_resources),
    }


def check_wait_departure_clearance(definition: dict[str, Any]) -> dict[str, Any]:
    """Check that each parked car can leave while the other four stay parked."""
    nodes = {node["id"]: node for node in definition["nodes"]}
    graph: dict[str, list[tuple[str, float]]] = defaultdict(list)
    for edge in definition["edges"]:
        if edge.get("enabled", True):
            graph[edge["from"]].append((edge["to"], float(edge["distance"])))

    def shortest_path(source: str, target: str) -> list[str]:
        pending = [(0.0, source, ())]
        visited: dict[str, float] = {}
        while pending:
            distance, node_id, route = heapq.heappop(pending)
            if node_id in visited and visited[node_id] <= distance:
                continue
            visited[node_id] = distance
            route = route + (node_id,)
            if node_id == target:
                return list(route)
            for next_id, edge_distance in graph[node_id]:
                heapq.heappush(pending, (distance + edge_distance, next_id, route))
        raise ValueError(f"等待位{source}无法到达{target}。")

    wait_ids = [f"WAIT-{index:02d}" for index in range(1, 6)]
    parked = {
        node_id: (nodes[node_id]["x"], nodes[node_id]["y"])
        for node_id in wait_ids
    }
    targets = ("DUMPER-001", "BUS-CNC-001", "BUS-CNC-305")
    violations = []
    for source in wait_ids:
        for target in targets:
            route = shortest_path(source, target)
            blocked = False
            for from_id, to_id in zip(route, route[1:]):
                ax, ay = nodes[from_id]["x"], nodes[from_id]["y"]
                bx, by = nodes[to_id]["x"], nodes[to_id]["y"]
                angle = math.atan2(by - ay, bx - ax)
                for sample in range(21):
                    ratio = sample / 20
                    moving = {
                        "x": ax + (bx - ax) * ratio,
                        "y": ay + (by - ay) * ratio,
                        "angle": angle,
                        "lengthMeters": 0.8,
                        "widthMeters": 0.52,
                    }
                    for peer_id, (px, py) in parked.items():
                        if peer_id == source:
                            continue
                        peer = {
                            "x": px,
                            "y": py,
                            "angle": 0.0,
                            "lengthMeters": 0.8,
                            "widthMeters": 0.52,
                        }
                        if swept_rectangles_overlap(
                            moving, moving, peer, peer, margin=0.05
                        ):
                            violations.append(
                                {
                                    "source": source,
                                    "target": target,
                                    "edge": [from_id, to_id],
                                    "parkedPeer": peer_id,
                                }
                            )
                            blocked = True
                            break
                    if blocked:
                        break
                if blocked:
                    break
    return {
        "directedPathsChecked": len(wait_ids) * len(targets),
        "violations": violations,
        "passed": not violations,
    }


def build_plc_csv(station_ids: list[str]) -> tuple[str, dict[str, Any]]:
    headers = [
        "address",
        "variableName",
        "chineseName",
        "stationId",
        "deviceType",
        "instanceIndex",
        "dataType",
        "direction",
        "access",
        "addressStatus",
        "enabled",
        "setConditionZh",
        "resetConditionZh",
        "sourceNote",
    ]
    station_signals = [
        ("FullBoxRequested", "满箱呼叫", "PLC->Host", "Read", "工位检测到满箱", "任务确认后复位"),
        ("BoxPresent", "箱体检测", "PLC->Host", "Read", "工位检测到箱体", "箱体离开后复位"),
        ("Faulted", "工位故障", "PLC->Host", "Read", "设备故障", "故障恢复后复位"),
        ("CommunicationOnline", "下游在线", "PLC->Host", "Read", "通信在线", "通信中断后复位"),
        ("FullBoxRequestAcknowledged", "呼叫响应", "Host->PLC", "Write", "调度接收呼叫", "握手完成后复位"),
        ("FullBoxRemoved", "满箱拉走通知", "Host->PLC", "Write", "车辆取走满箱", "返箱业务完成后复位"),
        ("EmptyBoxTaskAcknowledged", "空箱任务响应", "Host->PLC", "Write", "车辆接收空箱任务", "放箱完成后复位"),
        ("EmptyBoxPlaced", "空箱放到位通知", "Host->PLC", "Write", "空箱放置完成", "工位确认后复位"),
    ]
    rows = []
    for index, station_id in enumerate(station_ids):
        offset = index * 2
        for bit, (variable, chinese, direction, access, set_condition, reset_condition) in enumerate(station_signals):
            rows.append(
                {
                    "address": f"DB1.DBX{offset}.{bit}",
                    "variableName": variable,
                    "chineseName": chinese,
                    "stationId": station_id,
                    "deviceType": "ChipRemovalStation",
                    "instanceIndex": str(index + 1),
                    "dataType": "Bool",
                    "direction": direction,
                    "access": access,
                    "addressStatus": "LabOnly",
                    "enabled": "true",
                    "setConditionZh": set_condition,
                    "resetConditionZh": reset_condition,
                    "sourceNote": "305台CNC CAD全厂隔离仿真点表；地址未作为现场PLC地址核定",
                }
            )
    dumper_signals = [
        ("AgvAtWaitingPosition", "等待位申请", "Host->PLC", "Write"),
        ("AgvAtDumpingPosition", "倒料周期通知", "Host->PLC", "Write"),
        ("DumperReady", "Ready入站许可", "PLC->Host", "Read"),
        ("DumpingCompletedMayLeave", "Done允许离开", "PLC->Host", "Read"),
        ("Faulted", "倒料设备故障", "PLC->Host", "Read"),
    ]
    for bit, (variable, chinese, direction, access) in enumerate(dumper_signals):
        rows.append(
            {
                "address": f"DB1.DBX610.{bit}",
                "variableName": variable,
                "chineseName": chinese,
                "stationId": "DUMPER-001",
                "deviceType": "Dumper",
                "instanceIndex": "1",
                "dataType": "Bool",
                "direction": direction,
                "access": access,
                "addressStatus": "LabOnly",
                "enabled": "true",
                "setConditionZh": "隔离仿真握手",
                "resetConditionZh": "隔离仿真握手完成",
                "sourceNote": "终端倒料设备隔离仿真点；不是现场地址核定",
            }
        )
    text_buffer = io.StringIO(newline="")
    writer = csv.DictWriter(text_buffer, fieldnames=headers, lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)
    text = text_buffer.getvalue()
    binary = text.encode("utf-8-sig")
    metadata = {
        "fileName": "plc-cad-305-cnc-isolated.csv",
        "csvText": text,
        "sha256": hashlib.sha256(binary).hexdigest(),
        "bytes": len(binary),
        "rowCount": len(rows),
        "signalCount": len(rows),
        "stationCount": len(station_ids),
        "dumperSignalCount": len(dumper_signals),
        "stations": [
            {
                "stationId": station_id,
                "offset": index * 2,
                "signalSource": "isolated-synthetic",
                "sourceLabel": "CAD全厂305工位隔离仿真；地址未经现场核定",
                "fieldConfirmed": False,
            }
            for index, station_id in enumerate(station_ids)
        ],
        "headers": headers,
        "sourceFile": "tools/CadSimulationMap/build_full_map.py:generated-cad-305-cnc-isolated",
        "sourceDeviceId": "SIM-PLC-CAD-305",
        "sourceDeviceName": "CAD全厂305工位虚拟PLC",
    }
    return text, metadata


def build_configuration(
    definition: dict[str, Any],
    station_ids: list[str],
    plc_table: dict[str, Any],
    map_id: str = "",
    map_version_id: str = "",
    definition_hash: str = "",
) -> dict[str, Any]:
    stations = [
        {
            "stationId": station_id,
            "pickupSite": f"BUS-{station_id}",
            "signalSource": "isolated-synthetic",
            "priority": 0,
        }
        for station_id in station_ids
    ]
    vehicles = [
        {
            "id": f"AGV-{index:02d}",
            "initialSite": f"WAIT-{index:02d}",
            "radiusMeters": 0.49,
            "lengthMeters": 0.8,
            "widthMeters": 0.52,
            "initialLoaded": False,
            "batteryPercent": 86.0,
            "speedScale": 0.75,
            "accelerationMetersPerSecondSquared": 0.6,
            "operationSeconds": 3.0,
            "travelCostPercentPerMeter": 0.01,
            "idleCostPercentPerHour": 0.1,
            "voltageCurve": [
                {"percent": 0, "voltage": 21},
                {"percent": 100, "voltage": 26},
            ],
            "chargingGainPercentPerHour": 20,
        }
        for index in range(1, 6)
    ]
    return {
        "name": definition.get("name", MAP_NAME),
        "mapDefinition": definition,
        "stations": stations,
        "vehicles": vehicles,
        "dumper": {
            "dumpingSite": "DUMPER-001",
            "waitingSite": "WAIT-01",
            "waitingSites": [f"WAIT-{index:02d}" for index in range(1, 6)],
            "resourceId": "DUMPER-001",
        },
        "inspectionSite": "INSPECT-01" if any(n["id"] == "INSPECT-01" for n in definition["nodes"]) else "WAIT-01",
        "plcSignalTable": plc_table,
        "mapIdentity": {
            "mapId": map_id,
            "mapVersionId": map_version_id,
            "sourceName": definition.get("name", MAP_NAME),
            "definitionHash": definition_hash,
        },
    }


def build_scenario(station_ids: list[str]) -> dict[str, Any]:
    return {
        "name": "custom-calls",
        "rounds": 1,
        "intervalSeconds": 2,
        "timeoutSeconds": 86400,
        "calls": [
            {"stationId": station_id, "atSeconds": 0.0, "vehicleId": None}
            for station_id in station_ids
        ],
    }


def render_preview(
    path: Path,
    image_path: Path,
    viewbox: dict[str, float],
    stations: list[StationPoint],
    definition: dict[str, Any],
) -> None:
    regular = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 22)
    small = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 16)
    tiny = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 13)
    bold = ImageFont.truetype(r"C:\Windows\Fonts\msyhbd.ttc", 34)
    title = ImageFont.truetype(r"C:\Windows\Fonts\msyhbd.ttc", 26)

    source = Image.open(image_path).convert("RGB")
    cad_width, cad_height = 2700, 1736
    canvas = Image.new("RGB", (3600, 2000), "#07101f")
    canvas.paste(source.resize((cad_width, cad_height), Image.Resampling.LANCZOS), (20, 20))
    draw = ImageDraw.Draw(canvas, "RGBA")

    def pixel_from_cad_m(x: float, y: float) -> tuple[float, float]:
        x_mm = x * 1000
        y_mm = y * 1000
        px = (x_mm - viewbox["minX"]) * viewbox["pixelWidth"] / viewbox["width"]
        py = viewbox["pixelHeight"] - (
            (y_mm - viewbox["minY"]) * viewbox["pixelHeight"] / viewbox["height"]
        )
        return 20 + px * cad_width / viewbox["pixelWidth"], 20 + py * cad_height / viewbox["pixelHeight"]

    nodes = {node["id"]: node for node in definition["nodes"]}
    drawn_pairs = set()
    for edge in definition["edges"]:
        pair = tuple(sorted((edge['from'], edge['to'])))
        if pair in drawn_pairs:
            continue
        drawn_pairs.add(pair)
        a = nodes[edge["from"]]
        b = nodes[edge["to"]]
        p1 = pixel_from_cad_m(a["x"], a["y"])
        p2 = pixel_from_cad_m(b["x"], b["y"])
        draw.line((p1[0], p1[1], p2[0], p2[1]), fill=(32, 211, 238, 140), width=2)

    group_colors = ["#38bdf8", "#34d399", "#fbbf24", "#f472b6", "#a78bfa"]
    for station in stations:
        group = next(i for i, pair in enumerate(GROUPS) if station.row_index in pair)
        color = group_colors[group]
        px = 20 + station.pixel_x * cad_width / viewbox["pixelWidth"]
        py = 20 + station.pixel_y * cad_height / viewbox["pixelHeight"]
        draw.ellipse((px - 4, py - 4, px + 4, py + 4), fill=color, outline="#06111f", width=1)

    for index in range(1,6):
        node=nodes[f'WAIT-{index:02d}']
        px, py = pixel_from_cad_m(node['x'],node['y'])
        draw.ellipse((px - 10, py - 10, px + 10, py + 10), fill="#22d3ee", outline="#ecfeff", width=2)
        draw.text((px + 13, py - 13), f"AGV-{index:02d}", font=tiny, fill="#a5f3fc")
    for index in range(1,3):
        node=nodes[f'CHARGE-{index:02d}']
        px, py = pixel_from_cad_m(node['x'],node['y'])
        draw.ellipse((px - 10, py - 10, px + 10, py + 10), fill="#a78bfa", outline="#f5f3ff", width=2)
        draw.text((px + 13, py - 13), f"充电{index}", font=tiny, fill="#ddd6fe")
    px, py = pixel_from_cad_m(nodes['DUMPER-001']['x'],nodes['DUMPER-001']['y'])
    draw.rounded_rectangle((px - 12, py - 12, px + 12, py + 12), radius=4, fill="#fb7185", outline="#fff1f2", width=2)
    draw.text((px + 15, py - 13), "倒料工位", font=tiny, fill="#fecdd3")
    for point in CAD_REFERENCE_POINTS:
        px, py = pixel_from_cad_m(point["xMm"] / 1000, point["yMm"] / 1000)
        stroke, fill = CAD_REFERENCE_STYLE[point["category"]]
        draw.rectangle((px - 5, py - 5, px + 5, py + 5), fill=fill, outline=stroke, width=1)
        draw.text((px + 7, py - 7), point["text"], font=tiny, fill=stroke)

    draw.rounded_rectangle((35, 35, 1700, 205), radius=16, fill=(7, 16, 31, 235), outline="#315478", width=2)
    draw.text((59, 52), MAP_NAME, fill="#e7f4ff", font=bold)
    draw.text((60, 108), "CAD一比一导航 · 305台CNC · 5台AGV · 2个充电位 · 1个终端倒料工位", fill="#6fd7e7", font=regular)
    draw.text((60, 150), "CAD毫米坐标 ÷ 1000 后作为米制地图坐标；本预览中的彩色线为隔离仿真拓扑", fill="#a8bdd2", font=small)

    panel_x = 2760
    draw.rounded_rectangle((panel_x, 20, 3575, 980), radius=16, fill=(7, 16, 31, 238), outline="#315478", width=2)
    draw.text((panel_x + 30, 50), "建图结果", fill="#e7f4ff", font=title)
    rows = [
        ("机台", "305 台 CNC"),
        ("车辆", "5 台，800 × 520 mm"),
        ("速度", "0.75 m/s"),
        ("倒料", "终端倒料工位 1 个"),
        ("等待", "5 个 CAD 标注 AGV 位置"),
        ("充电", "2 个 CAD 标注充电位"),
        ("导航", f"{len(definition['nodes'])} 节点 / {len(definition['edges'])} 有向道路"),
        ("资源", f"{len(definition['resources'])} 个容量 1 资源"),
    ]
    y = 105
    for label, value in rows:
        draw.text((panel_x + 32, y), label, fill="#7f9bb8", font=small)
        draw.text((panel_x + 140, y), value, fill="#d9ecf8", font=regular)
        y += 54
    y += 20
    draw.text((panel_x + 32, y), "机台行识别", fill="#70d9c0", font=title)
    y += 44
    row_counts = " / ".join(str(v) for v in EXPECTED_ROW_COUNTS)
    for text in (
        f"纳入行：{row_counts}",
        "合计：305 台",
        "R1-R2 的 73 个重复设备图形不纳入",
        "工位取货偏移 1.1 m（净空待核定）",
        "PLC地址全部标记 LabOnly",
    ):
        draw.text((panel_x + 34, y), text, fill="#a8bdd2", font=small)
        y += 34

    draw.rounded_rectangle((panel_x, 1010, 3575, 1560), radius=16, fill=(7, 16, 31, 238), outline="#7b4b5e", width=2)
    draw.text((panel_x + 30, 1040), "必须保留的边界", fill="#fda4af", font=title)
    draw.text((panel_x + 32, 1095), "本图不是车端SMAP导出，也不是现场实测导航地图。", fill="#d6bfc8", font=tiny)
    draw.text((panel_x + 32, 1135), "横向巷道由CAD机台空档生成。", fill="#d6bfc8", font=tiny)
    draw.text((panel_x + 32, 1175), "真实转弯半径和障碍轮廓仍待车端核对。", fill="#d6bfc8", font=tiny)
    draw.text((panel_x + 32, 1215), "305个PLC点用于隔离仿真，", fill="#d6bfc8", font=tiny)
    draw.text((panel_x + 32, 1255), "不能直接作为现场PLC写入依据。", fill="#d6bfc8", font=tiny)

    canvas.save(path)


def render_vector_preview(path: Path, definition: dict[str, Any]) -> None:
    """Render the simulation map itself, without using the CAD image as a base."""
    width, height = 2400, 1500
    canvas = Image.new("RGB", (width, height), "#07101f")
    draw = ImageDraw.Draw(canvas, "RGBA")
    heading = ImageFont.truetype(r"C:\Windows\Fonts\msyhbd.ttc", 34)
    title = ImageFont.truetype(r"C:\Windows\Fonts\msyhbd.ttc", 24)
    normal = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 18)
    small = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 14)
    tiny = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 11)

    nodes = {str(node["id"]): node for node in definition["nodes"]}
    points = [(float(node["x"]), float(node["y"])) for node in nodes.values()]
    for edge in definition["edges"]:
        for point in edge.get("controlPoints") or []:
            points.append((float(point["x"]), float(point["y"])))
    min_x = min(point[0] for point in points)
    max_x = max(point[0] for point in points)
    min_y = min(point[1] for point in points)
    max_y = max(point[1] for point in points)
    map_box = (40, 130, 1980, 1360)
    left, top, right, bottom = map_box
    scale = min((right - left) / max(.001, max_x - min_x),
                (bottom - top) / max(.001, max_y - min_y))
    used_width = (max_x - min_x) * scale
    used_height = (max_y - min_y) * scale
    offset_x = left + ((right - left) - used_width) / 2
    offset_y = top + ((bottom - top) - used_height) / 2

    def transform(x: float, y: float) -> tuple[float, float]:
        return offset_x + (x - min_x) * scale, offset_y + (max_y - y) * scale

    draw.text((42, 28), "B07-1F · 全厂305CNC · 仿真矢量地图", fill="#e8f6ff", font=heading)
    draw.text(
        (44, 78),
        "站点、分区、道路和资源均由 .agvmap.json 数据绘制；CAD 仅作为点位来源，不嵌入导航底图",
        fill="#77a4c0",
        font=normal,
    )
    draw.rounded_rectangle(map_box, radius=14, fill="#091526", outline="#294967", width=2)

    group_colors = ("#38bdf8", "#34d399", "#fbbf24", "#f472b6", "#a78bfa")
    for area in definition.get("areaAnnotations", []):
        if area.get("sourceType") not in {"CadSimulationEvidence", "CadReferencePoint"} or len(area.get("boundary") or []) < 3:
            continue
        polygon = [transform(float(point["x"]), float(point["y"])) for point in area["boundary"]]
        stroke = str(area.get("strokeColor") or "#38bdf8").lstrip("#")
        fill = str(area.get("fillColor") or "#38bdf8").lstrip("#")
        stroke_rgb = tuple(int(stroke[index:index + 2], 16) for index in (0, 2, 4))
        fill_rgb = tuple(int(fill[index:index + 2], 16) for index in (0, 2, 4))
        draw.polygon(polygon, fill=(*fill_rgb, int(255 * float(area.get("fillOpacity") or .08))),
                     outline=(*stroke_rgb, 190))
        label_x = sum(point[0] for point in polygon) / len(polygon)
        label_y = sum(point[1] for point in polygon) / len(polygon)
        text = str(area.get("text") or "")
        if text:
            label_font = tiny if area.get("sourceType") == "CadReferencePoint" else small
            box = draw.textbbox((0, 0), text, font=small)
            draw.rounded_rectangle(
                (label_x - (box[2] - box[0]) / 2 - 6, label_y - 11,
                 label_x + (box[2] - box[0]) / 2 + 6, label_y + 11),
                radius=4,
                fill=(7, 16, 31, 225),
            )
            draw.text((label_x, label_y), text, anchor="mm", fill=stroke_rgb, font=label_font)

    for edge in definition["edges"]:
        if not edge.get("enabled", True):
            continue
        a, b = nodes.get(str(edge["from"])), nodes.get(str(edge["to"]))
        if not a or not b:
            continue
        p1 = transform(float(a["x"]), float(a["y"]))
        p2 = transform(float(b["x"]), float(b["y"]))
        draw.line((*p1, *p2), fill=(61, 101, 137, 155), width=2)

    thresholds = (62, 135, 208, 269, 305)
    for node in definition["nodes"]:
        node_id = str(node["id"])
        site = str(node.get("agvSiteId") or node_id)
        x, y = transform(float(node["x"]), float(node["y"]))
        match = re.fullmatch(r"CNC-(\d{3})", site)
        if match:
            station_number = int(match.group(1))
            group = next(index for index, maximum in enumerate(thresholds) if station_number <= maximum)
            radius = 3.6 if station_number == 1 or station_number % 25 == 0 or station_number == 305 else 2.2
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=group_colors[group])
            if station_number == 1 or station_number % 25 == 0 or station_number == 305:
                draw.text((x + 7, y - 8), site, fill="#c7e5f5", font=tiny)
            continue
        if re.fullmatch(r"WAIT-\d{2}|CHARGE-\d{2}", site):
            radius = 9
            color = "#22d3ee" if site.startswith("WAIT") else "#a78bfa"
        elif site in {"DUMPER-001", "INSPECT-01"}:
            radius = 11
            color = "#fb7185" if site.startswith("DUMPER") else "#f59e0b"
        elif str(node.get("type")) == "Intersection":
            radius = 3
            color = "#64748b"
        else:
            radius = 1.4
            color = "#334155"
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color,
                     outline="#e8f6ff" if radius >= 9 else None, width=2 if radius >= 9 else 0)
        if radius >= 9:
            draw.text((x + 13, y - 10), site, fill=color, font=small)

    panel = (2010, 130, 2360, 1120)
    draw.rounded_rectangle(panel, radius=14, fill="#091526", outline="#294967", width=2)
    draw.text((2038, 160), "地图工程数据", fill="#e8f6ff", font=title)
    facts = (
        ("CNC 工位", "305"),
        ("AGV", "5 台 · 800 × 520 mm"),
        ("设计速度", "0.75 m/s"),
        ("待命位", "5"),
        ("充电位", "2"),
        ("终端倒料位", "1"),
        ("节点", str(len(definition["nodes"]))),
        ("有向道路", str(len(definition["edges"]))),
        ("容量1资源", str(len(definition["resources"]))),
    )
    y = 214
    for label, value in facts:
        draw.text((2040, y), label, fill="#7895ad", font=small)
        draw.text((2180, y), value, fill="#d7ecf6", font=normal)
        y += 52
    y += 22
    draw.text((2040, y), "分区图例", fill="#6ed8c0", font=title)
    y += 50
    for title_text, count_text, color in (
        ("一组取货通道", "62 台", group_colors[0]),
        ("二组取货通道", "73 台", group_colors[1]),
        ("三组取货通道", "73 台", group_colors[2]),
        ("四组取货通道", "61 台", group_colors[3]),
        ("末组取货通道", "36 台", group_colors[4]),
    ):
        draw.ellipse((2042, y + 3, 2058, y + 19), fill=color)
        draw.text((2070, y), title_text, fill="#bdcedd", font=small)
        draw.text((2290, y), count_text, fill="#8fa9be", font=small)
        y += 38
    draw.text((2040, y + 25), "约束", fill="#fda4af", font=title)
    for line in (
        "本图是隔离仿真拓扑，不是车端 SMAP。",
        "CAD未提供连续中心线、转弯半径及避障轮廓。",
        "现场PLC地址、机械和安全联锁仍需逐项核定。",
    ):
        draw.text((2042, y + 75), line, fill="#c8aeb7", font=tiny)
        y += 34

    draw.rectangle((0, 1400, width, height), fill="#091526")
    draw.text(
        (42, 1426),
        "说明：彩色站点与道路来自仿真地图定义；密集区域每25台CNC显示一个业务编号，园区终端区单独保留待命、充电和倒料标注。",
        fill="#7894ad",
        font=small,
    )
    canvas.save(path)


def build(args: argparse.Namespace) -> None:
    viewbox = read_json(args.viewbox) if args.viewbox else DEFAULT_VIEWBOX
    viewbox = {key: float(viewbox[key]) for key in DEFAULT_VIEWBOX}
    stations, rows = detect_station_points(args.cad_preview, viewbox)
    if getattr(args, 'physical_lanes', False):
        from physical_lanes import build_physical_graph
        definition, summary = build_physical_graph(stations, args.cad_dxf, build_graph)
        lane_summary = dict(lanePairCount=6, laneOffsetMeters=None, separatedPhysicalGeometry=True,
                            directedMainEdges=sum(e.get('laneRole') in ('eastbound','westbound','northbound','southbound') for e in definition['edges']),
                            **summary['physicalLanes'])
    else:
        definition, summary = build_graph(stations)
        lane_summary = apply_dual_lane_topology(definition)
    summary['stationEnvelopeGuards'] = apply_station_envelope_guards(definition)
    summary['resourceCount'] = len(definition['resources'])
    station_ids = [station.station_id for station in stations]
    graph_check = valid_graph(definition, [f"WAIT-{index:02d}" for index in range(1, 6)])
    departure_check = check_wait_departure_clearance(definition)
    if not departure_check["passed"]:
        raise ValueError(f"五个待命位中有出场路线与静止车辆包络重叠：{departure_check['violations'][0]}")
    graph_check["waitDepartureClearance"] = departure_check
    plc_text, plc_table = build_plc_csv(station_ids)
    config = build_configuration(
        definition,
        station_ids,
        plc_table,
        args.map_id or "",
        args.map_version_id or "",
        args.definition_hash or "",
    )
    scenario = build_scenario(station_ids)
    source_records = {
        "cadDwg": {"path": str(args.cad_dwg.resolve()), "sha256": sha256(args.cad_dwg)},
        "cadDxf": {"path": str(args.cad_dxf.resolve()), "sha256": sha256(args.cad_dxf)},
        "cadPreview": {"path": str(args.cad_preview.resolve()), "sha256": sha256(args.cad_preview)},
        "acceptancePdf": {"path": str(args.pdf.resolve()), "sha256": sha256(args.pdf), "pages": 25},
    }
    definition["cadProvenance"] = {
        **source_records,
        "coordinateMapping": "CAD millimetres divided by 1000; no translation, scale or rotation",
        "referencePointsAreDispatchStations": False,
    }

    args.output.mkdir(parents=True, exist_ok=True)
    cad_asset = args.output / "cad" / "b07-1f-lower-layout-reference.png"
    cad_asset.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(args.cad_preview, cad_asset)
    (args.output / plc_table["fileName"]).write_text(plc_text, encoding="utf-8-sig")
    write_json(args.output / "cad-station-points.json", {"viewBox": viewbox, "rows": rows, "stations": [
        {
            "stationId": station.station_id,
            "row": station.row_index + 1,
            "column": station.column_index,
            "pixel": {"x": station.pixel_x, "y": station.pixel_y},
            "cadMm": {"x": station.cad_x_mm, "y": station.cad_y_mm},
            "cadM": {"x": station.cad_x_mm / 1000, "y": station.cad_y_mm / 1000},
        }
        for station in stations
    ]})
    write_json(args.output / "cad-reference-points.json", {
        "source": "Active lower-layout CAD text labels",
        "navigationAuthority": "visual-reference-only",
        "points": CAD_REFERENCE_POINTS,
    })
    station_map = {station.station_id: station for station in stations}
    node_map = {node["id"]: node for node in definition["nodes"]}
    station_order = {station_id: index for index, station_id in enumerate(station_ids)}
    with (args.output / "cad-station-points.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=[
            "stationId", "row", "column", "stationNode", "approachNode", "resourceId",
            "cadStationXmm", "cadStationYmm", "cadPickupXmm", "cadPickupYmm",
            "mapStationXm", "mapStationYm", "mapPickupXm", "mapPickupYm",
            "plcByte", "plcFirstAddress",
        ])
        writer.writeheader()
        for station_id in sorted(summary["stationRelations"],
                                 key=lambda value: station_order[value]):
            relation = summary["stationRelations"][station_id]
            source = station_map[station_id]
            station_node = node_map[relation["stationNode"]]
            plc_offset = station_order[station_id] * 2
            writer.writerow({
                "stationId": station_id,
                "row": source.row_index + 1,
                "column": source.column_index,
                "stationNode": relation["stationNode"],
                "approachNode": relation["approachNode"],
                "resourceId": station_id,
                "cadStationXmm": source.cad_x_mm,
                "cadStationYmm": source.cad_y_mm,
                "cadPickupXmm": relation["cadPickupMm"]["x"],
                "cadPickupYmm": relation["cadPickupMm"]["y"],
                "mapStationXm": station_node["x"],
                "mapStationYm": station_node["y"],
                "mapPickupXm": station_node["x"],
                "mapPickupYm": station_node["y"],
                "plcByte": plc_offset,
                "plcFirstAddress": f"DB1.DBX{plc_offset}.0",
            })
    terminal_roles = {
        **{f"WAIT-{index:02d}": "AGV待命位（新双线图采用CAD泊位轮廓中心）" for index in range(1, 6)},
        "CHARGE-01": "充电位1",
        "CHARGE-02": "充电位2",
        "DUMPER-001": "终端倒料工位",
        "INSPECT-01": "检修与设备观察点",
        "WAIT-TOP-SPINE": "北侧待命区离位汇入脊线",
        "WAIT-BOTTOM-SPINE": "南侧待命区离位汇入脊线",
        "WAIT-LOOP-E": "待命区东侧绕行节点",
    }
    with (args.output / "cad-terminal-points.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=[
            "nodeId", "role", "nodeType", "agvSiteId", "xM", "yM", "resourceIds",
        ])
        writer.writeheader()
        for node_id, role in terminal_roles.items():
            if node_id not in node_map:
                continue
            node = node_map[node_id]
            writer.writerow({
                "nodeId": node_id,
                "role": role,
                "nodeType": node["type"],
                "agvSiteId": node["agvSiteId"],
                "xM": node["x"],
                "yM": node["y"],
                "resourceIds": ",".join(node.get("resourceIds") or []),
            })
    write_json(args.output / "cad-simulation-map-definition.json", definition)
    write_json(args.output / "cad-simulation-configuration.template.json", config)
    write_json(args.output / "cad-simulation-scenario-305.json", scenario)
    manifest = {
        "schemaVersion": 1,
        "generatedAt": args.generated_at,
        "mapName": definition.get("name", MAP_NAME),
        "sources": source_records,
        "viewBox": viewbox,
        "coordinateMapping": {
            "cad": "millimetres, original world coordinates",
            "map": "metres, CAD millimetres divided by 1000",
            "scale": "1:1",
        },
        "stationEvidence": {
            "detectedRows": len(rows),
            "rowCounts": [row["count"] for row in rows],
            "total": len(stations),
            "excludedRows": "R1-R2 (73 symbols outside the approved 305-CNC quantity)",
        },
        "cadReferencePoints": {
            "total": len(CAD_REFERENCE_POINTS),
            "byCategory": {
                category: sum(point["category"] == category for point in CAD_REFERENCE_POINTS)
                for category in CAD_REFERENCE_STYLE
            },
            "navigationAuthority": "visual-reference-only",
        },
        "graphCheck": graph_check,
        "dualLaneTopology": lane_summary,
        "navigationAuthority": {
            "routeCenterlinesFromCad": False,
            "generatedSimulationTopology": True,
            "cadReferencePointsAreDispatchStations": False,
            "reason": "The reviewed CAD has equipment and zone labels but no approved AGV centerline, travel-direction, narrowing or obstacle layer.",
        },
        "summary": summary,
        "plcTable": {key: value for key, value in plc_table.items() if key not in ("csvText", "stations", "headers")},
    }
    if getattr(args, 'physical_lanes', False):
        manifest['navigationAuthority'].update(
            cadLineworkUsedAsDesignReference=True,
            reason='User 2026-11-11 marked paired terminal roads, waiting/charging area and dumper approach; CAD bay symbol centres replace text anchors. Directions and docking connections are design proposals, not certified vendor paths.')
    if args.map_id:
        manifest["backendPublication"] = {
            "mapId": args.map_id,
            "versionId": args.map_version_id,
            "definitionHash": args.definition_hash,
            "isActive": False,
        }
    write_json(args.output / "source-manifest.json", manifest)
    report = f"""# {MAP_NAME}

## 结论

- 按照下方有效 CAD 一比一建立米制地图：CAD 原始毫米坐标除以 1000，不做平移、缩放或旋转。
- CAD 中纳入 AGV 服务范围的九排机台逐排识别数量为：{' + '.join(str(v) for v in EXPECTED_ROW_COUNTS)} = 305 台。
- 前两排共 73 个重复设备图形不纳入已确认的 305 台 CNC 数量。
- 五台 AGV、两个充电桩和倒料工位使用 CAD 中的实际文字/设备锚点。
- 另纳入 {len(CAD_REFERENCE_POINTS)} 个 CAD 下方有效布局参考点：10 个码头、8 个 5T 点位、总控/调度及终端处理设备；它们只用于画面核对，未自动成为可派车业务站。
- 地图包含 {graph_check['nodeCount']} 个节点、{graph_check['edgeCount']} 条有向道路、{graph_check['resourceCount']} 个资源（各自容量见定义）。
- 五辆车从 CAD 标注的五个 AGV 待命位置出发，{graph_check['directedReachabilityChecks']} 个有向可达性组合通过。

## 拓扑

- CNC 取货点：每台 CNC 的 CAD 中心点向相邻机台通道偏移 1.1 m；这是待现场核定的仿真设计，部分停靠位会侵入通道包络，不能作为测量级停靠点或最终验收依据。
- 横向通道：按 CAD 机台中背靠背行之间的空档建立五条取货通道。
- 外围通道：使用 CAD 机台区左右边界和南侧边界建立双回路。
- 多车主通道：{lane_summary['lanePairCount']} 对主路段记录东/西、南/北方向角色；共用中心线不是实测双车道。每对反向路段容量 1 保护相向占用，但单靠它不能证明无死锁。
- 无独立会让空间的主路口与取货支线路口均设置 `allowTrafficStop=false`；滚动窗口需继续延伸到可停靠业务位，不能在共用中心线上持有前一段路权等待对向车辆。
- 终端区：五个 CAD 标注 AGV 待命位各增加独立离位点和南北汇入脊线，避免相邻空载车体挡住出场路线；再连接两个充电位、倒料工位和检修点。
- 所有道路使用 0.75 m/s，每个工位、待命位、倒料位和充电位均为容量 1 资源。

## PLC 与业务

- 生成 305 个工位的 8 点业务握手表，共 {plc_table['rowCount']} 行。
- 工位地址依次占用 `DB1.DBX0.0` 至 `DB1.DBX608.7`，倒料使用 `DB1.DBX610.0` 至 `.4`。
- 所有地址状态明确为 `LabOnly`，用于隔离仿真，不是现场 PLC 地址核定。
- 保存场景一次性呼入 305 个工位，由正式调度按五车能力排队和分配。

## 未冒充的内容

- CAD 没有连续 AGV 路线中心线、单双向标志、转弯半径、避障轮廓和车端站点编号。
- 因此码头、5T、总控和终端工艺点只显示为参考点，不能仅依据 CAD 标签自动连线或派车。
- 本图的横向通道和外围联络线依据 CAD 设备空档与蓝色包围区生成，可用于仿真分析，不能直接作为真车导航或地图传输依据。
- 实车前仍应导入厂家 SMAP、核对车端 MD5、逐站确认业务角色、校准安全停点和现场路权。
"""
    if getattr(args, 'physical_lanes', False):
        report = f'''# 成都全厂分离双车道仿真设计

按用户2026-11-10提供的两张截图和授权，沿有效DXF的12条横线建立六组物理分离车道，
上侧向东、下侧向西。西侧外线向南内线向北，东侧外线向北内线向南。
每组在两端和中部配置受控双向连接，所有平面交叉切分为共享图节点及容量1路口。
这些方向为本次仿真设计，不冒充厂家已核定路线。

- CNC数量保持305，机台坐标不移动；取箱位向最近CAD通道偏移1.1m。
- 两线间距最小{summary['physicalLanes']['minimumSeparationMeters']:.3f}m，几何确实分开，不是同一线上的方向标签。
- 终端以CAD两条竖线x=981.90318/983.50192m建立双通道；独立环路连接五待命位。
- 两个充电位保留文字锚点坐标，提升倒料机本体与车辆对接位分别标注。
- 节点{len(definition['nodes'])}、有向道路{len(definition['edges'])}、资源{len(definition['resources'])}。
- 图示dual-lane-layout.png包含方向箭头及终端放大；全部原始坐标、点表、来源摘要同时保留。

部分取箱位会占用近侧车道，已把该站容量1资源绑定到邻道包络，不能把业务完成当成车体消失。
是否能经另一条车道通过仍由正式规划器结合车辆占位、路权、封路计算；无安全路线就等待。
机台设备轮廓、充电/倒料对接姿态和转弯半径尚未测量核定；不能直接下发给现场车辆。
'''
    (args.output / "README.md").write_text(report, encoding="utf-8")
    render_preview(
        args.output / "cad-full-map-preview.png",
        args.cad_preview,
        viewbox,
        stations,
        definition,
    )
    render_vector_preview(
        args.output / "cad-vector-map-preview.png",
        definition,
    )
    print(json.dumps({
        "output": str(args.output.resolve()),
        "stations": len(stations),
        "rowCounts": [row["count"] for row in rows],
        "nodes": graph_check["nodeCount"],
        "edges": graph_check["edgeCount"],
        "resources": graph_check["resourceCount"],
        "plcRows": plc_table["rowCount"],
    }, ensure_ascii=False))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cad-preview", type=Path, required=True)
    parser.add_argument("--cad-dwg", type=Path, required=True)
    parser.add_argument("--cad-dxf", type=Path, required=True)
    parser.add_argument("--pdf", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--generated-at", required=True)
    parser.add_argument("--viewbox", type=Path)
    parser.add_argument("--map-id")
    parser.add_argument("--map-version-id")
    parser.add_argument("--definition-hash")
    parser.add_argument("--physical-lanes", action="store_true", help="按CAD双排线构建实际分离的仿真路线，方向为用户授权的仿真设计。")
    return parser.parse_args()


if __name__ == "__main__":
    build(parse_args())
