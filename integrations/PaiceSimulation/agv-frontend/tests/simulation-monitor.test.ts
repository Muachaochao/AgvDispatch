import { test } from 'node:test'
import assert from 'node:assert/strict'
import { callPresentation, resourceLocations, resourceRows, vehicleTaskKind, routeTarget, trafficWaitRows, runTiming } from '../src/integration/simulation-monitor.ts'
import * as monitor from '../src/integration/simulation-monitor.ts'

test('shows formal charging evidence after docking and preserves business during a pending charge', () => {
  const queue = { vehicleId: 'AGV-03', phase: 'WaitingForBusiness', message: 'BAY-02 暂无可用路线' };
  const charging = { vehicleId: 'AGV-02', phase: 'Charging', site: { id: 'BAY-01' }, battery: { current: 16.5, charging: true } };
  const sessions = [queue, charging, { vehicleId: 'AGV-02', phase: 'Completed' }];
  assert.equal(monitor.chargingPresentation(sessions, 'AGV-02', false)?.label, '充电中');
  assert.equal(monitor.chargingPresentation(sessions, 'AGV-02', false)?.current, 16.5);
  assert.equal(monitor.chargingPresentation(sessions, 'AGV-03', false)?.label, '排队充电');
  assert.equal(monitor.chargingPresentation(sessions, 'AGV-03', true)?.label, '业务完成后充电');
  assert.equal(monitor.chargingPresentation(sessions, 'AGV-03', true)?.pending, true);
  assert.equal(monitor.chargingPresentation([{ ...charging, phase: 'AwaitingStop', shutdownVerified: true }], 'AGV-02', false)?.label, '等待离桩');
  assert.equal(monitor.chargingPresentation([{ ...charging, phase: 'Completed' }], 'AGV-02', false), undefined);
})

test('shows a staging destination and earliest charger independently', () => {
  const row = { vehicleId: 'AGV-03', phase: 'GoingToWaitingSite', waitingSiteId: 'SAFE-2', message: '等空桩' };
  const moving = monitor.chargingPresentation([row], row.vehicleId, false);
  assert.equal(moving?.label, '前往安全等桩位');
  assert.equal(moving?.target, 'SAFE-2');
  const parked = monitor.chargingPresentation([{ ...row, phase: 'Queued',
    siteEstimates: [{ siteId: 'BAY-01', startInSeconds: 91 }, { siteId: 'BAY-02', startInSeconds: 20 }] }], row.vehicleId, false);
  assert.equal(parked?.label, '安全位等桩');
  assert.equal(parked?.reason, '等空桩');
  assert.match(parked?.details ?? '', /BAY-02.*20/);
})

test('keeps repeated calls separate and explains the previous unfinished cycle only once due', () => {
  const first = { planIndex: 0, stationId: 'CNC-111', atSeconds: 12, status: 'active', taskId: 'A', vehicleId: 'AGV-01' }
  const next = { planIndex: 7, stationId: 'CNC-111', atSeconds: 21, status: 'pending' }
  assert.match(callPresentation(next, [first, next], 22).reason, /第 1 单/)
  assert.equal(callPresentation(next, [first, next], 19).reason, '等待计划触发')
  assert.equal(callPresentation({ ...next, status: 'emitted' }, [first], 25).reason, '等待 PLC 呼叫建单')
  assert.equal(callPresentation({ ...first, status: 'created', vehicleId: undefined, stepNumber: 1 }, [], 20).step, '待派车')
})

test('shows active resources first with short map labels and keeps full IDs searchable', () => {
  const long = 'AUTO-CORRIDOR-0123456789ABCDEF'
  const map = {
    nodes: [{ id: 'BUS-CNC-154', x: 0, y: 0 }, { id: 'J-01', x: 1, y: 0 }],
    edges: [{ id: 'E-01', from: 'BUS-CNC-154', to: 'J-01', resourceIds: [long] }],
  }
  const locations = resourceLocations(map)
  const resources = [{ resourceId: 'free', occupiedCount: 0, capacity: 1 }, { resourceId: long, occupiedCount: 1, capacity: 1 }]
  const waiting = [{ stepId: 'step', taskId: 'task', vehicleId: 'AGV-02', blockers: [{ resourceId: long, vehicleId: 'AGV-01' }] }]
  const rows = resourceRows(resources, waiting, locations, '', false)
  assert.deepEqual(rows.map(row => row.resourceId), [long])
  assert.match(rows[0].label, /CNC-154/)
  assert.equal(rows[0].waits[0].vehicleId, 'AGV-02')
  assert.deepEqual(locations.get(long)?.edgeIds, ['E-01'])
  assert.equal(resourceRows(resources, [], locations, '0123456789ABCDEF', false).length, 1)
  assert.equal(resourceRows(resources, [], locations, 'free', false).length, 1)
})

