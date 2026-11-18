import type { SourceNode } from "./dispatch-adapter.ts";

export interface EngineeringPoint {
  label: string;
  nodeId: string;
  group: string;
}

export function inspectEngineeringPoints(
  nodes: ReadonlyMap<string, SourceNode>,
  points: EngineeringPoint[],
) {
  const groups = new Map<string, EngineeringPoint[]>();
  for (const point of points) {
    const key = JSON.stringify([point.group, point.nodeId]);
    const list = groups.get(key) ?? [];
    list.push(point);
    groups.set(key, list);
  }
  return points.flatMap(point => {
    const node = nodes.get(point.nodeId);
    const peers = groups.get(JSON.stringify([point.group, point.nodeId])) ?? [];
    const reason = !node ? "地图点位不存在"
      : node.enabled === false ? "地图点位已停用"
      : !Number.isFinite(node.x) || !Number.isFinite(node.y) ? "地图坐标无效"
      : point.group === "station" && node.type && node.type !== "Station" ? "停靠点类型不是工位"
      : peers.length > 1 ? "与 " + peers.filter(peer => peer !== point).map(peer => peer.label).join("、") + " 共用点位"
      : "";
    return reason ? [{ ...point, reason }] : [];
  });
}

interface PlcSignal {
  address: string;
  name: string;
  direction: string;
  access: string;
  enabled: boolean;
}

export function readPlcSignals(text: string) {
  // Point tables have already passed backend validation. Preserve escaped
  // quotes and line breaks in quoted CSV fields while selecting useful columns.
  const result = new Map<string, PlcSignal[]>();
  const rows = text.replace(/^\uFEFF/, "").matchAll(/(?:^|,|\r\n|\n|\r)(?:"((?:[^"]|"")*)"|([^",\r\n]*))/g);
  let headers: string[] | undefined;
  let fields: string[] = [];
  function finishRow() {
    if (!headers) {
      headers = fields;
      return;
    }
    const field = (key: string) => (fields[headers!.indexOf(key)] ?? "").trim();
    const station = field("stationId");
    if (!station) return;
    const signals = result.get(station) ?? [];
    signals.push({
      address: field("address"),
      name: field("variableName"),
      direction: field("direction"),
      access: field("access"),
      enabled: field("enabled").toLowerCase() === "true",
    });
    result.set(station, signals);
  }
  for (const match of rows) {
    if (match[0].startsWith("\n") || match[0].startsWith("\r")) {
      finishRow();
      fields = [];
    }
    fields.push(match[1]?.replace(/""/g, '"') ?? match[2] ?? "");
  }
  if (fields.length) finishRow();
  return result;
}
