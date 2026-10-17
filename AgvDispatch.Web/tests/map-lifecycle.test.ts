import test from 'node:test';
import assert from 'node:assert/strict';
import { isMapSwitchPhase } from '../src/map-lifecycle.ts';

test('地图切换识别WebHost字符串阶段及旧数字阶段', () => {
  assert.equal(isMapSwitchPhase('Draining', 10), true);
  assert.equal(isMapSwitchPhase(10, 10), true);
  assert.equal(isMapSwitchPhase('AwaitingResume', 20), true);
  assert.equal(isMapSwitchPhase(20, 20), true);
  assert.equal(isMapSwitchPhase('Completed', 90), true);
  assert.equal(isMapSwitchPhase('Cancelled', 100), true);
});
test('未知、缺失和其他切换阶段不能开放错误操作', () => {
  for (const value of [null, undefined, '10', 'unknown', 'Completed', 90])
    assert.equal(isMapSwitchPhase(value, 10), false);
  assert.equal(isMapSwitchPhase('Draining', 20), false);
  assert.equal(isMapSwitchPhase('AwaitingResume', 90), false);
});
