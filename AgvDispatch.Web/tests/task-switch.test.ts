import { test } from 'node:test';
import assert from 'node:assert/strict';
import { observeTaskSwitchIntent, parseTaskSwitchIntent, retainTaskSwitchIntent, taskSwitchIntentError, taskSwitchProgress,
  taskSwitchStageName, taskSwitchStorageKey, taskSwitchTerminal, type TaskSwitchIntent, type TaskSwitchRecord } from '../src/task-switch.ts';

const original: TaskSwitchIntent = { requestId: '1234567890abcdef1234567890abcdef', taskId: 'TASK-configurable',
  expectedStage: 'GoingToPickup', targetStationId: 'custom-station', priority: 73,
  reason: '现场空车状态及原工位箱体位置已经确认', siteConfirmed: true, mode: 'Auto' };
const record: TaskSwitchRecord = { switchId: original.requestId, sourceTaskId: original.taskId, vehicleId: 'custom-vehicle',
  targetStationId: original.targetStationId, stage: 'AwaitingStop', message: '查询原任务', blockers: [], revision: 1,
  updatedAt: '2026-11-02T00:00:00Z', reason: original.reason, priority: original.priority, mapId: 'map', mapVersionId: 'version',
  parameterRevision: 'revision', replacementRequestId: 'abcdef1234567890abcdef1234567890', cancelRequestId: '0123456789abcdef0123456789abcdef',
  resolvedMode: 'CancelEmptyNavigation' };

test('换单申请校验现场确认、工位、原因及优先级', () => {
  assert.equal(taskSwitchIntentError(original), null);
  for (const patch of [{ siteConfirmed: false }, { priority: -1 }, { priority: 101 }, { priority: 1.2 },
    { priority: Number.NaN }, { reason: '太短' }, { reason: ' '.repeat(12) }, { reason: '字'.repeat(1001) },
    { targetStationId: '' }, { requestId: 'bad-id' }, { mode: 'Unsafe' as TaskSwitchIntent['mode'] }]) assert.ok(taskSwitchIntentError({ ...original, ...patch }));
});

test('结果未知时保持原编号和所有参数，显式重试不生成新申请', () => {
  const saved = retainTaskSwitchIntent(null, { ...original, reason: `  ${original.reason}  ` });
  assert.equal(saved.reason, original.reason);
  assert.equal(retainTaskSwitchIntent(saved, { ...saved }), saved);
  for (const patch of [{ requestId: 'abcdefabcdefabcdefabcdefabcdefab' }, { expectedStage: 'PickingUp' },
    { targetStationId: 'another-station' }, { priority: 74 }, { reason: original.reason + '另外原因' }, { taskId: 'another-task' },
    { mode: 'CompleteBusinessReturn' as const }])
    assert.throws(() => retainTaskSwitchIntent(saved, { ...saved, ...patch }), /原换单申请/);
});

test('刷新后恢复完整申请，损坏或属于其他任务的记录不能用于发送', () => {
  assert.deepEqual(parseTaskSwitchIntent(JSON.stringify(original), original.taskId), original);
  assert.equal(parseTaskSwitchIntent(null, original.taskId), null);
  for (const raw of ['{', 'null', '[]', '{}', JSON.stringify({ ...original, siteConfirmed: 'true' }),
    JSON.stringify({ ...original, priority: '73' }), JSON.stringify({ ...original, taskId: 'another-task' })])
    assert.throws(() => parseTaskSwitchIntent(raw, original.taskId));
  assert.notEqual(taskSwitchStorageKey('task-a'), taskSwitchStorageKey('task-b'));
});

test('查询不到原记录不解除不确定申请，同编号不同参数仍保留核对', () => {
  assert.equal(observeTaskSwitchIntent(original, null), 'Missing');
  assert.equal(observeTaskSwitchIntent(original, record), 'Accepted');
  for (const patch of [{ switchId: 'other-id' }, { sourceTaskId: 'other-task' }, { targetStationId: 'other-station' },
    { reason: '另一条现场申请依据已经确认' }, { priority: 74 }])
    assert.equal(observeTaskSwitchIntent(original, { ...record, ...patch }), 'Mismatch');
});

test('后台使用嵌套原申请时严格核对完整参数，不丢失续接身份', () => {
  const nested = { ...record, reason: undefined, priority: undefined, request: { ...original } };
  assert.equal(observeTaskSwitchIntent(original, nested), 'Accepted');
  for (const patch of [{ requestId: 'other-id' }, { taskId: 'other-task' }, { expectedStage: 'PickingUp' },
    { targetStationId: 'other-station' }, { priority: 10 }, { reason: original.reason + '不同' }, { siteConfirmed: false }])
    assert.equal(observeTaskSwitchIntent(original, { ...nested, request: { ...original, ...patch } }), 'Mismatch');
});

test('取消受理、停稳待查及待核对均不能显示为切换完成', () => {
  for (const stage of ['Requested', 'AwaitingStop', 'AwaitingBusinessCleanup', 'ClosingOriginal', 'ReadyToSwitch', 'SubmittingReplacement', 'ReviewHold', 'Unknown'])
    assert.equal(taskSwitchTerminal(stage), false);
  assert.equal(taskSwitchTerminal('Completed'), true);
  assert.equal(taskSwitchTerminal('Abandoned'), true);
  assert.equal(taskSwitchProgress('AwaitingStop'), 1);
  assert.equal(taskSwitchProgress('AwaitingBusinessCleanup'), 1);
  assert.equal(taskSwitchProgress('ReviewHold'), -1);
  assert.equal(taskSwitchProgress('arbitrary'), -1);
  assert.match(taskSwitchStageName('arbitrary'), /未知阶段/);
  assert.equal(taskSwitchStageName('ReviewHold'), '暂停待核对');
  assert.equal(taskSwitchStageName('AwaitingBusinessCleanup'), '完成原业务返箱收尾');
});
