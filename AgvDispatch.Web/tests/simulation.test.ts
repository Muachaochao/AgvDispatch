import test from 'node:test';
import assert from 'node:assert/strict';
import { simulationFresh, scenarioActive, simulationGeometry, simulationTone, simulationLabel, simulationMapFromPublishedVersion, loadCurrentSimulationMap,
  simulationMetricLabel, simulationMetricValue, simulationMetricComparison, simulationReplayCars, type SimulationMapWorkspace, type SimulationStatus } from '../src/simulation.ts';
import type { Snapshot, WebMap } from '../src/types.ts';

const now = Date.parse('2026-11-04T10:00:00+08:00');
const sample = (patch: Partial<SimulationStatus> = {}): SimulationStatus => ({ enabled: true, running: true, fresh: true, device: { time: now / 1000, cars: {} }, ...patch });

test('controls require a running model with a recent, non-future device sample', () => {
  assert.equal(simulationFresh(sample(), now), true);
  for (const input of [null, sample({ enabled: false }), sample({ running: false }), sample({ fresh: false }), sample({ device: null }),
    sample({ device: { time: (now - 6000) / 1000 } }), sample({ device: { time: (now + 3000) / 1000 } }), sample({ device: { time: NaN } })])
    assert.equal(simulationFresh(input, now), false);
});

test('a finished, aborted or unknown scenario never prevents reading original records', () => {
  assert.equal(scenarioActive({ status: 'running' }), true);
  assert.equal(scenarioActive({ status: 'starting' }), true);
  for (const status of ['passed', 'failed', 'aborted', 'idle', 'Unrecognized']) assert.equal(scenarioActive({ status }), false);
});

test('unknown or missing evidence is visibly distinct from passed checks', () => {
  for (const status of ['unknown', 'skipped', 'inconclusive', 'not_checked', undefined]) assert.notEqual(simulationTone(status), 'pass');
  assert.equal(simulationTone('passed'), 'pass');
  assert.equal(simulationTone('failed'), 'fail');
  assert.equal(simulationLabel('skipped'), '未执行');
});

test('report metrics stay readable in Chinese and invalid numbers cannot appear as a valid observation', () => {
  assert.equal(simulationMetricLabel('durationSeconds'), '运行时长（秒）');
  assert.equal(simulationMetricLabel('newInternalEnglishKey'), '扩展检查指标');
  assert.equal(simulationMetricValue(NaN), '证据无效');
  assert.equal(simulationMetricValue(Infinity), '证据无效');
  assert.equal(simulationMetricValue(false), '否');
  assert.equal(simulationMetricValue(2.34567), '2.346');
});

test('history comparison uses only common finite numeric evidence and preserves direction without inferring improvement', () => {
  assert.deepEqual(simulationMetricComparison({ taskCount: 4, durationSeconds: 20, samples: NaN, unknown: '10' },
    { taskCount: 2, durationSeconds: 30, samples: 2, unknown: 5 }), [
    { key: 'taskCount', label: '任务数', current: 4, previous: 2, delta: 2 },
    { key: 'durationSeconds', label: '运行时长（秒）', current: 20, previous: 30, delta: -10 }
  ]);
});

test('replay retains the recorded original identity and omits invalid positions without fabricating a live state', () => {
  const cars = simulationReplayCars({ time: 12, vehicles: [
    { vehicleId: 'AGV-1', x: 3, y: 4, site: 'LM4', taskId: 'ORIGINAL', waitReason: '等待倒料资源' },
    { vehicleId: 'broken', x: NaN, y: 4 }, { vehicleId: '', x: 0, y: 0 }
  ] });
  assert.deepEqual(Object.keys(cars), ['AGV-1']);
  assert.equal(cars['AGV-1'].current_task, 'ORIGINAL');
  assert.equal(cars['AGV-1'].waitReason, '等待倒料资源');
  assert.equal(cars['AGV-1'].offline, undefined);
  assert.deepEqual(simulationReplayCars(), {});
});

test('map joins edges by node ID or vehicle site ID and ignores invalid coordinates', () => {
  const result = simulationGeometry([
    { id: '取料', agvSiteId: 'LM1', x: -20, y: 10 }, { id: '倒料', agvSiteId: 'LM2', x: 30, y: -10 }, { id: 'bad', x: NaN, y: 0 }
  ], [{ from: '取料', to: '倒料' }, { from: 'LM1', to: 'LM2' }, { from: 'bad', to: '倒料' }],
  { 'AGV-01': { x: -20, y: 10 }, 'AGV-02': { x: NaN, y: 0 } });
  assert.equal(result.nodes.length, 2); assert.equal(result.edges.length, 2); assert.equal(result.cars.length, 1);
  assert.equal(result.cars[0].x, result.nodes[0].x); assert.equal(result.cars[0].y, result.nodes[0].y);
  assert.ok(result.nodes.every(node => Number.isFinite(node.x) && node.x >= 0 && node.x <= 800 && node.y >= 0 && node.y <= 430));
  assert.deepEqual(result.edges[0].a, result.edges[1].a);
});

