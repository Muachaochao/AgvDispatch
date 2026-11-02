import test from 'node:test';
import assert from 'node:assert/strict';
import { assignmentNumber, assignmentOptionsError, assignmentMappingRowsError, changedAssignmentCount, type DispatchAssignmentOptions, type DispatchOptimizationTask } from '../src/dispatch-optimization.ts';

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
