"""Recheck the CAD-derived factory fixture without contacting field equipment.

The result proves fixture consistency, not surveyed navigation geometry or PLC
acceptance. CNC coordinates were digitised from a raster CAD preview; vendor
station IDs and PLC addresses in this fixture are synthetic.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import math
from collections import Counter, defaultdict
from pathlib import Path

import ezdxf

from build_full_map import detect_station_points, check_wait_departure_clearance, station_envelope_conflicts


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def audit(root: Path):
    manifest = load(root / "source-manifest.json")
    config = load(root / "cad-simulation-configuration.template.json")
    definition = load(root / "cad-simulation-map-definition.json")
    failures = []

    def check(ok, message):
        if not ok:
            failures.append(message)

    sources = {}
    for key, source in manifest["sources"].items():
        path = Path(source["path"])
        digest = hashlib.sha256(path.read_bytes()).hexdigest().upper() if path.is_file() else None
        sources[key] = dict(path=str(path), actualSha256=digest, matches=digest == source["sha256"])
        check(sources[key]["matches"], f"源文件摘要不一致：{key}")

    points, rows = detect_station_points(Path(manifest["sources"]["cadPreview"]["path"]), manifest["viewBox"])
    expected = {f"CNC-{i:03}" for i in range(1, 306)}
    stations = {s["stationId"]: s for s in config["stations"]}
    check(len(stations) == len(config["stations"]) == 305 and set(stations) == expected, "业务工位应完整且唯一覆盖 CNC-001～305")
    check({p.station_id for p in points} == expected, "CAD重新识别点位与业务工位不一致")
    check(config["mapDefinition"] == definition, "方案内嵌地图与独立地图文件不一致")
    nodes = {n["id"]: n for n in definition["nodes"]}
    unverified_stops = [n['id'] for n in nodes.values() if n['type'] in ('Intersection', 'Waypoint')
                        and n.get('allowTrafficStop') is not False]
    check(not unverified_stops, "CAD通道路口缺少禁止中途停车标记；没有独立会让空间时不可拆窗等待")
    check(len(nodes) == len(definition["nodes"]), "导航节点ID重复")
    check(len({n.get("agvSiteId") for n in nodes.values()}) == len(nodes), "厂家站点映射重复")
    resources = {r["id"]: r for r in definition["resources"]}
    check(len(resources) == len(definition["resources"]), "资源ID重复")
    check(len({e['id'] for e in definition['edges']}) == len(definition['edges']), "道路ID重复")
    graph = defaultdict(set)
    for e in definition["edges"]:
        check(e["from"] in nodes and e["to"] in nodes, f"道路端点缺失：{e['id']}")
        check(all(r in resources for r in e.get("resourceIds", [])), f"道路引用未知资源：{e['id']}")
        if e.get("enabled", True) and e["from"] in nodes and e["to"] in nodes:
            graph[e["from"]].add(e["to"])
            if e.get("bidirectional", False):
                graph[e["to"]].add(e["from"])

    # Intermediate business stations are not shortcuts through another machine.
    def reachable(start, end):
        pending, seen = [start], set()
        while pending:
            current = pending.pop()
            if current == end:
                return True
            if current in seen or current not in nodes:
                continue
            seen.add(current)
            n = nodes[current]
            if current != start and n["type"] in ("Station", "WaitingPoint", "ChargingPoint") and not n.get("allowTransitThrough", False):
                continue
            pending.extend(graph[current] - seen)
        return False

    parked_road_conflicts = [(e['id'], resource) for e, resource in station_envelope_conflicts(definition)
                            if resource not in (nodes[e['from']].get('agvSiteId'), nodes[e['to']].get('agvSiteId'))]
    check(not parked_road_conflicts, '仿真停靠位侵入其他道路包络，不能以最终释放任务路权当作车体消失')
    missing_envelope_guards = [(e['id'], resource) for e, resource in station_envelope_conflicts(definition)
                               if resource not in e.get('resourceIds', [])]
    check(not missing_envelope_guards, '停靠车体影响邻近道路但缺少资源约束')
    route_checks = 0
    origins = [v["initialSite"] for v in config["vehicles"]]
    dumper = config["dumper"]["dumpingSite"]
    waiting = config["dumper"]["waitingSites"]
    chargers = [n["id"] for n in nodes.values() if n["type"] == "ChargingPoint"]
    for s in config["stations"]:
        pickup = s["pickupSite"]
        check(pickup in nodes, f"工位缺少取货节点：{s['stationId']}")
        for a, b in [(o, pickup) for o in origins] + [(pickup, dumper), (dumper, pickup)] + [(pickup, w) for w in waiting] + [(pickup, c) for c in chargers]:
            route_checks += 1
            check(reachable(a, b), f"有向路线不通：{a}→{b}")
    for p in points:
        n = nodes.get(stations.get(p.station_id, {}).get("pickupSite"), {})
        # The docking offset is a documented simulation design, not a survey.
        check(abs(n.get("x", math.inf) - p.cad_x_mm / 1000) < 1e-6, f"机台X映射不一致：{p.station_id}")
        check(abs(abs(n.get("y", math.inf) - p.cad_y_mm / 1000) - 1.1) < 1e-6, f"机台取货偏移不一致：{p.station_id}")

    check(len(config["vehicles"]) == 5 and len(set(origins)) == 5, "应有五辆车和五个独立起点")
    check(len(chargers) == 2, "应有两个充电位")
    check(resources.get(config["dumper"]["resourceId"], {}).get("capacity") == 1, "倒料容量必须为1")
    for v in config["vehicles"]:
        check(v.get("lengthMeters") == .8 and v.get("widthMeters") == .52, f"车辆尺寸不符合最新800×520口径：{v['id']}")
    clearance = check_wait_departure_clearance(definition)
    check(clearance["passed"], "待命车辆离位包络校验失败")

    signals = list(csv.DictReader(io.StringIO(config["plcSignalTable"]["csvText"])))
    addresses = [s["address"].strip().upper() for s in signals]
    check(len(set(addresses)) == len(addresses), "仿真PLC地址重复")
    check(all(s["addressStatus"] == "LabOnly" for s in signals), "合成点表必须标明LabOnly")
    by_station = defaultdict(set)
    for s in signals:
        by_station[s["stationId"]].add(s["variableName"])
    required = {"FullBoxRequested", "BoxPresent", "Faulted", "CommunicationOnline", "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced"}
    for sid in expected:
        check(by_station[sid] == required, f"工位握手信号缺失或不一致：{sid}")

    dxf = ezdxf.readfile(manifest["sources"]["cadDxf"]["path"])
    from build_full_map import CAD_REFERENCE_POINTS, WAIT_MM, CHARGER_MM
    for reference in CAD_REFERENCE_POINTS:
        entity = dxf.entitydb.get(reference["handle"])
        check(entity is not None, f"CAD参考实体不存在：{reference['id']}")
        if entity is not None:
            check(math.hypot(entity.dxf.insert.x-reference["xMm"], entity.dxf.insert.y-reference["yMm"]) < .001, f"CAD参考坐标不一致：{reference['id']}")
    text_anchors = [e.dxf.insert for e in dxf.modelspace().query("TEXT MTEXT")]
    for x, y in WAIT_MM + CHARGER_MM:
        check(any(math.hypot(a.x-x, a.y-y) < .001 for a in text_anchors), "待命或充电位CAD文字锚点缺失")
    return dict(passed=not failures, failures=failures, sources=sources,
                counts=dict(cnc=len(stations), vehicles=len(config["vehicles"]), chargers=len(chargers),
                            dumpers=1, nodes=len(nodes), directedEdges=len(definition["edges"]), resources=len(resources),
                            signals=len(signals), cadReferences=len(CAD_REFERENCE_POINTS)),
                rowCounts=[r["count"] for r in rows], directedRouteChecks=route_checks,
                waitDeparture=clearance,
                unverifiedTransitStops=unverified_stops,
                missingStationEnvelopeGuards=missing_envelope_guards,
                parkedRoadConflicts=parked_road_conflicts,
                limitations=["CNC编号为仿真顺序编号，须与现场铭牌和厂家站点表核对。",
                             "CNC坐标来自CAD预览像素识别，约69.44mm/像素，不是测量级CAD实体中心。",
                             "取货点偏移1.1m、终端对接点、道路及其方向/容量属于待确认仿真设计。",
                             ("充电点采用CAD泊位轮廓中心，充电桩实际对接姿态仍待标定。"
                              if manifest.get("summary",{}).get("physicalLanes",{}).get("terminalLayout") else
                              "充电点为CAD文字锚点，尚不等于充电桩实际对接姿态。"),
                             "PLC地址、初始电量、耗电及电压曲线为隔离合成数据，不能作为现场验收依据。",
                             ("本图为实际分离的CAD双排线，方向由用户授权设计；尚未核定现场转弯、机台轮廓和对接姿态。"
                              if manifest.get('dualLaneTopology',{}).get('separatedPhysicalGeometry') else
                              "逻辑双向车道共用物理中心线；未证明真实双车并行间距或现场转弯包络。")])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.root)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k not in ("sources", "limitations")}, ensure_ascii=False))
    raise SystemExit(0 if result["passed"] else 1)