test('cubic curves preserve their real control points and remain inside the map viewport', () => {
  const result = simulationGeometry([{ id: 'A', x: 0, y: 0 }, { id: 'B', x: 10, y: 0 }],
    [{ id: 'curve', from: 'A', to: 'B', geometryKind: 'CubicBezier', controlPoints: [{ x: -5, y: 30 }, { x: 15, y: 30 }] }], {});
  assert.ok(result.edges[0].path.includes('C ')); assert.ok(!result.edges[0].path.includes('NaN'));
  assert.ok(result.nodes.every(node => node.x >= 0 && node.x <= 800 && node.y >= 0 && node.y <= 430));
});

const liveMap = {
  id: 'MAP-LIVE', version: 'MAPV-LIVE', definitionHash: 'immutable-definition-hash', name: '当前运行图', unit: 'm', coordinateSystem: 'RobotWorld',
  nodes: [], edges: [], resources: [], warnings: []
} as WebMap;
const workspace = (): SimulationMapWorkspace => ({
  project: { mapId: liveMap.id, isActive: true, activeVersionId: liveMap.version },
  published: { mapId: liveMap.id, versionId: liveMap.version, status: 'Published', definition: {
    name: liveMap.name, source: 'original source', unit: 'm', coordinateSystem: 'RobotWorld',
    robokitSource: { mapName: 'robot-original', sha256: 'source-hash' },
    nodes: [
      { id: 'BUS-LM1', agvSiteId: 'LM1', x: 1, y: 2, type: 'Station', enabled: true, resourceIds: ['R1'] },
      { id: 'BUS-LM2', agvSiteId: 'LM2', x: 3, y: 4, type: 'WaitingPoint', enabled: true, resourceIds: ['WAIT-EXPLICIT'] },
      { id: 'BUS-LM3', agvSiteId: 'LM3', x: 5, y: 6, type: 'Station', enabled: true, resourceIds: [] }
    ],
    edges: [
      { id: 'E1', from: 'BUS-LM1', to: 'BUS-LM2', enabled: true, bidirectional: true, geometryKind: 'CubicBezier',
        distance: 12.34, controlPoints: [{ x: 2, y: 15 }, { x: 4, y: -10 }], speedLimit: 0.6, resourceIds: ['R1'] },
      { id: 'E2', from: 'BUS-LM2', to: 'BUS-LM3', enabled: true, bidirectional: false, geometryKind: 'Line',
        distance: 4, speedLimit: 0.4, controlPoints: null, resourceIds: [] }
    ],
    resources: [
      { id: 'R1', type: 'Station', capacity: 1, boundary: [{ x: 0, y: 0 }, { x: 2, y: 0 }, { x: 2, y: 2 }] },
      { id: 'WAIT-EXPLICIT', type: 'Station', capacity: 1, boundary: [{ x: 2, y: 3 }, { x: 4, y: 3 }, { x: 4, y: 5 }] },
      { id: 'WAIT-LM3', type: 'Station', capacity: 1, boundary: [{ x: 12, y: 3 }, { x: 14, y: 3 }, { x: 14, y: 5 }] }
    ]
  } }
});

test('full published map is independently copied without guessing geometry or resource bindings', () => {
  const source = workspace();
  const original = source.published!.definition;
  original.edges[0]!.vendorPath = { className: 'DegenerateBezier', rawPropertiesJson: '[{"key":"direction","value":0}]' } as any;
  const copy = simulationMapFromPublishedVersion(liveMap, source);
  assert.deepEqual(copy, original);
  assert.notEqual(copy, original);
  assert.deepEqual(copy.nodes[1]?.resourceIds, ['WAIT-EXPLICIT']);
  assert.deepEqual(copy.nodes[2]?.resourceIds, []); // WAIT-LM3 name must not create a binding.
  assert.deepEqual(copy.edges[0]?.resourceIds, ['R1']); // Endpoint bindings must not spread onto roads.
  assert.deepEqual(copy.edges[0]?.controlPoints, [{ x: 2, y: 15 }, { x: 4, y: -10 }]);
  assert.equal(copy.edges[0]?.distance, 12.34);
  copy.edges[0]!.controlPoints![0]!.x = 99;
  copy.nodes[0]!.resourceIds!.push('changed');
  copy.edges[0]!.vendorPath!.className = 'changed';
  assert.equal(original.edges[0]!.controlPoints![0]!.x, 2);
  assert.deepEqual(original.nodes[0]!.resourceIds, ['R1']);
  assert.equal(original.edges[0]!.vendorPath!.className, 'DegenerateBezier');
});

