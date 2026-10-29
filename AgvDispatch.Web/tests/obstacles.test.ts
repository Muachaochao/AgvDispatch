import { test } from 'node:test';
import assert from 'node:assert/strict';
import { vehicleCategory } from '../src/dashboard.ts';
import { vehicleObstacle, obstacleDuration } from '../src/obstacles.ts';
import type { TaskRow, Vehicle, VehicleObstacle } from '../src/types.ts';

const now = Date.parse('2026-10-30T04:00:00Z');
const at = (delta = 0) => new Date(now + delta).toISOString();
const task: TaskRow = { taskId: 'original', stationId: 'P1', vehicleId: 'AGV-01', stage: 'Executing', priority: 50, createdAt: at(), updatedAt: at() };
function observation(overrides: Partial<VehicleObstacle> = {}): VehicleObstacle {
  return { state: 'Blocked', since: at(-3000), durationSeconds: 3, lastClearedAt: null, source: 'Blocked / 52200',
    message: '保留原任务，等待车端处理。', navigationPolicy: '指定路径；保留原任务，不自动取消或改道',
    localDetourEvidence: '未采集车端局部轨迹，未验证自主绕障', ...overrides };
}
function vehicle(overrides: Partial<Vehicle> = {}): Vehicle {
  return { vehicleId: 'AGV-01', displayName: '测试车', connection: 'Online', quality: 'Good', batteryPercent: 80,
    currentStation: 'P1', navigationState: 'Executing', x: 1, y: 2, angle: 0, localizationConfidence: .99,
    mapPositionState: 'Normal', mapPositionMessage: '', lastSuccessfulPollAt: at(), updatedAt: at(),
    blocked: false, emergencyStopped: false, ...overrides };
}

test('短时障碍使执行车辆显示受阻等待，保留原任务分类依据', () => {
  const v = vehicle({ blocked: true, obstacle: observation() });
  assert.equal(vehicleCategory(v, [task], true, now), 'blocked');
  assert.equal(vehicleObstacle(v, true, now).duration, '3 秒');
  assert.equal(task.stage, 'Executing');
});

test('仅车端52200诊断也显示受阻，30秒升级为持续受阻', () => {
  const v = vehicle({ obstacle: observation({ durationSeconds: 29.9 }) });
  assert.equal(vehicleCategory(v, [task], true, now), 'blocked');
  assert.equal(vehicleObstacle(v, true, now).state, 'Blocked');
  v.obstacle = observation({ durationSeconds: 30 });
  assert.equal(vehicleObstacle(v, true, now).state, 'Prolonged');
  assert.equal(vehicleObstacle(v, true, now).label, '持续受阻（≥30秒）');
});

test('严重报警与急停优先于受阻，即使仍在执行原任务', () => {
  for (const v of [vehicle({ blocked: true, obstacle: observation({ state: 'SafetyStop', message: 'Fatal 52200' }) }),
    vehicle({ blocked: true, emergencyStopped: true, obstacle: observation() })]) {
    assert.equal(vehicleCategory(v, [task], true, now), 'fault');
    assert.equal(vehicleObstacle(v, true, now).state, 'SafetyStop');
    assert.equal(vehicleObstacle(v, true, now).duration, '—');
  }
});

test('后台Unknown优先于Blocked，旧后台急停缺失时也不能显示安全等待', () => {
  for (const v of [vehicle({ blocked: true, obstacle: observation({ state: 'Unknown', message: '安全状态未知。' }) }),
    vehicle({ blocked: true, emergencyStopped: null }), vehicle({ blocked: true, emergencyStopped: undefined })]) {
    const view = vehicleObstacle(v, true, now);
    assert.equal(view.state, 'Unknown');
    assert.equal(view.duration, '—');
    assert.equal(view.durationSeconds, null);
    assert.equal(vehicleCategory(v, [task], true, now), 'unknown');
  }
  assert.equal(vehicleObstacle(vehicle({ blocked: true, emergencyStopped: true,
    obstacle: observation({ state: 'Unknown' }) }), true, now).state, 'SafetyStop');
});

test('后台刷新不能让过期车端样本继续计时，也不能据旧解除样本显示当前恢复', () => {
  for (const state of ['Clear', 'Blocked', 'Prolonged'] as const) {
    const v = vehicle({ lastSuccessfulPollAt: at(-5001), obstacle: observation({ state, durationSeconds: 32, lastClearedAt: at(-8000) }) });
    const view = vehicleObstacle(v, true, now);
    assert.equal(view.state, 'Unknown');
    assert.equal(view.durationSeconds, null);
    assert.equal(view.duration, '—');
    assert.equal(vehicleCategory(v, [task], true, now), 'offline');
  }
});

test('断线、坏质量、未来时间与快照失效均显示未知，不声称任务恢复', () => {
  for (const v of [vehicle({ connection: 'Interrupted' }), vehicle({ quality: 'Bad' }),
    vehicle({ lastSuccessfulPollAt: null }), vehicle({ lastSuccessfulPollAt: at(1001) })]) {
    assert.equal(vehicleObstacle(v, true, now).state, 'Unknown');
  }
  assert.equal(vehicleObstacle(vehicle(), false, now).state, 'Unknown');
});

test('浏览器时钟不从开始时间外推受阻时长，未知或无效时长保持未知', () => {
  const v = vehicle({ obstacle: observation({ since: at(-120000), durationSeconds: 2 }) });
  assert.equal(vehicleObstacle(v, true, now).duration, '2 秒');
  for (const durationSeconds of [null, NaN, Infinity, -1]) {
    v.obstacle = observation({ durationSeconds });
    assert.equal(vehicleObstacle(v, true, now).duration, '时长未知');
  }
  assert.equal(obstacleDuration(60.9), '1 分 0 秒');
  assert.equal(obstacleDuration(125), '2 分 5 秒');
});

test('旧后台兼容Blocked字段，不凭原始坐标移动宣布自主绕行', () => {
  const v = vehicle({ blocked: true, x: 100, y: 50 });
  const view = vehicleObstacle(v, true, now);
  assert.equal(vehicleCategory(v, [task], true, now), 'blocked');
  assert.equal(view.duration, '时长未知');
  assert.match(view.localDetourEvidence, /未验证自主绕障/);
  assert.match(view.navigationPolicy, /不自动取消或改道/);
  assert.equal(vehicleCategory(vehicle({ blocked: null }), [], true, now), 'unknown');
});

test('解除观察单独显示，原任务仍由任务反馈决定是否执行与完成', () => {
  const v = vehicle({ obstacle: observation({ state: 'Clear', since: null, durationSeconds: null, lastClearedAt: at(-1000), message: '' }) });
  const view = vehicleObstacle(v, true, now);
  assert.equal(view.label, '已观察到解除');
  assert.equal(view.duration, '—');
  assert.equal(vehicleCategory(v, [task], true, now), 'running');
  assert.equal(vehicleCategory(v, [{ ...task, stage: 'Completed' }], true, now), 'unknown');
});
