import { test } from 'node:test';
import assert from 'node:assert/strict';
import { vehicleCategory, alarmStatus, filterAlarms } from '../src/dashboard.ts';
import { demoSnapshot } from '../src/demo.ts';

test('车辆状态有明确证据，未知和过期不显示为空闲', () => {
  const v = demoSnapshot().status!.agvs[0];
  assert.equal(vehicleCategory({ ...v, navigationState: null }, [], true), 'unknown');
  assert.equal(vehicleCategory({ ...v, navigationState: 'Idle' }, [], true), 'idle');
  assert.equal(vehicleCategory({ ...v, navigationState: 'Idle' }, [], false), 'offline');
  assert.equal(vehicleCategory({ ...v, batteryPercent: 99, navigationState: null }, [], true), 'unknown');
  assert.equal(vehicleCategory({ ...v, charging: true }, [], true), 'charging');
  assert.equal(vehicleCategory({ ...v, emergencyStopped: true, charging: true }, [], true), 'fault');
});
test('告警确认与现场恢复分别展示，筛选保留对应状态', () => {
  const alarm = demoSnapshot().alarms![0];
  const rows = [alarm, { ...alarm, alarmId: 'ACK', status: 'Acknowledged' }, { ...alarm, alarmId: 'REC', status: 'Recovered' }];
  assert.equal(alarmStatus('Acknowledged'), '已确认');
  assert.equal(alarmStatus('Recovered'), '已恢复');
  assert.deepEqual(filterAlarms(rows, 'ACK', 'Acknowledged', 'Info').map(a => a.alarmId), ['ACK']);
  assert.equal(filterAlarms(rows, '', 'Open', 'all').length, 1);
});
