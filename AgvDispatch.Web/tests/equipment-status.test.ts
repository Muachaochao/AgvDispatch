import test from 'node:test';
import assert from 'node:assert/strict';
import { chargerMonitoringState, equipmentTone } from '../src/equipment-status.ts';
import type { ChargingStatus } from '../src/equipment-status.ts';

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
