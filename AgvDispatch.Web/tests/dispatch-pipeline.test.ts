import test from 'node:test';
import assert from 'node:assert/strict';
import {
  pipelineLayerViews, pipelineEvidenceState, pipelineTimestampFresh, pipelineTime, retainPipelineObservation,
  pipelineBusinessReference, pipelineSimulationCaptureValid,
  filterPipelineRecords, pipelineRecordIsIssue, pipelineRecordState, pipelineInternalLink, pipelineLayerKey,
  pipelineExecutionSummary, type PipelineInspectionSnapshot, type PipelineInspectionLayer, type PipelineRecord
} from '../src/dispatch-pipeline.ts';

const now = Date.parse('2026-11-03T09:00:00+08:00');
const stamp = new Date(now).toISOString();
const layer = (key: string, state = 'Ready', observedAt: string | null = stamp): PipelineInspectionLayer => ({
  key, number: 1, name: key, state, summary: '真实观察', observedAt, checks: [], metrics: [], records: [], totalCount: 0, truncated: false, note: '只读来源'
});
const snapshot = (capturedAt = stamp): PipelineInspectionSnapshot => ({
  capturedAt, cycleNumber: 1, optimizationCapturedAt: null, mode: 'Shadow', summary: '未启用', gates: [], layers: [layer('queue')]
});
const record = (state: string, patch: Partial<PipelineRecord> = {}): PipelineRecord => ({
  id: 'STEP-ORIGINAL', state, title: '原任务记录', reason: '等待 LM28', updatedAt: '2025-01-01T00:00:00Z',
  facts: [{ label: '原厂家编号', value: 'VENDOR-ORIGINAL' }], ...patch
});

test('fixed six-layer navigation retains missing, duplicate or future evidence without declaring ready', () => {
  const rows = pipelineLayerViews(snapshot(), true, now);
  assert.deepEqual(rows.map(row => row.key), ['queue', 'candidates', 'matching', 'traffic', 'safety', 'execution']);
  assert.equal(rows[0]?.status.tone, 'ready'); assert.equal(rows[1]?.status.tone, 'unknown');
  assert.equal(pipelineLayerViews({ ...snapshot(), layers: [layer('queue'), layer('queue')] }, true, now)[0]?.status.tone, 'unknown');
  assert.notEqual(pipelineLayerViews({ ...snapshot(), layers: [layer('queue', 'Ready', new Date(now + 2000).toISOString())] }, true, now)[0]?.status.tone, 'ready');
  assert.equal(pipelineLayerKey('unsafe-or-unknown-layer'), 'queue');
});

test('missing, stale, disconnected or not-executed checks cannot be displayed as passed', () => {
  assert.equal(pipelineEvidenceState('Pass', stamp, true, now).tone, 'ready');
  for (const [state, observed, fresh] of [
    ['NotChecked', null, true], ['Pass', null, true], ['Pass', new Date(now - 15000).toISOString(), true],
    ['Pass', stamp, false], ['Pass', '0001-01-01T00:00:00Z', true], ['UnknownFutureState', stamp, true]
  ] as const) assert.notEqual(pipelineEvidenceState(state, observed, fresh, now).tone, 'ready');
  assert.equal(pipelineEvidenceState('Fail', stamp, true, now).tone, 'blocked');
  assert.equal(pipelineEvidenceState('Disabled', stamp, true, now).label, '未启用');
});

test('history is judged at its capture time while the same observation becomes stale in live time', () => {
  assert.equal(pipelineEvidenceState('Pass', stamp, true, now).tone, 'ready');
  assert.equal(pipelineEvidenceState('Pass', stamp, true, now + 60000).tone, 'stale');
  assert.equal(pipelineTimestampFresh(new Date(now + 2000).toISOString(), now), false);
  assert.equal(pipelineTimestampFresh('invalid', now), false);
  assert.equal(pipelineTime('0001-01-01T00:00:00Z'), '尚无记录');
});

