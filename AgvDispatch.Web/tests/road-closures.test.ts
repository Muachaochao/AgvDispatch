import test from 'node:test';
import assert from 'node:assert/strict';
import { closureMapMatches, closurePageCurrent, closureRouteCurrent, closureSites,
  type RoadClosurePage, type RoadRoutePreview } from '../src/road-closures.ts';
import type { WebMap } from '../src/types.ts';

const now = Date.parse('2026-10-30T06:00:00Z');
const at = (offset = 0) => new Date(now + offset).toISOString();
const map: WebMap = { id: 'test', version: 'V1', definitionHash: 'abc', name: '三角测试', unit: 'm', coordinateSystem: 'local',
  nodes: [{ id: 'N1', agvSiteId: 'LM1', x: 0, y: 0, enabled: true, type: 'Station' },
    { id: 'N2', agvSiteId: 'LM2', x: 1, y: 1, enabled: true, type: 'Station' },
    { id: 'N3', agvSiteId: 'LM3', x: 2, y: 0, enabled: false, type: 'Station' }], edges: [], resources: [], warnings: [] };
const page: RoadClosurePage = { ready: true, receivedAt: at(), mapId: map.id, versionId: map.version,
  definitionHash: 'ABC', revision: 5, closures: [] };
const preview: RoadRoutePreview = { receivedAt: at(), mapId: map.id, versionId: map.version, definitionHash: 'ABC', revision: 5,
  sourceSite: 'N1', targetSite: 'N2', available: true, route: { nodes: ['LM1', 'LM2'], edgeIds: ['E12'], resourceIds: [], cost: 1 }, message: '候选' };

test('临时封锁绑定地图、版本和内容，旧图同名通道不能用于操作', () => {
  assert.equal(closureMapMatches(page, map), true);
  for (const change of [{ mapId: 'other' }, { versionId: 'old' }, { definitionHash: 'changed' }, { definitionHash: null }])
    assert.equal(closureMapMatches({ ...page, ...change }, map), false);
  assert.equal(closureMapMatches(page, { ...map, definitionHash: undefined }), false);
});

test('断连、封锁未就绪和旧读取不能继续变更封锁', () => {
  assert.equal(closurePageCurrent(page, map, true, now), true);
  assert.equal(closurePageCurrent(page, map, false, now), false);
  assert.equal(closurePageCurrent({ ...page, ready: false }, map, true, now), false);
  assert.equal(closurePageCurrent(page, map, true, now + 15001), false);
  assert.equal(closurePageCurrent({ ...page, receivedAt: at(1001) }, map, true, now), false);
  assert.equal(closurePageCurrent({ ...page, receivedAt: 'bad' }, map, true, now), false);
});

test('改动封锁、地图或起终点后候选失效；候选可达不是已发送证据', () => {
  const current = (candidate = preview, list = page, from = 'N1', to = 'N2') => closureRouteCurrent(candidate, list, map, from, to, true, now);
  assert.equal(current(), true);
  assert.equal(current(preview, { ...page, revision: 6 }), false);
  assert.equal(current({ ...preview, versionId: 'V2' }), false);
  assert.equal(current(preview, page, 'N2', 'N1'), false);
  assert.equal(current({ ...preview, receivedAt: at(-15001) }), false);
  assert.equal(current({ ...preview, available: false, route: null }), true);
});

test('选择站点保留逻辑编号与厂家站点映射，禁用站点不用于新规划', () => {
  assert.deepEqual(closureSites(map), [{ id: 'N1', label: 'LM1（N1）' }, { id: 'N2', label: 'LM2（N2）' }]);
  assert.deepEqual(closureSites(null), []);
});
