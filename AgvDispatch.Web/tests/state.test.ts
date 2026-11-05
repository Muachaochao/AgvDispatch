import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isFresh, isRunningStage, label } from '../src/state.ts';

describe('isFresh', () => {
  it('returns true when snapshot is connected and recent', () => {
    const now = Date.now();
    const snapshot = { connected: true, receivedAt: new Date(now - 1000).toISOString() } as any;
    assert.equal(isFresh(snapshot, now), true);
  });

  it('returns false when snapshot is null', () => {
    assert.equal(isFresh(null), false);
  });

  it('returns false when disconnected', () => {
    const snapshot = { connected: false, receivedAt: new Date().toISOString() } as any;
    assert.equal(isFresh(snapshot), false);
  });

  it('returns false when data is stale (>7s)', () => {
    const now = Date.now();
    const snapshot = { connected: true, receivedAt: new Date(now - 10000).toISOString() } as any;
    assert.equal(isFresh(snapshot, now), false);
  });
});

describe('isRunningStage', () => {
  it('identifies executing stages', () => {
    assert.equal(isRunningStage('Executing'), true);
    assert.equal(isRunningStage('GoingToPickup'), true);
    assert.equal(isRunningStage('Dumping'), true);
    assert.equal(isRunningStage('SubmittingToAgv'), true);
  });

  it('rejects non-running stages', () => {
    assert.equal(isRunningStage('Completed'), false);
    assert.equal(isRunningStage('Created'), false);
    assert.equal(isRunningStage('Cancelled'), false);
    assert.equal(isRunningStage('Faulted'), false);
  });
});

describe('label', () => {
  it('translates known stages', () => {
    assert.equal(label('Created'), '已创建');
    assert.equal(label('Completed'), '已完成');
    assert.equal(label('GoToPickup'), '前往取货');
  });

  it('hides unknown technical codes behind an operator-safe fallback', () => {
    assert.equal(label('SomeUnknown'), '状态待核对');
  });

  it('returns dash for null/undefined', () => {
    assert.equal(label(null), '—');
    assert.equal(label(undefined), '—');
  });
});
