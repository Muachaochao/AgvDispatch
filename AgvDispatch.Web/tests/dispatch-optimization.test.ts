import test from 'node:test';
import assert from 'node:assert/strict';
import { assignmentNumber, assignmentOptionsError, assignmentMappingRowsError, changedAssignmentCount, dispatchPipelineLayers, dispatchPipelineLayerName, type DispatchAssignmentOptions, type DispatchOptimizationTask, type DispatchPipelineLayerSnapshot } from '../src/dispatch-optimization.ts';

const options: DispatchAssignmentOptions = {
  mode: 'Shadow', intervalMs: 3000, windowSize: 80, candidatesPerVehicle: 15, minimumCandidatesPerTask: 2,
  waitPriorityPerMinute: 1, maxWaitPriority: 30, deadlineHorizonMinutes: 15, maxDeadlinePriority: 30,
  minimumBatteryPercent: 20, nominalSpeedMetersPerSecond: 1, standbyBonusSeconds: 1, batteryRiskSeconds: 10,
  crossZoneSeconds: 10, returnCostWeight: 0.2, siteZones: {}, stationDeadlineMinutes: {}
};

test('saved options validate numeric inputs before sending, including empty, nonfinite and fractional integer fields', () => {
  assert.equal(assignmentOptionsError(options), '');
  for (const patch of [{ intervalMs: 1000 }, { windowSize: 3.5 }, { nominalSpeedMetersPerSecond: 0 },
    { minimumBatteryPercent: Number.NaN }, { waitPriorityPerMinute: Infinity }, { standbyBonusSeconds: 1.01 },
    { maxWaitPriority: '' }])
    assert.notEqual(assignmentOptionsError({ ...options, ...patch } as DispatchAssignmentOptions), '');
  assert.equal(assignmentOptionsError({ ...options, windowSize: 300, candidatesPerVehicle: 20 }), '');
});

test('zone and deadline rows cannot silently overwrite repeated station identifiers', () => {
  assert.equal(assignmentMappingRowsError([{ siteId: 'LM28', zone: 'A区' }], [{ stationId: 'CNC-01', minutes: 30 }]), '');
  assert.notEqual(assignmentMappingRowsError([{ siteId: 'LM28', zone: 'A区' }, { siteId: ' lm28 ', zone: 'B区' }], []), '');
  assert.notEqual(assignmentMappingRowsError([], [{ stationId: 'CNC-01', minutes: 1 }, { stationId: 'cnc-01', minutes: 2 }]), '');
  assert.notEqual(assignmentMappingRowsError([{ siteId: '', zone: '' }], []), '');
  assert.notEqual(assignmentOptionsError({ ...options, stationDeadlineMinutes: { 'CNC-01': 0 } }), '');
  assert.notEqual(assignmentOptionsError({ ...options, siteZones: { 'LM28': '' } }), '');
});

test('comparison includes lost or newly possible assignments and treats absent vehicles equally', () => {
  const tasks = [
    { recommendedVehicleId: 'AGV-01', greedyVehicleId: 'AGV-03' },
    { recommendedVehicleId: 'AGV-03', greedyVehicleId: null },
    { recommendedVehicleId: null, greedyVehicleId: 'AGV-02' },
    { recommendedVehicleId: 'AGV-04', greedyVehicleId: 'AGV-04' },
    { recommendedVehicleId: null, greedyVehicleId: '' },
  ] as DispatchOptimizationTask[];
  assert.equal(changedAssignmentCount(tasks), 3);
});

test('unavailable cost values are never displayed as zero estimates', () => {
  assert.equal(assignmentNumber(null), '—');
  assert.equal(assignmentNumber(Number.NaN), '—');
  assert.equal(assignmentNumber(undefined), '—');
  assert.equal(assignmentNumber(0), '0.0');
});

test('six layers remain unobserved until backend evidence arrives and missing layers are never shown ready', () => {
  const layers = dispatchPipelineLayers(undefined, true);
  assert.deepEqual(layers.map(layer => layer.key), ['queue', 'candidates', 'matching', 'traffic', 'safety', 'execution']);
  assert.ok(layers.every(layer => layer.tone === 'unknown' && layer.label === '未取得状态' && layer.itemCount === null));
  assert.equal(dispatchPipelineLayerName('traffic'), '第 4 层 · 交通路权');
  assert.equal(dispatchPipelineLayerName('unrecognized'), '');
});

test('backend ready, disabled and blocked observations retain distinct meanings', () => {
  const now = Date.now();
  const evidence = (key: string, state: string): DispatchPipelineLayerSnapshot => ({
    key, number: 1, name: key, state, summary: '实际后台观察', itemCount: 3, updatedAt: new Date(now).toISOString()
  });
  const layers = dispatchPipelineLayers([evidence('queue', 'Ready'), evidence('matching', 'Disabled'), evidence('safety', 'Blocked')], true, now);
  assert.equal(layers[0]?.tone, 'ready'); assert.equal(layers[0]?.itemCount, 3);
  assert.equal(layers[2]?.tone, 'disabled'); assert.equal(layers[4]?.tone, 'blocked');
  assert.equal(layers[5]?.tone, 'unknown');
});

test('expired, disconnected, future, unknown or duplicate observations cannot claim current readiness', () => {
  const now = Date.now();
  const fresh: DispatchPipelineLayerSnapshot = { key: 'queue', number: 1, name: 'queue', state: 'Ready', summary: 'queue ready', itemCount: 2, updatedAt: new Date(now).toISOString() };
  assert.equal(dispatchPipelineLayers([fresh], false, now)[0]?.tone, 'stale');
  assert.equal(dispatchPipelineLayers([{ ...fresh, updatedAt: new Date(now - 15001).toISOString() }], true, now)[0]?.tone, 'stale');
  assert.equal(dispatchPipelineLayers([{ ...fresh, updatedAt: new Date(now + 2000).toISOString() }], true, now)[0]?.tone, 'unknown');
  assert.equal(dispatchPipelineLayers([{ ...fresh, state: 'UnrecognizedState' }], true, now)[0]?.tone, 'unknown');
  const unobserved = dispatchPipelineLayers([{ ...fresh, state: 'Idle', updatedAt: '0001-01-01T00:00:00+00:00' }], true, now)[0];
  assert.equal(unobserved?.label, '尚未观察'); assert.equal(unobserved?.updatedAt, null);
  assert.equal(dispatchPipelineLayers([fresh, fresh], true, now)[0]?.tone, 'unknown');
});