test('accelerated isolated observations use the shared business clock for freshness only', () => {
  const origin = Date.parse('2026-11-22T12:00:00Z');
  assert.equal(pipelineBusinessReference(now, { originUnixMs: now - 120_000, rate: 2 }), now + 120_000);
  const businessNow = now + 120_000;
  const captured = new Date(businessNow - 20_000).toISOString();
  assert.equal(pipelineSimulationCaptureValid(captured, now, { originUnixMs: now - 120_000, rate: 2 }, 30_000), true);
  assert.equal(pipelineSimulationCaptureValid(new Date(businessNow - 61_000).toISOString(), now, { originUnixMs: origin, rate: 2 }, 30_000), false);
  assert.equal(pipelineSimulationCaptureValid(new Date(businessNow + 2_000).toISOString(), now, { originUnixMs: origin, rate: 2 }, 30_000), false);
  const businessReference = pipelineBusinessReference(now, { originUnixMs: origin, rate: 2 });
  assert.equal(pipelineEvidenceState('Pass', new Date(businessReference - 2_000).toISOString(), true, businessReference).tone, 'ready');
  assert.equal(pipelineBusinessReference(now, { originUnixMs: origin }), now);
  assert.equal(pipelineSimulationCaptureValid(captured, now), pipelineTimestampFresh(captured, now, 30_000));
});

test('observation retention keys off collection time rather than unchanged optimization cycle and remains immutable and bounded', () => {
  const original = snapshot(); original.layers[0]!.records = [record('Waiting')];
  let history = retainPipelineObservation([], original);
  const frozen = history[0]!;
  original.layers[0]!.records[0]!.reason = 'Changed after capture';
  assert.equal(frozen.snapshot.layers[0]?.records[0]?.reason, '等待 LM28');
  for (let index = 1; index <= 65; index++) history = retainPipelineObservation(history, snapshot(new Date(now + index * 5000).toISOString()));
  assert.equal(history.length, 60); assert.ok(history.every(row => row.snapshot.cycleNumber === 1));
  assert.equal(frozen.snapshot.capturedAt, stamp);
  assert.equal(frozen.snapshot.layers[0]?.records[0]?.id, 'STEP-ORIGINAL');
  history = retainPipelineObservation(history, history[0]!.snapshot);
  assert.equal(history.length, 60);
});

test('record filters trace tasks, vehicles, resources and original IDs including expanded facts without treating old durable timestamps as stale', () => {
  const waiting = record('WaitingForVehicle', { taskId: 'T-A', stationId: 'CNC-01', vehicleId: 'AGV-03', resourceId: 'TRAFFIC-LM28' });
  const ready = record('Prepared', { id: 'STEP-B', taskId: 'T-B' });
  assert.equal(filterPipelineRecords([waiting, ready], 't-a agv-03', false).length, 1);
  assert.equal(filterPipelineRecords([waiting], 'vendor-original', false).length, 1);
  assert.equal(filterPipelineRecords([waiting], 'TRAFFIC-LM28', true).length, 1);
  assert.equal(pipelineRecordState(waiting.state).label, '等待派车');
  assert.equal(filterPipelineRecords([ready], '', true).length, 0);
  assert.equal(pipelineRecordState('FirmwareSpecificState').label, '状态待核对');
  assert.equal(pipelineRecordState('FirmwareSpecificState').tone, 'neutral');
});

test('full capacity and abnormal retained responsibility stay visible under attention filter while sending remains a normal business state', () => {
  for (const state of ['Full', 'Retained', 'SignalConflict', 'ManualIntervention', 'Offline']) assert.equal(pipelineRecordIsIssue(record(state)), true, state);
  assert.equal(pipelineRecordIsIssue(record('Sending')), false);
  assert.equal(pipelineRecordState('Sending').label, '已领取发送');
  assert.equal(pipelineRecordState('Recommended').label, '仅推荐 · 尚未登记');
});

test('only internal route links are used for related records', () => {
  assert.equal(pipelineInternalLink('/tasks/T-A?tab=steps'), '/tasks/T-A?tab=steps');
  for (const link of ['https://other.test', '//other.test', 'javascript:alert(1)', '/\\other', '/task\nnext']) assert.equal(pipelineInternalLink(link), undefined);
});

test('execution summary distinguishes disabled, incomplete and open switches without granting permission', () => {
  const gate = (key: string, enabled: boolean | null) => ({ key, enabled, label: key, detail: '' });
  assert.match(pipelineExecutionSummary([gate('dispatch', false), gate('outbox', true)], true), /未全部启用/);
  assert.match(pipelineExecutionSummary([gate('dispatch', true), gate('outbox', true)], true), /逐单仍须复核/);
  assert.equal(pipelineExecutionSummary([gate('dispatch', null)], true), '执行开关状态不完整');
  assert.equal(pipelineExecutionSummary([gate('dispatch', true), gate('outbox', true)], false), '未取得当前执行开关');
});
