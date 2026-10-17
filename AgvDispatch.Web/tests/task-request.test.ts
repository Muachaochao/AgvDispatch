import { test } from 'node:test';
import assert from 'node:assert/strict';
import { newTaskRequestId } from '../src/task-request.ts';
test('任务申请编号符合后台Guid N格式且多次申请不共用编号', () => {
  const ids=Array.from({length:100},newTaskRequestId);
  assert.ok(ids.every(id=>/^[0-9a-f]{32}$/.test(id)));
  assert.equal(new Set(ids).size,100);
});
