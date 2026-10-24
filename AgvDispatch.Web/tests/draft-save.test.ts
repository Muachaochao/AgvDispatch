import test from 'node:test';
import assert from 'node:assert/strict';
import { checkDraftDestination, draftSaveBlocker } from '../src/draft-save.ts';

test('导入后未关联时给出保留编辑稿的关联步骤，关联后允许保存', () => {
  assert.match(draftSaveBlocker(false, true, false, []), /关联并保留编辑稿/);
  assert.equal(draftSaveBlocker(false, true, true, []), '');
  assert.match(draftSaveBlocker(false, false, true, []), /后台未连接/);
  assert.match(draftSaveBlocker(false, true, true, ['道路引用缺失']), /道路引用缺失/);
  assert.match(draftSaveBlocker(true, true, true, []), /正在处理/);
});

test('关联必须保留目标版本和并发令牌，错误目标不能绑定', () => {
  const destination = { project: { mapId: 'M' }, draft: { versionId: 'V', editToken: 'revision' } };
  const original = structuredClone(destination);
  checkDraftDestination(destination, 'M');
  assert.deepEqual(destination, original);
  for (const invalid of [null, {}, { ...destination, draft: { versionId: 'V' } }, { ...destination, project: { mapId: 'OTHER' } }])
    assert.throws(() => checkDraftDestination(invalid, 'M'));
  assert.throws(() => checkDraftDestination(destination, ''));
});
