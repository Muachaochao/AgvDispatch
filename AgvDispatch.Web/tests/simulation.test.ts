import test from 'node:test';
import assert from 'node:assert/strict';
import { simulationFresh, scenarioActive, simulationGeometry, simulationTone, simulationLabel, type SimulationStatus } from '../src/simulation.ts';

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
