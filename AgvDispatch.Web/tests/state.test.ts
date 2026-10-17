import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isFresh, isRunningStage, mayPlot, label } from '../src/state.ts';
import { demoSnapshot } from '../src/demo.ts';

test('仅有效、当前且坐标绑定明确的车位进入地图', () => {
  const snapshot = demoSnapshot(), v = snapshot.status!.agvs[0];
  assert.equal(mayPlot(snapshot, v), true);
  assert.equal(mayPlot(snapshot, { ...v, quality: 'Bad' }), false);
  assert.equal(mayPlot(snapshot, { ...v, x: Number.NaN }), false);
  assert.equal(mayPlot(snapshot, { ...v, lastSuccessfulPollAt: new Date(Date.now() - 6000).toISOString() }), false);
  assert.equal(mayPlot({ ...snapshot, positionedVehicleIds: [] }, v), false);
  assert.equal(mayPlot({ ...snapshot, connected: false }, v), false);
  assert.equal(mayPlot({ ...snapshot, map: { ...snapshot.map!, coordinateSystem: 'SmapWorldTranslated' } }, v), false);
});
test('推送停止后不能继续展示当前有效状态，异常时间拒绝', () => {
  const snapshot = demoSnapshot();
  assert.equal(isFresh(snapshot), true);
  assert.equal(isFresh(snapshot, Date.now() + 8000), false);
  assert.equal(isFresh({ ...snapshot, receivedAt: 'not-a-date' }), false);
  assert.equal(isFresh({ ...snapshot, receivedAt: new Date(Date.now() + 5000).toISOString() }), false);
});
test('示例始终明确来源，九步后才标记示例完成', () => {
  for (let i = 0; i < 9; i++) { const snapshot = demoSnapshot(i); assert.equal(snapshot.source, 'demo'); assert.equal(snapshot.tasks!.items[0].stage, 'Executing'); }
  assert.equal(demoSnapshot(9).tasks!.items[0].stage, 'Completed');
  assert.equal(demoSnapshot(9).status!.safety.agvCommandsAllowed, false);
});
test('执行筛选覆盖真实后台运输和PLC等待阶段', () => {
  for (const stage of ['GoingToPickup', 'PickingUp', 'GoingToDumper', 'WaitingForDumper', 'Dumping', 'ReturningEmptyBox', 'WaitingForPlcConfirmation']) {
    assert.equal(isRunningStage(stage), true); assert.notEqual(label(stage), stage);
  }
  assert.equal(isRunningStage('Completed'), false); assert.equal(isRunningStage('Faulted'), false);
});
