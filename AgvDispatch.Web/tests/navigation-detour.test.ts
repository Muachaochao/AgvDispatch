import test from 'node:test';
import assert from 'node:assert/strict';
import { detourCanContinue, detourPreviewCurrent, type NavigationDetourCurrent, type NavigationDetourPlan,
  type NavigationDetourPreview, type NavigationDetourRecord } from '../src/navigation-detour.ts';
import type { WebMap } from '../src/types.ts';

const now = Date.parse('2026-10-30T06:00:00Z');
const map: WebMap = { id: 'triangle', version: 'V1', definitionHash: 'ABC', name: '', unit: 'm', coordinateSystem: 'local', nodes: [], edges: [], resources: [], warnings: [] };
const plan: NavigationDetourPlan = { available: true, message: '候选', closureRevision: 7, mapId: map.id, versionId: map.version,
  definitionHash: 'abc', robotMap: 'robot-map', robotMapMd5: 'md5', sourceSite: 'LM1', targetSite: 'LM3', route: ['LM1', 'LM2', 'LM3'], blockedEdgeIds: ['E13'] };
const current: NavigationDetourCurrent = { taskId: 'original', vehicleId: 'fork', targetSite: 'LM3', robotMap: 'robot-map', robotMapMd5: 'md5', state: 'Running' };
const preview: NavigationDetourPreview = { previewId: 'P', expiresAt: new Date(now + 30000).toISOString(), taskId: current.taskId,
  vehicleId: current.vehicleId, blockedSince: new Date(now - 10000).toISOString(), plan };
const record: NavigationDetourRecord = { previewId: 'P', state: 'Ready', message: '', plan, originalVendorTaskIds: ['old-1'],
  originalRoute: ['LM1', 'LM3'], stoppedObservedAt: new Date(now).toISOString(), sendAttemptedAt: null, newVendorTaskIds: [] };

test('候选严格绑定同一测试、车辆、原终点与车端地图', () => {
  assert.equal(detourPreviewCurrent(preview, current, map, now), true);
  for (const change of [{ taskId: 'other' }, { vehicleId: 'other' }, { targetSite: 'LM2' }, { robotMap: 'other' }, { robotMapMd5: 'other' }])
    assert.equal(detourPreviewCurrent(preview, { ...current, ...change }, map, now), false);
  assert.equal(detourPreviewCurrent(preview, null, map, now), false);
});

test('运行地图切换、内容变化、候选到期后不能确认结束原导航', () => {
  for (const change of [{ id: 'other' }, { version: 'V2' }, { definitionHash: 'other' }, { definitionHash: undefined }])
    assert.equal(detourPreviewCurrent(preview, current, { ...map, ...change }, now), false);
  assert.equal(detourPreviewCurrent(preview, current, map, now + 30000), false);
  assert.equal(detourPreviewCurrent({ ...preview, expiresAt: 'bad' }, current, map, now), false);
});

test('未发送阶段可重新核验，已尝试发送后旧预览不能重新使用', () => {
  assert.equal(detourPreviewCurrent(preview, { ...current, detour: record }, map, now), true);
  assert.equal(detourPreviewCurrent(preview, { ...current, detour: { ...record, state: 'ReviewHold' } }, map, now), true);
  assert.equal(detourPreviewCurrent(preview, { ...current, detour: { ...record, sendAttemptedAt: new Date(now).toISOString() } }, map, now), false);
  assert.equal(detourCanContinue({ ...current, state: 'DetourReady', detour: record }, map), true);
});

test('取消应答、未停稳、重启复核及未知发送都不允许执行备选路线', () => {
  for (const state of ['CancelIntent', 'CancelRequested', 'AwaitingOriginalTerminal', 'ReviewHold', 'SendIntent', 'Executing', 'unknown'])
    assert.equal(detourCanContinue({ ...current, state: 'DetourReady', detour: { ...record, state } }, map), false);
  assert.equal(detourCanContinue({ ...current, detour: record }, map), false);
  assert.equal(detourCanContinue({ ...current, state: 'DetourReady', detour: { ...record, sendAttemptedAt: new Date(now).toISOString() } }, map), false);
});

test('无备选道路或异图候选保留等待，不能由确认按钮越过', () => {
  assert.equal(detourCanContinue({ ...current, state: 'DetourReady', detour: { ...record, plan: { ...plan, available: false, route: [] } } }, map), false);
  assert.equal(detourCanContinue({ ...current, state: 'DetourReady', detour: record }, { ...map, version: 'V2' }), false);
});
