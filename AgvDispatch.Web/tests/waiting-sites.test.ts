import test from 'node:test';
import assert from 'node:assert/strict';
import { effectiveWaitingSites, withWaitingSites } from '../src/waiting-sites.ts';

test('old single-bay profiles remain readable and round-trip without losing other settings', () => {
  const original = { waitingSite: 'LM5', dumpingSite: 'LM6', pickups: [{ stationId: 'CNC-001', allowedVehicleIds: ['AGV-01'] }], actions: [{ operation: 'JackLoad' }] };
  assert.deepEqual(effectiveWaitingSites(original), ['LM5']);
  const saved = withWaitingSites(original, effectiveWaitingSites(original).join(', '));
  assert.deepEqual(saved.waitingSites, ['LM5']);
  assert.deepEqual(saved.pickups, original.pickups);
  assert.deepEqual(saved.actions, original.actions);
  assert.equal('waitingSites' in original, false);
});

test('multi-bay profiles display and save all configured bays with a compatible first default', () => {
  const profile = { waitingSite: 'LM35', waitingSites: ['LM35', 'LM36'], dumpingSite: 'LM39', revision: 'existing' };
  assert.deepEqual(effectiveWaitingSites(profile), ['LM35', 'LM36']);
  const saved = withWaitingSites(profile, ' LM36， LM35 ');
  assert.equal(saved.waitingSite, 'LM36');
  assert.deepEqual(saved.waitingSites, ['LM36', 'LM35']);
  assert.equal(saved.revision, 'existing');
  assert.deepEqual(profile.waitingSites, ['LM35', 'LM36']);
});

test('invalid bay selections are rejected before submitting configuration', () => {
  const profile = { waitingSite: 'LM35', dumpingSite: 'LM39' };
  for (const text of ['', 'LM35, LM35', 'LM35, lm35', 'LM35; LM39', 'lm39', Array.from({ length: 1001 }, (_, i) => `W${i}`).join(',')]) {
    assert.throws(() => withWaitingSites(profile, text));
  }
});

test('empty arrays in historic responses fall back to the single site', () => {
  assert.deepEqual(effectiveWaitingSites({ waitingSite: 'LM5', waitingSites: [] }), ['LM5']);
  assert.deepEqual(effectiveWaitingSites(null), []);
});
