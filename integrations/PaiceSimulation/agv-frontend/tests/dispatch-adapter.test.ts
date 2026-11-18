import test from "node:test";
import assert from "node:assert/strict";
import { findMapMatches, mapNavigationItems } from "../src/integration/map-navigation.ts";
import {
  convertDispatchMap,
  applyImportRoadDirections,
  sourcePointTraffic,
  dispatchSceneMap,
  dispatchViews,
  transportTasks,
  publishedDispatchMap,
  dispatchIsFresh,
  dispatchRoutePoints,
  resolveMapSiteId,
  roadnetFingerprint,
  selectWebSimulationMap,
  simulationRoadnetName,
} from "../src/integration/dispatch-adapter.ts";

const definition = {
  unit: "m",
  coordinateSystem: "test",
  nodes: [
    { id: "A", agvSiteId: "LM1", x: 100, y: 200, type: "Station" },
    { id: "B", x: 104, y: 200, type: "Station" },
    { id: "C", x: 100, y: 204, type: "Station" },
  ],
  edges: [
    {
      id: "curve",
      from: "A",
      to: "B",
      distance: 6,
      speedLimit: 0.75,
      geometryKind: "CubicBezier",
      controlPoints: [
        { x: 100, y: 204 },
        { x: 104, y: 204 },
      ],
      bidirectional: true,
    },
  ],
  resources: [{ id: "R", capacity: 1 }],
};

test("map location prioritizes exact IDs and resolves source aliases and road segments", () => {
  const items = mapNavigationItems({
    nodes: [
      { id: "BUS-CNC-001", x: 1, y: 1, type: "WORKSTATION" },
      { id: "CNC-0010", x: 5, y: 3, type: "WORKSTATION" },
    ],
    edges: [{ id: "A__1", from: "BUS-CNC-001", to: "CNC-0010", length: 4, speed: 1, direction: "ONE_WAY", capacity: 1 }],
    integration: {
      sourceDefinition: { nodes: [{ id: "BUS-CNC-001", agvSiteId: "CNC-001" }] },
      edgeMapping: { "CAD-E01": ["A__1"] },
    },
  });
  assert.equal(findMapMatches(items, "cnc-001")[0]?.id, "BUS-CNC-001");
  assert.deepEqual(findMapMatches(items, "CAD-E01").map(({ kind, x, y }) => [kind, x, y]), [["edge", 3, 2]]);
});

test("map conversion preserves invertible source coordinates, subdivides curves and declared length", () => {
  const result = convertDispatchMap(definition, "C");
  assert.equal(result.roadnet.edges.length, 24);
  assert.equal(
    result.roadnet.nodes.find((n) => n.id === "A")!.x +
      result.coordinateOffset.x,
    100,
  );
  assert.equal(result.roadnet.nodes.find((n) => n.id === "C")?.type, "CHARGER");
  assert.ok(
    Math.abs(result.roadnet.edges.reduce((n, e) => n + e.length, 0) - 6) < 1e-8,
  );
  assert.equal(result.roadnet.edges[0]?.speed, 0.75);
  assert.equal(result.edgeMapping.curve?.length, 24);
  assert.deepEqual(result.sourceDefinition, definition);
  assert.ok(result.warnings.some((w) => w.includes("区域资源")));
});
test("unknown units and missing speed fail explicitly, disabled paths are excluded", () => {
  assert.throws(
    () => convertDispatchMap({ ...definition, unit: "mm" }, "C"),
    /米制/,
  );
  assert.throws(
    () =>
      convertDispatchMap(
        {
          ...definition,
          edges: [{ ...definition.edges[0], speedLimit: undefined }],
        },
        "C",
      ),
    /速度/,
  );
  assert.equal(
    convertDispatchMap(
      { ...definition, edges: [{ ...definition.edges[0], enabled: false }] },
      "C",
    ).roadnet.edges.length,
    0,
  );
});

