"""Freeze the active engineering bundle into a reusable isolated simulation scheme."""

import argparse
import hashlib
import json
import urllib.parse
import urllib.request
from pathlib import Path


def request(base: str, path: str, method: str = "GET", body=None):
    data = None
    headers = {}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base.rstrip("/") + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=120) as response:
        payload = response.read()
        if not payload:
            return None
        return json.loads(payload)


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def compact_hash(value) -> str:
    payload = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest().upper()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-uri", default="http://127.0.0.1:5199")
    parser.add_argument("--control-uri", default=None,
                        help="仿真控制端地址；默认与 --base-uri 相同")
    parser.add_argument("--engineering-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--scheme-name", required=True)
    args = parser.parse_args()

    base = args.base_uri.rstrip("/")
    control = (args.control_uri or args.base_uri).rstrip("/")
    snapshot = request(base, "/api/snapshot")
    if not snapshot.get("map"):
        raise SystemExit("当前运行地图不可用。")
    map_identity = snapshot["map"]
    if not map_identity.get("id") or not map_identity.get("version"):
        raise SystemExit("当前运行地图缺少 ID 或版本。")

    workspace = request(base, "/api/maps/" + urllib.parse.quote(map_identity["id"], safe="") + "/workspace")
    project = workspace.get("project") or {}
    published = workspace.get("published") or {}
    if not project.get("isActive") or project.get("mapId") != map_identity["id"] or project.get("activeVersionId") != map_identity["version"]:
        raise SystemExit("运行地图与已发布工程地图版本不一致。")
    if published.get("mapId") != map_identity["id"] or published.get("versionId") != map_identity["version"] or published.get("status") != "Published":
        raise SystemExit("已发布地图版本与当前运行地图不一致。")

    map_definition = published.get("definition")
    if not isinstance(map_definition, dict):
        raise SystemExit("未取得完整发布地图定义。")
    parameters = request(base, "/api/task-parameters")
    if parameters.get("mapId") != map_identity["id"] or parameters.get("mapVersionId") != map_identity["version"]:
        raise SystemExit("任务参数与当前运行地图版本不一致。")
    configuration = parameters.get("configuration") or {}

    table_snapshot = request(base, "/api/communication/point-table")
    if not table_snapshot.get("csvText"):
        raise SystemExit("当前通讯点表内容不可用。")
    table = request(control, "/api/simulation/point-table-validate", "POST", {
        "fileName": table_snapshot["fileName"],
        "csvText": table_snapshot["csvText"],
        "sourceSha256": table_snapshot["sha256"],
        "sourceFile": table_snapshot.get("relativePath"),
        "sourceDeviceId": table_snapshot.get("deviceId"),
        "sourceDeviceName": table_snapshot.get("deviceName"),
    })
    table["csvText"] = table_snapshot["csvText"]

    draft = read_json(args.engineering_root / "draft.json")
    plan = draft["plan"]
    if plan.get("mapId") != map_identity["id"] or plan.get("mapVersion") != map_identity["version"]:
        raise SystemExit("当前工程草稿不是 5199 正在运行的地图版本。")
    pickups = configuration.get("pickups") or []
    station_priorities = {station["id"]: station.get("priority", 0) for station in plan.get("stations") or []}
    signal_catalog = {station["stationId"]: station for station in table.get("stations") or []}
    stations = []
    for pickup in pickups:
        station_id = pickup["stationId"]
        signal = signal_catalog.get(station_id) or {}
        stations.append({
            "stationId": station_id,
            "pickupSite": pickup["pickupSite"],
            "signalSource": "verified" if signal.get("signalSource") == "verified" and signal.get("fieldConfirmed") is not False
            else "isolated-synthetic",
            "priority": station_priorities.get(station_id, 0),
        })
    if [row["stationId"] for row in stations] != [f"CNC-{index:03d}" for index in range(1, 8)]:
        raise SystemExit("当前任务参数不是预期的 CNC-001 至 CNC-007 七工位配置。")

    dimensions = {
        "AGV-01": (0.810, 0.545),
        "AGV-02": (0.810, 0.545),
        "AGV-03": (1.000, 0.545),
    }
    vehicles = []
    for vehicle in plan.get("vehicles") or []:
        vehicle_id = vehicle["id"]
        length, width = dimensions.get(vehicle_id, (0.810, 0.545))
        vehicles.append({
            "id": vehicle_id,
            "initialSite": vehicle["startNode"],
            "radiusMeters": max(float(vehicle.get("radius") or 0.48814060474416593),
                                (length * length + width * width) ** 0.5 / 2),
            "lengthMeters": length,
            "widthMeters": width,
            "initialLoaded": False,
            "batteryPercent": float(vehicle.get("battery") or 86),
            "speedScale": 0.75,
            "accelerationMetersPerSecondSquared": 0.6,
            "operationSeconds": 5.0,
            "travelCostPercentPerMeter": 0.01,
            "idleCostPercentPerHour": 0.1,
            "voltageCurve": [{"percent": 0, "voltage": 21}, {"percent": 100, "voltage": 26}],
            "chargingGainPercentPerHour": 20.0,
        })
    if len(vehicles) != 3:
        raise SystemExit("当前工程不是三辆车配置。")

    dumper = plan.get("dumpers") or []
    if len(dumper) != 1:
        raise SystemExit("当前工程倒料设备配置不是唯一对象。")
    dumper = dumper[0]
    waiting_sites = [dumper["waitingNode"], *(dumper.get("additionalWaitingNodes") or [])]
    simulation_configuration = {
        "name": "ZM-3AGV V20 三车七工位当前工程",
        "mapDefinition": map_definition,
        "stations": stations,
        "vehicles": vehicles,
        "dumper": {
            "dumpingSite": dumper["dumpingNode"],
            "waitingSite": dumper["waitingNode"],
            "waitingSites": waiting_sites,
            "resourceId": dumper["dumpingResource"],
        },
        # Inspection is a Station-typed bootstrap site. The active map has no
        # separate inspection station; BUS-LM1 is used only for isolated startup.
        "inspectionSite": "BUS-LM1",
        "plcSignalTable": table,
        "mapIdentity": {
            "mapId": map_identity["id"],
            "mapVersionId": map_identity["version"],
            "sourceName": map_identity.get("name") or map_definition.get("name") or "ZM-3AGV",
            "definitionHash": map_definition.get("definitionHash") or map_identity.get("definitionHash"),
        },
    }
    scenario = {
        "name": "dispatch-backlog",
        "rounds": 1,
        "intervalSeconds": 0,
        "timeoutSeconds": 7200,
        "calls": [
            {"stationId": f"CNC-{index:03d}", "atSeconds": 0.0, "vehicleId": None}
            for index in range(1, 8)
        ],
    }
    saved = request(control, "/api/simulation/scheme-save", "POST", {
        "name": args.scheme_name,
        "configuration": simulation_configuration,
        "scenario": scenario,
    })
    scheme_id = saved.get("id") or saved.get("scheme", {}).get("id") or saved.get("selectedSchemeId")
    if not scheme_id:
        catalog = request(control, "/api/simulation")
        scheme_id = next((row.get("id") for row in catalog.get("schemes") or []
                          if row.get("name") == args.scheme_name), None)
    if not scheme_id:
        raise SystemExit("仿真方案已提交，但未从后台目录取得方案编号。")
    args.output.mkdir(parents=True, exist_ok=True)
    request_payload = {
        "name": args.scheme_name,
        "configuration": simulation_configuration,
        "scenario": scenario,
    }
    (args.output / "scheme-request.json").write_text(
        json.dumps(request_payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    manifest = {
        "schemeId": scheme_id,
        "schemeName": args.scheme_name,
        "engineeringVersionId": parameters.get("configuration", {}).get("engineeringSource", {}).get("versionId")
        or (parameters.get("engineeringSource") or {}).get("versionId"),
        "engineeringContentHash": (parameters.get("engineeringSource") or {}).get("contentHash"),
        "mapId": map_identity["id"],
        "mapVersionId": map_identity["version"],
        "mapDefinitionHash": map_definition.get("definitionHash") or map_identity.get("definitionHash"),
        "pointTable": {
            key: table.get(key)
            for key in ("fileName", "sha256", "sourceSha256", "bytes", "signalCount", "stationCount")
        },
        "vehicleOrigins": {vehicle["id"]: vehicle["initialSite"] for vehicle in vehicles},
        "stationPickups": {station["stationId"]: station["pickupSite"] for station in stations},
        "waitingSites": waiting_sites,
        "dumpingSite": dumper["dumpingNode"],
        "scenario": scenario,
        "schemeRequestSha256": compact_hash(request_payload),
    }
    (args.output / "source-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