test('no draft, other version or inactive project can silently replace the running map', () => {
  for (const patch of [(w: SimulationMapWorkspace) => { w.project!.isActive = false; },
    (w: SimulationMapWorkspace) => { w.project!.activeVersionId = 'other'; },
    (w: SimulationMapWorkspace) => { w.published!.versionId = 'other'; },
    (w: SimulationMapWorkspace) => { w.published!.mapId = 'other'; },
    (w: SimulationMapWorkspace) => { w.published!.status = 'Draft'; },
    (w: SimulationMapWorkspace) => { w.published = null; }]) {
    const source = workspace(); patch(source);
    assert.throws(() => simulationMapFromPublishedVersion(liveMap, source), /完整发布地图不一致/);
  }
});

test('a sampled display curve or missing bindings cannot be substituted for the full definition', () => {
  for (const patch of [(w: SimulationMapWorkspace) => { w.published!.definition.edges[0]!.controlPoints = undefined; },
    (w: SimulationMapWorkspace) => { w.published!.definition.edges[0]!.resourceIds = undefined; },
    (w: SimulationMapWorkspace) => { w.published!.definition.nodes[0]!.resourceIds = undefined; }]) {
    const source = workspace(); patch(source);
    assert.throws(() => simulationMapFromPublishedVersion(liveMap, source), /未自动推测或补写/);
  }
});
const snapshot = (patch: Partial<Snapshot> = {}): Snapshot => ({
  connected: true, source: 'backend', receivedAt: new Date(now).toISOString(), map: liveMap,
  error: null, positionedVehicleIds: [], status: null, tasks: null, devices: null, resources: null, ...patch
});
function currentMapReader(after: Snapshot = snapshot(), parameterPatch = {}) {
  const requests: string[] = []; let snapshots = 0;
  const read = async (path: string) => {
    requests.push(path);
    if (path === '/api/snapshot') return snapshots++ === 0 ? snapshot() : after;
    if (path.endsWith('/workspace')) return workspace();
    if (path === '/api/task-parameters') return { mapId: liveMap.id, mapVersionId: liveMap.version,
      configuration: { pickups: [{ pickupSite: 'BUS-LM1' }], waitingSite: 'BUS-LM2', dumpingSite: 'BUS-LM3' }, ...parameterPatch };
    throw new Error('Unexpected request ' + path);
  };
  return { read, requests };
}

test('current map loading uses full workspace and verifies the active map again after reading parameters', async () => {
  const fixture = currentMapReader();
  const result = await loadCurrentSimulationMap(fixture.read, () => now);
  assert.deepEqual(result.definition, workspace().published!.definition);
  assert.equal(result.parameters?.configuration?.waitingSite, 'BUS-LM2');
  assert.equal(result.parameterWarning, '');
  assert.deepEqual(fixture.requests, ['/api/snapshot', '/api/maps/MAP-LIVE/workspace', '/api/task-parameters', '/api/snapshot']);
});

test('a version or hash change, stale feedback or disconnect during loading rejects the copy', async () => {
  for (const after of [snapshot({ map: { ...liveMap, version: 'other' } }), snapshot({ map: { ...liveMap, definitionHash: 'other' } }),
    snapshot({ connected: false }), snapshot({ source: 'demo' }), snapshot({ receivedAt: new Date(now - 8000).toISOString() })]) {
    await assert.rejects(loadCurrentSimulationMap(currentMapReader(after).read, () => now), /未替换仿真地图/);
  }
});

test('demo snapshots cannot supply a current map and mismatched parameters are not inherited', async () => {
  await assert.rejects(loadCurrentSimulationMap(async () => snapshot({ source: 'demo' }), () => now), /没有有效观察/);
  const result = await loadCurrentSimulationMap(currentMapReader(snapshot(), { mapVersionId: 'other' }).read, () => now);
  assert.equal(result.parameters, null);
  assert.match(result.parameterWarning, /手动选择/);
});