test("simplified road preview keeps endpoints and declared distance", () => {
  const result = convertDispatchMap(definition, "C", { simplifyRoads: true });
  assert.equal(result.roadnet.edges.length, 1);
  assert.equal(result.roadnet.edges[0]?.id, "curve");
  assert.equal(result.roadnet.edges[0]?.from, "A");
  assert.equal(result.roadnet.edges[0]?.to, "B");
  assert.equal(result.roadnet.edges[0]?.length, 6);
  assert.ok(result.warnings.some((warning) => warning.includes("简化道路")));
});
test("vehicle adapter converts radians to degrees and keeps unknown or stale states", () => {
  const cars = dispatchViews(
    {
      one: { x: 100, y: 200, angle: Math.PI / 2, moving: true },
      two: { x: 101, y: 202, offline: true },
    },
    true,
  );
  assert.equal(cars[0]?.heading, 90);
  assert.equal(cars[0]?.y, -200);
  assert.equal(cars[1]?.status, "OFFLINE");
  assert.equal(
    dispatchViews({ one: { x: 0, y: 0, moving: true } }, false)[0]?.status,
    "OFFLINE",
  );
});
test("live scene uses the same Y direction as the Web simulation map", () => {
  const scene = dispatchSceneMap(definition);
  assert.equal(scene.nodes.find((node) => node.id === "A")?.y, -200);
  assert.deepEqual(scene.edges[0]?.points?.[0], { x: 100, y: -200 });
  assert.deepEqual(scene.edges[0]?.points?.at(-1), { x: 104, y: -200 });
});
test("transport extraction keeps original identities and explains unsupported endpoints", () => {
  const result = convertDispatchMap(definition, "C");
  const tasks = transportTasks(
    [
      {
        task: { taskId: "original" },
        commands: [
          { commandId: "cmd-1", route: { nodeIds: ["A", "B"] } },
          { commandId: "cmd-2", route: { nodeIds: ["B", "C"] } },
        ],
      },
    ],
    result.roadnet,
  );
  assert.equal(tasks.tasks.length, 1);
  assert.equal(tasks.mapping[0]?.taskId, "original");
  assert.equal(tasks.mapping[0]?.commandId, "cmd-1");
  assert.equal(tasks.skipped.length, 1);
});
test("published map lookup verifies identity, hash, observation and publication state", () => {
  const snapshot = {
    connected: true,
    source: "backend",
    receivedAt: new Date().toISOString(),
    map: { id: "map", version: "v1", definitionHash: "abc", unit: "m" },
  };
  const workspace = {
    project: { mapId: "map", isActive: true, activeVersionId: "v1" },
    published: {
      mapId: "map",
      versionId: "v1",
      status: "Published",
      definition,
    },
  };
  assert.equal(publishedDispatchMap(snapshot, workspace), definition);
  assert.throws(
    () => publishedDispatchMap({ ...snapshot, connected: false }, workspace),
    /有效/,
  );
  assert.throws(
    () =>
      publishedDispatchMap(snapshot, {
        ...workspace,
        published: { ...workspace.published, versionId: "v2" },
      }),
    /版本/,
  );
});

test("source freshness expires on its device clock even when polling succeeds", () => {
  const now = Date.now(),
    status = {
      enabled: true,
      running: true,
      fresh: true,
      device: { time: now / 1000 },
    };
  assert.equal(dispatchIsFresh(status, now), true);
  assert.equal(dispatchIsFresh(status, now + 5001), false);
  assert.equal(dispatchIsFresh({ ...status, running: false }, now), false);
});
test("selected route follows sampled geometry in either direction", () => {
  const map = {
    nodes: [],
    edges: [
      {
        id: "E",
        from: "A",
        to: "B",
        length: 2,
        speed: 1,
        capacity: 1,
        direction: "TWO_WAY" as const,
        points: [
          { x: 0, y: 0 },
          { x: 1, y: 1 },
          { x: 2, y: 0 },
        ],
      },
    ],
  };
  assert.deepEqual(
    dispatchRoutePoints(map, ["A", "B"], ["E"]),
    map.edges[0].points,
  );
  assert.deepEqual(
    dispatchRoutePoints(map, ["B", "A"], ["E"]),
    [...map.edges[0].points].reverse(),
  );
});
test("native JSON property ordering does not invalidate imported graph identity", () => {
  const a = convertDispatchMap(definition, "C").roadnet;
  const b = JSON.parse(JSON.stringify(a));
  b.edges = b.edges.map((e: any) => ({
    id: e.id,
    from: e.from,
    to: e.to,
    length: e.length,
    direction: e.direction,
    speed: e.speed,
    capacity: e.capacity,
    laneMode: e.laneMode,
  }));
  assert.equal(roadnetFingerprint(a), roadnetFingerprint(b));
});

