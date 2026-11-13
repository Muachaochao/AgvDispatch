import test from "node:test";
import assert from "node:assert/strict";
import {
  convertDispatchMap,
  dispatchViews,
  transportTasks,
  publishedDispatchMap,
  dispatchIsFresh,
  dispatchRoutePoints,
  roadnetFingerprint,
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
test("vehicle adapter converts radians to degrees and keeps unknown or stale states", () => {
  const cars = dispatchViews(
    {
      one: { x: 100, y: 200, angle: Math.PI / 2, moving: true },
      two: { x: 101, y: 202, offline: true },
    },
    true,
  );
  assert.equal(cars[0]?.heading, 90);
  assert.equal(cars[0]?.y, 200);
  assert.equal(cars[1]?.status, "OFFLINE");
  assert.equal(
    dispatchViews({ one: { x: 0, y: 0, moving: true } }, false)[0]?.status,
    "OFFLINE",
  );
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

test("parallel directed source roads preserve independent identities through distinct intermediate nodes", () => {
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
  assert.equal(result.roadnet.nodes[0].y + result.coordinateOffset.y, -400);
});
