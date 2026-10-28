import test from 'node:test';
import assert from 'node:assert/strict';
import { fleetSelectionStatus, fleetStartRetirementConfirmed, moveFleetStation, reconcileFleetSelection, selectVisibleFleetOptions, retireUnacceptedFleetStart } from '../src/fleet-test-options.ts';

const options = {
  allowedVehicles: Array.from({ length: 7 }, (_, i) => `CARRIER-${i + 1}`),
  configuredStations: Array.from({ length: 320 }, (_, i) => `PRESS-${i + 1}`),
  maxConcurrentTasks: 3
};

test('fleet size, station count and concurrency come from configuration independently', () => {
  assert.equal(fleetSelectionStatus(options, options.allowedVehicles, options.configuredStations).valid, true);
  assert.equal(fleetSelectionStatus(options, ['CARRIER-2', 'CARRIER-7'], ['PRESS-300', 'PRESS-15']).valid, true);
  assert.equal(fleetSelectionStatus({ ...options, maxConcurrentTasks: 1 }, options.allowedVehicles, options.configuredStations).valid, true);
  assert.equal(fleetSelectionStatus({ ...options, maxConcurrentTasks: 0 }, options.allowedVehicles, options.configuredStations).valid, false);
  assert.equal(fleetSelectionStatus(null, [], []).valid, false);
});

test('reject missing, duplicate and insufficient members before creating a request', () => {
  for (const vehicles of [[], ['CARRIER-1'], ['CARRIER-1', 'carrier-1'], ['CARRIER-1', 'UNKNOWN']])
    assert.equal(fleetSelectionStatus(options, vehicles, ['PRESS-1', 'PRESS-2']).valid, false);
  for (const stations of [[], ['PRESS-1'], ['PRESS-1', 'press-1'], ['PRESS-1', 'UNKNOWN']])
    assert.equal(fleetSelectionStatus(options, ['CARRIER-1', 'CARRIER-2'], stations).valid, false);
});

test('polling initializes once and preserves user-cleared selections', () => {
  assert.deepEqual(reconcileFleetSelection([], options.allowedVehicles, false).selected, options.allowedVehicles);
  assert.deepEqual(reconcileFleetSelection([], options.allowedVehicles, true).selected, []);
  assert.deepEqual(reconcileFleetSelection([], [], false).selected, []);
});

test('configuration changes remove invalid editable choices but preserve frozen request identity', () => {
  const original = ['CARRIER-7', 'CARRIER-2'];
  assert.deepEqual(reconcileFleetSelection(original, ['CARRIER-2', 'CARRIER-8'], true), { selected: ['CARRIER-2'], missing: ['CARRIER-7'] });
  assert.deepEqual(reconcileFleetSelection(original, ['CARRIER-2'], true, true), { selected: original, missing: ['CARRIER-7'] });
  assert.deepEqual(original, ['CARRIER-7', 'CARRIER-2']);
});

test('filtered select and clear preserve choices outside the filter and order', () => {
  assert.deepEqual(selectVisibleFleetOptions(['PRESS-7', 'PRESS-2'], ['PRESS-2', 'PRESS-3'], true), ['PRESS-7', 'PRESS-2', 'PRESS-3']);
  assert.deepEqual(selectVisibleFleetOptions(['PRESS-7', 'PRESS-2', 'PRESS-3'], ['PRESS-2', 'PRESS-3'], false), ['PRESS-7']);
  assert.deepEqual(selectVisibleFleetOptions(['PRESS-7'], ['press-7'], true), ['PRESS-7']);
});

test('station sequence is explicit and bounds do not mutate the original plan', () => {
  const original = ['WEST', 'NORTH', 'SOUTH'];
  assert.deepEqual(moveFleetStation(original, 2, -1), ['WEST', 'SOUTH', 'NORTH']);
  assert.deepEqual(moveFleetStation(original, 0, -1), original);
  assert.deepEqual(original, ['WEST', 'NORTH', 'SOUTH']);
});

test('unknown or accepted original requests cannot be cleared as unaccepted', () => {
  for (const response of [null, undefined, {}, { current: null }, { requestId: 'original', retired: false }, { requestId: 'another', retired: true }])
    assert.equal(fleetStartRetirementConfirmed('original', response), false);
  assert.equal(fleetStartRetirementConfirmed('original', { requestId: 'original', retired: true }), true);
});

test('retirement failure retains the frozen pending plan for the next inquiry', async () => {
  const pending = { requestId: 'original', vehicleIds: ['CARRIER-1', 'CARRIER-2'], stationIds: ['NORTH', 'SOUTH'] };
  await assert.rejects(retireUnacceptedFleetStart(pending, async () => { throw new Error('response lost'); }), /response lost/);
  await assert.rejects(retireUnacceptedFleetStart(pending, async () => ({ requestId: pending.requestId, retired: false })), /保留原参数/);
  await retireUnacceptedFleetStart(pending, async id => ({ requestId: id, retired: true }));
  assert.deepEqual(pending.stationIds, ['NORTH', 'SOUTH']);
});