test("reciprocal roads merge into one bidirectional road with both source identities", () => {
  const d = {
    ...definition,
    edges: [
      {
        id: "forward",
        from: "A",
        to: "B",
        geometryKind: "Line",
        distance: 4,
        speedLimit: 1,
      },
      {
        id: "reverse",
        from: "B",
        to: "A",
        geometryKind: "Line",
        distance: 4,
        speedLimit: 1,
      },
    ],
  };
  const result = convertDispatchMap(d, "C");
  assert.equal(result.roadnet.nodes.length, 3);
  assert.equal(result.roadnet.edges.length, 1);
  assert.equal(result.roadnet.edges[0]?.direction, "TWO_WAY");
  assert.deepEqual(result.edgeMapping.forward, [result.roadnet.edges[0]?.id]);
  assert.deepEqual(result.edgeMapping.reverse, [result.roadnet.edges[0]?.id]);
});

test("parallel road separation remains available when reciprocal merging is disabled", () => {
  const d = {
    ...definition,
    edges: [
      {
        id: "forward",
        from: "A",
        to: "B",
        geometryKind: "Line",
        distance: 4,
        speedLimit: 1,
      },
      {
        id: "reverse",
        from: "B",
        to: "A",
        geometryKind: "Line",
        distance: 4,
        speedLimit: 1,
      },
    ],
  };
  const result = convertDispatchMap(d, "C", {
    mergeOppositeParallel: false,
  });
  assert.equal(result.roadnet.edges.length, 4);
  const pairs = result.roadnet.edges.map((e) =>
    [e.from, e.to].sort().join("|"),
  );
  assert.equal(new Set(pairs).size, 4);
});
test("optional round-trip scope excludes directed CAD tails with explicit records", () => {
  const d = {
    ...definition,
    nodes: [
      ...definition.nodes,
      { id: "tail", x: 110, y: 200, type: "Waypoint" },
    ],
    edges: [
      {
        id: "AB",
        from: "A",
        to: "B",
        geometryKind: "Line",
        distance: 4,
        speedLimit: 1,
        bidirectional: true,
      },
      {
        id: "BC",
        from: "B",
        to: "C",
        geometryKind: "Line",
        distance: 6,
        speedLimit: 1,
        bidirectional: true,
      },
      {
        id: "tail-road",
        from: "A",
        to: "tail",
        geometryKind: "Line",
        distance: 10,
        speedLimit: 1,
      },
    ],
  };
  const result = convertDispatchMap(d, "C", { roundTripOnly: true });
  assert.ok(!result.roadnet.nodes.some((n) => n.id === "tail"));
  assert.deepEqual(result.excludedNodes, ["tail"]);
  assert.deepEqual(result.excludedEdges, ["tail-road"]);
  assert.equal(sourcePointTraffic(d, "tail").reason, "只进不出");
  assert.equal(sourcePointTraffic(d, "tail").incoming, 1);
  const changed = applyImportRoadDirections(d, { "tail-road": true });
  assert.equal(sourcePointTraffic(changed, "tail").outgoing, 1);
  assert.deepEqual(convertDispatchMap(changed, "C", { roundTripOnly: true }).excludedNodes, []);
  assert.equal(sourcePointTraffic(d, "tail").outgoing, 0);
  assert.throws(
    () => convertDispatchMap(d, "C", { roundTripOnly: true, protectedSiteIds: ["tail"] }),
    /tail.*无法与初始点往返/,
  );
  const workingTail = {
    ...d,
    nodes: d.nodes.map(node => node.id === "tail" ? { ...node, type: "Station" } : node),
  };
  assert.throws(
    () => convertDispatchMap(workingTail, "C", { roundTripOnly: true, protectedSiteIds: [] }),
    /tail.*无法与初始点往返/,
  );
});

