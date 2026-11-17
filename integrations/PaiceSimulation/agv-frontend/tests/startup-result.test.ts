import test from 'node:test';
import assert from 'node:assert/strict';
import { startupAccepted } from '../src/integration/startup-result.ts';

test('only a new instance with matching submitted startup is accepted', () => {
  assert.equal(startupAccepted({ rootName: 'new', phase: 'starting', startupPlan: { schemeId: 's', status: 'starting' } }, 'old', 's'), true);
  assert.equal(startupAccepted({ rootName: 'new', phase: 'running', startupPlan: { schemeId: 's', status: 'submitted' } }, 'old', 's'), true);
  for (const value of [
    { rootName: 'old', phase: 'running', startupPlan: { schemeId: 's', status: 'submitted' } },
    { rootName: 'new', phase: 'failed', startupPlan: { schemeId: 's', status: 'failed' } },
    { rootName: 'new', phase: 'running', startupPlan: { schemeId: 'other', status: 'submitted' } },
  ]) assert.equal(startupAccepted(value, 'old', 's'), false);
});
