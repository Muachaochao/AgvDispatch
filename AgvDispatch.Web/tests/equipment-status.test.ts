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

test('后台读取失败不沿用旧的已启用策略或桩状态作为当前状态', () => {
  const view = chargerMonitoringState({ ...status, enabled: true }, 'connection lost');
  assert.equal(view.policy, '读取失败');
  assert.equal(view.label, '状态读取失败');
  assert.equal(view.tone, 'offline');
  assert.equal(chargerMonitoringState().policy, '未取得');
});

const now = Date.parse('2026-11-02T11:00:00Z');
const stamp = (offset = 0) => new Date(now + offset).toISOString();
function liveCharging(): ChargingStatus {
  const site = { id: 'CHARGE-A', chargingSiteId: 'LM52', exitSiteId: 'LM51', resourceId: 'BAY-A',
    commissioned: true, allowedVehicleIds: ['AGV-01'], controlMode: 'RobokitScript' };
  return {
    ...status, enabled: true, capturedAt: stamp(), options: { ...status.options, stateMaxAgeSeconds: 5, sites: [site] },
    sessions: [{ sessionId: 'session-A', vehicleId: 'AGV-01', phase: 'SessionRunning', site,
      message: '充电会话运行中', updatedAt: stamp(), scriptOperationId: 'operation-A',
      workflowStatus: { operationId: 'operation-A', kind: 'Charge', state: 'Running', updatedAt: stamp(), message: '正电流观察',
        continuousPositiveSeconds: 30, shutdownVerified: false,
        battery: { current: 19, charging: true, observedAt: stamp() },
        station: { chargerIp: '192.0.2.11', observedAt: stamp(), isAuto: true, charging: null, current: 19, voltage: 54, alarms: [] }
      } }]
  };
}

test('同一原操作的新鲜桩端回包证明通信，自动模式和输出电流不代替桩充电判据', () => {
  const view = chargerMonitoringState(liveCharging(), '', now);
  assert.equal(view.label, '反馈在线');
  assert.equal(view.tone, 'green');
  assert.equal(view.operation, '桩端充电状态待核定');
  assert.equal(view.vehicleCharge, '车端状态及正电流已确认');
  assert.equal(view.fault, '本次反馈无报警');
  assert.equal(view.output, '54.00 V / 19.00 A');
});

test('原操作不匹配或历史已完成会话不提升为桩端实时在线', () => {
  for (const change of ['mismatch', 'completed']) {
    const data = liveCharging();
    if (change === 'mismatch') data.sessions[0].scriptOperationId = 'different-operation';
    else data.sessions[0].phase = 'Completed';
    const view = chargerMonitoringState(data, '', now);
    assert.equal(view.label, '等待桩端反馈');
    assert.equal(view.operation, '未知');
    assert.equal(view.fault, '未知');
  }
});

test('后台快照、worker或桩端任一采样过期或来自未来，当前状态均未知', () => {
  for (const source of ['backend', 'worker', 'station', 'future']) {
    const data = liveCharging();
    if (source === 'backend') data.capturedAt = stamp(-5001);
    if (source === 'worker') data.sessions[0].workflowStatus!.updatedAt = stamp(-5001);
    if (source === 'station') data.sessions[0].workflowStatus!.station!.observedAt = stamp(-5001);
    if (source === 'future') data.sessions[0].workflowStatus!.station!.observedAt = stamp(1001);
    const view = chargerMonitoringState(data, '', now);
    assert.equal(view.label, '桩端反馈过期', source);
    assert.equal(view.tone, 'offline', source);
    assert.equal(view.operation, '未知', source);
    assert.equal(view.fault, '未知', source);
  }
});

test('充电连接失败、新鲜警报与正电流证据分别显示', () => {
  const data = liveCharging();
  data.sessions[0].phase = 'Hold';
  const workflow = data.sessions[0].workflowStatus!;
  workflow.state = 'Failed'; workflow.station!.alarms = ['5201'];
  workflow.battery!.current = 0;
  const view = chargerMonitoringState(data, '', now);
  assert.equal(view.label, '桩端报警');
  assert.equal(view.tone, 'red');
  assert.equal(view.fault, '5201');
  assert.match(view.vehicleCharge, /尚未共同确认/);
  workflow.station!.alarms = [];
  assert.equal(chargerMonitoringState(data, '', now).label, '反馈在线 · 待核对');
});

test('其他桩较新的正常反馈不掩盖仍新鲜的报警，过期车端采样不证明充电', () => {
  const data = liveCharging();
  const alarming = structuredClone(data.sessions[0]);
  alarming.sessionId = 'session-B'; alarming.vehicleId = 'AGV-02';
  alarming.workflowStatus!.station!.alarms = ['5201'];
  alarming.workflowStatus!.station!.observedAt = stamp(-1000);
  data.sessions.push(alarming);
  assert.equal(chargerMonitoringState(data, '', now).fault, '5201');
  data.sessions.pop();
  data.sessions[0].workflowStatus!.battery!.observedAt = stamp(-5001);
  assert.equal(chargerMonitoringState(data, '', now).vehicleCharge, '车端采样非当前');
});

test('无充电会话时展示常驻TCP健康，但不把端口在线冒充桩工作状态', () => {
  const data: ChargingStatus = {
    ...status, enabled: false, capturedAt: stamp(),
    chargers: [{ chargerId: 'CHARGE-01', chargerName: '一号桩', address: '192.168.98.11', ackPortReachable: true, statusPortReachable: true,
      observedAt: stamp(), outcome: 'connected', message: '端口可连接', transport: 'TCP health only' }]
  };
  const view = chargerMonitoringState(data, '', now);
  assert.equal(view.label, '通信已连接 · 状态待读取');
  assert.equal(view.communication, '20207 / 20204 均可连接');
  assert.equal(view.operation, '待厂家只读状态协议');
  assert.equal(view.fault, '未知');
  assert.equal(view.vehicleCharge, '未知');
  assert.match(view.summary, /避免1001控制报文/);
});

test('常驻桩监控离线、部分可用和过期均明确降级', () => {
  for (const test of [
    { outcome: 'offline' as const, ack: false, status: false, label: '充电桩通信中断', tone: 'red' },
    { outcome: 'degraded' as const, ack: true, status: false, label: '充电桩通信部分异常', tone: 'amber' }
  ]) {
    const data: ChargingStatus = {
      ...status, capturedAt: stamp(), chargers: [{ chargerId: 'CHARGE-01', chargerName: '一号桩', address: '192.168.98.11',
        ackPortReachable: test.ack, statusPortReachable: test.status, observedAt: stamp(),
        outcome: test.outcome, message: '20204不可连接：超时' }]
    };
    const view = chargerMonitoringState(data, '', now);
    assert.equal(view.label, test.label);
    assert.equal(view.tone, test.tone);
    assert.equal(view.operation, '未知');
  }
  const stale: ChargingStatus = {
    ...status, capturedAt: stamp(), chargers: [{ chargerId: 'CHARGE-01', chargerName: '一号桩', address: '192.168.98.11',
      ackPortReachable: true, statusPortReachable: true, observedAt: stamp(-6000),
      outcome: 'connected', message: '端口可连接' }]
  };
  assert.equal(chargerMonitoringState(stale, '', now).label, '充电桩通信数据过期');
});