test("source charging points retain charger type while transit waiting points remain junctions", () => {
  const result = convertDispatchMap({
    ...definition,
    nodes: [
      ...definition.nodes,
      { id: "charge", x: 0, y: 0, type: "ChargingPoint" },
      { id: "wait", x: 1, y: 1, type: "WaitingPoint" },
    ],
  });
  assert.equal(
    result.roadnet.nodes.find((n) => n.id === "charge")?.type,
    "CHARGER",
  );
  assert.equal(
    result.roadnet.nodes.find((n) => n.id === "wait")?.type,
    "JUNCTION",
  );
  assert.ok(result.warnings.some((w) => w.includes("WaitingPoint")));
});

test("Web vehicle station resolves to the matching map node by agvSiteId or node id", () => {
  const map = {
    ...definition,
    nodes: [
      { id: "BUS-LM1", agvSiteId: "LM1", x: 1, y: 2, type: "Station" },
      { id: "LM9", x: 3, y: 4, type: "Station" },
    ],
    edges: [],
  };
  assert.equal(resolveMapSiteId(map, "LM1"), "BUS-LM1");
  assert.equal(resolveMapSiteId(map, "LM9"), "LM9");
  assert.equal(resolveMapSiteId(map, "UNKNOWN"), "");
});

test("Web simulation map selection prefers the published five-car factory map", () => {
  const selected = selectWebSimulationMap([
    {
      mapId: "site",
      name: "ZM-3AGV",
      description: "现场三车地图",
      unit: "m",
      isActive: true,
      publishedVersionId: "site-v7",
      updatedAt: "2026-11-08T17:54:56+08:00",
    },
    {
      mapId: "factory",
      name: "B07-1F·全厂305CNC·五车仿真图",
      description: "305台CNC、5台AGV",
      unit: "m",
      isActive: false,
      publishedVersionId: "factory-v5",
      updatedAt: "2026-11-06T18:46:59+08:00",
    },
  ]);
  assert.equal(selected?.mapId, "factory");
});

test("Web simulation map selection falls back to the current active published map", () => {
  const selected = selectWebSimulationMap([
    {
      mapId: "draft",
      name: "未发布地图",
      unit: "m",
      isActive: true,
      publishedVersionId: null,
      updatedAt: "2026-11-08T17:54:56+08:00",
    },
    {
      mapId: "active",
      name: "当前活动地图",
      unit: "m",
      isActive: true,
      publishedVersionId: "active-v1",
      updatedAt: "2026-11-07T17:54:56+08:00",
    },
  ]);
  assert.equal(selected?.mapId, "active");
});

test("simulation roadnet names stay short for the live scene", () => {
  assert.equal(
    simulationRoadnetName({
      ...definition,
      name: "28阶段双车闭环联调车间",
    }),
    "真实场景仿真",
  );
  assert.equal(
    simulationRoadnetName({
      ...definition,
      name: "B07-1F·全厂305CNC·五车仿真图",
    }),
    "全厂五车仿真",
  );
  assert.equal(
    simulationRoadnetName({ ...definition, name: "自定义地图" }),
    "自定义地图（仿真）",
  );
});

test("translated negative engineering coordinates fit the original positive editor canvas", () => {
  const d = {
    ...definition,
    nodes: definition.nodes.map((n) => ({ ...n, x: n.x + 900, y: n.y - 600 })),
  };
  const result = convertDispatchMap(d, "C");
  assert.ok(
    result.roadnet.nodes.every(
      (n) =>
        n.x >= 0 &&
        n.y >= 0 &&
        n.x <= result.roadnet.width! &&
        n.y <= result.roadnet.height!,
    ),
  );
  assert.equal(result.roadnet.nodes[0].x + result.coordinateOffset.x, 1000);
  assert.equal(result.coordinateOffset.y - result.roadnet.nodes[0].y, -400);
});
