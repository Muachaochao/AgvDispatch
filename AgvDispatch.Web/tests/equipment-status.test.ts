import test from 'node:test';
import assert from 'node:assert/strict';
import { chargerMonitoringState, equipmentTone, stationSignalSummary, executionModeState } from '../src/equipment-status.ts';
import type { ChargingStatus } from '../src/equipment-status.ts';
import type { Device } from '../src/types.ts';

test('运行模式区分查看、全部开启、部分开启和过期；不把派发标志当作全部就绪', () => {
  const off = { dispatchingEnabled: false, plcWritesAllowed: false, agvCommandsAllowed: false };
  assert.equal(executionModeState(off, true).label, '查看模式');
  assert.equal(executionModeState({ ...off, dispatchingEnabled: true }, true).label, '部分执行开启');
  assert.equal(executionModeState({ dispatchingEnabled: true, plcWritesAllowed: true, agvCommandsAllowed: true }, true).label, '执行已启用');
  assert.equal(executionModeState(off, false).label, '状态未取得');
  assert.equal(executionModeState(null, true).tone, 'offline');
});

test('工位摘要保留故障和过期状态，空闲未呼叫不等于信号故障', () => {
  const device = (quality: string, status: string): Device => ({ id: 'station', name: '工位', quality, status, summary: '', fields: [] });
  const idle = device('正常', '业务状态未就绪');
  assert.deepEqual(stationSignalSummary([idle], true), { tone: 'green', label: '1/1 正常', total: 1 });
  assert.equal(stationSignalSummary([idle, device('正常', '故障')], true).tone, 'red');
  assert.equal(stationSignalSummary([idle, device('数据过期', '数据过期')], true).label, '1/2 正常');
  assert.equal(stationSignalSummary([idle], false).tone, 'offline');
  assert.equal(stationSignalSummary([], true).label, '尚未配置');
});

const status: ChargingStatus = {
  enabled: false, capturedAt: '2026-11-01T10:00:00Z',
  options: { startBatteryPercent: 25, criticalBatteryPercent: 15, targetBatteryPercent: 80, sites: [] },
  sessions: []
};

test('后台正常的中文PLC质量和连接诊断均显示绿灯', () => {
  assert.equal(equipmentTone({ quality: '正常', status: '空闲' }, true), 'green');
  assert.equal(equipmentTone({ quality: '连接诊断', status: '正常' }, true), 'green');
  assert.equal(equipmentTone({ quality: 'Good', status: '空闲' }, true), 'green');
});

test('新鲜信号中的故障不被正常采集质量掩盖', () => {
  for (const state of ['故障', '信号冲突', '中断', '未连接'])
    assert.equal(equipmentTone({ quality: '正常', status: state }, true), 'red');
});

test('停用、过期和首次未采集都不会显示正常在线', () => {
  assert.equal(equipmentTone({ quality: '未启用', status: '通信未启用' }, true), 'offline');
  assert.equal(equipmentTone({ quality: '连接诊断', status: '数据过期' }, true), 'amber');
  assert.equal(equipmentTone({ quality: '等待首次采集', status: '信号未就绪' }, true), 'amber');
  assert.equal(equipmentTone({ quality: '正常', status: '空闲' }, false), 'offline');
});

test('充电未配置时明示桩监测尚未接入，故障与工作状态均未知', () => {
  const view = chargerMonitoringState(status);
  assert.equal(view.label, '未接入实时监测');
  assert.equal(view.tone, 'offline');
  assert.equal(view.communication, '未接入');
  assert.equal(view.operation, '未知');
  assert.equal(view.fault, '未知');
  assert.equal(view.lastFeedback, '尚未取得');
  assert.equal(view.policy, '未启用');
});

test('启用策略、核定充电位和车辆充电采样都不能证明桩端当前在线', () => {
  const configured: ChargingStatus = {
    ...status, enabled: true,
    options: { ...status.options, sites: [{ id: 'CHARGE-A', chargingSiteId: 'LM-A', exitSiteId: 'LM-B', resourceId: 'BAY-A', commissioned: true, allowedVehicleIds: ['AGV-03'] }] },
    sessions: [{ sessionId: 'old', vehicleId: 'AGV-03', phase: 'Charging', message: '历史会话', updatedAt: status.capturedAt,
      battery: { charging: true, current: 20 } }]
  };
  const view = chargerMonitoringState(configured);
  assert.equal(view.policy, '已启用');
  assert.equal(view.label, '未接入实时监测');
  assert.equal(view.tone, 'offline');
  assert.equal(view.operation, '未知');
  assert.equal(view.lastFeedback, '尚未取得');
});

test('后台读取失败不沿用旧的已启用策略作为当前状态', () => {
  const view = chargerMonitoringState({ ...status, enabled: true }, 'connection lost');
  assert.equal(view.policy, '读取失败');
  assert.equal(view.label, '未接入实时监测');
  assert.equal(view.tone, 'offline');
  assert.equal(chargerMonitoringState().policy, '未取得');
});