test('uses real navigation and charging evidence and avoids carrying over old destinations', () => {
  assert.equal(vehicleTaskKind({ charging: true, current_task: 'move' }, true), '充电')
  assert.equal(vehicleTaskKind({ current_task: 'STANDBY-abc' }, false), '回待机')
  assert.equal(vehicleTaskKind({ current_task: 'move' }, true), '业务 · 导航')
  assert.equal(vehicleTaskKind(undefined, false), '未确认')
  assert.equal(routeTarget({ vendorSites: ['A', 'B'], nodeIds: ['J-01', 'J-02'] }), 'B')
  assert.equal(routeTarget(undefined), undefined)
})
test('uses one row per request and distinguishes request age from the current blockers', () => {
  const now = Date.parse('2026-11-21T13:00:20Z')
  const waiting = {
    taskId: 'A', stepId: 'S', vehicleId: 'AGV-04', state: 'Waiting', waitingSeconds: 600,
    blockingSince: '2026-11-21T13:00:10Z', blockingObservationCurrent: true, blockingStartKnown: true,
    blockers: [{ resourceId: 'R1', vehicleId: 'AGV-02' }, { resourceId: 'R1', vehicleId: 'AGV-03' },
      { resourceId: 'R2', vehicleId: 'AGV-02' }],
  }
  const rows = trafficWaitRows([waiting, { ...waiting, taskId: 'B', blockingObservationCurrent: false },
    { ...waiting, taskId: 'C', waitingCategory: 'Equipment' }], now)
  assert.equal(rows.length, 2)
  assert.deepEqual(rows[0].holders, ['AGV-02', 'AGV-03'])
  assert.equal(rows[0].blockers.length, 2)
  assert.equal(rows[0].blockingSeconds, 10)
  assert.equal(rows[1].blockingSeconds, null)
})

test('distinguishes reference conflicts from window reservations and existing leases', () => {
  const resourceIds = Array.from({ length: 97 }, (_, i) => `R${i}`)
  const blockers = resourceIds.slice(0, 9).map(resourceId => ({ resourceId, vehicleId: 'AGV-02' }))
  const reference = {
    taskId: 'A', stepId: 'S', state: 'Waiting', waitingCategory: 'ReferenceRoute',
    resourceIds, blockers, heldResourceIds: ['P', 'P'], edgeIds: ['E1', 'E2'],
  }
  const legacy = { ...reference, waitingCategory: 'Route', reason: '候车位或其路径不可用，保持在原取箱点；未发送命令' }
  const window = { ...reference, waitingCategory: 'Route', resourceIds: ['R0'], blockers: blockers.slice(0, 1) }
  for (const request of [reference, legacy]) {
    const [row] = trafficWaitRows([request], 0)
    assert.equal(row.reference, true)
    assert.equal(row.resourceCount, 97)
    assert.equal(row.blockers.length, 9)
    assert.equal(row.heldCount, 1)
    assert.equal(row.segmentCount, 2)
    const resources = [{ resourceId: 'R0', occupiedCount: 1, capacity: 1 }]
    assert.equal(resourceRows(resources, [request], new Map(), '', false)[0].blocking, false)
    assert.equal(resourceRows(resources, [request], new Map(), '', false)[0].waits.length, 0)
  }
  assert.equal(trafficWaitRows([window], 0)[0].reference, false)
  assert.equal(resourceRows([{ resourceId: 'R0', occupiedCount: 1, capacity: 1 }], [window], new Map(), '', false)[0].blocking, true)
})

test('keeps business seconds and wall time separate and stops at report cutoff', () => {
  assert.deepEqual(runTiming({ simulationRate: 8, scenario: { startedAt: 100 }, device: { time: 180 } }),
    { rate: 8, businessSeconds: 80, wallSeconds: 10 })
  assert.deepEqual(runTiming({ simulationRate: 8, scenario: { id: 'running', startedAt: 100 },
    report: { id: 'running', startedAt: 100, finishedAt: 164 }, device: { time: 180 } }),
    { rate: 8, businessSeconds: 64, wallSeconds: 8 })
  assert.deepEqual(runTiming({ scenario: { id: 'new', startedAt: 100 },
    report: { id: 'old', startedAt: 20, finishedAt: 60 } }), { rate: undefined, businessSeconds: undefined, wallSeconds: undefined })
})
