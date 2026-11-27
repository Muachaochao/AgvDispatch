import test from 'node:test';
import assert from 'node:assert/strict';
import { simulationKpis, simulationWaitText } from '../src/simulation.ts';

const byKey = (items: ReturnType<typeof simulationKpis>) => Object.fromEntries(items.map(item => [item.key, item]));

test('KPI strip reports completed calls, running vehicles and current route-right waits', () => {
  const kpis = byKey(simulationKpis({
    tasks: { total: 12, completed: 7 }, activeVehicles: 3, vehicleCount: 5,
    trafficRequests: [
      { state: 'Waiting', waitingSeconds: 34, waitingClockValid: true },
      { state: 'Waiting', waitingSeconds: 21.4, waitingClockValid: true },
      { state: 'Granted', waitingSeconds: 0, waitingClockValid: true }
    ]
  }));
  assert.deepEqual(Object.keys(kpis), ['completed', 'vehicles', 'waiting', 'averageWait']);
  assert.equal(kpis.completed!.label, '完成单数');
  assert.equal(kpis.completed!.value, '7');
  assert.equal(kpis.completed!.detail, '/ 12 单');
  assert.equal(kpis.vehicles!.label, '运行车辆');
  assert.equal(kpis.vehicles!.value, '3');
  assert.equal(kpis.vehicles!.detail, '/ 5 辆');
  assert.equal(kpis.waiting!.label, '等待路权');
  assert.equal(kpis.waiting!.value, '2');
  assert.equal(kpis.waiting!.tone, 'waiting');
  assert.equal(kpis.averageWait!.label, '平均等待');
  assert.equal(kpis.averageWait!.value, '28 秒');
});

test('missing evidence is shown as unknown instead of zero', () => {
  const kpis = byKey(simulationKpis({ tasks: null, activeVehicles: 0, vehicleCount: 0, trafficRequests: null }));
  assert.equal(kpis.completed!.value, '—');
  assert.equal(kpis.vehicles!.value, '—');
  assert.equal(kpis.waiting!.value, '—');
  assert.equal(kpis.waiting!.detail, '路权快照不可用');
  assert.equal(kpis.averageWait!.value, '—');
});

test('waits without a valid clock are counted but excluded from the average', () => {
  const kpis = byKey(simulationKpis({ tasks: { total: 0, completed: 0 }, activeVehicles: 1, vehicleCount: 2,
    trafficRequests: [{ state: 'Waiting', waitingSeconds: 400, waitingClockValid: false }] }));
  assert.equal(kpis.completed!.value, '—');
  assert.equal(kpis.waiting!.value, '1');
  assert.equal(kpis.averageWait!.value, '—');
  assert.equal(kpis.averageWait!.detail, '等待计时不可用');
  const idle = byKey(simulationKpis({ tasks: { total: 2, completed: 2 }, activeVehicles: 0, vehicleCount: 2, trafficRequests: [] }));
  assert.equal(idle.waiting!.value, '0');
  assert.equal(idle.waiting!.tone, 'pass');
  assert.equal(idle.averageWait!.value, '—');
  assert.equal(idle.averageWait!.detail, '当前无等待');
});

test('wait durations use minutes once they pass one minute', () => {
  assert.equal(simulationWaitText(0), '0 秒');
  assert.equal(simulationWaitText(59.4), '59 秒');
  assert.equal(simulationWaitText(75), '1 分 15 秒');
  assert.equal(simulationWaitText(Number.NaN), '—');
});
