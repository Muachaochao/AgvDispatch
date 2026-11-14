import test from 'node:test';
import assert from 'node:assert/strict';
import { buildMapEdgeBatches, type MapEdgeBatchInput } from '../src/map-edge-batching.ts';

const edge = (
  id: string,
  overrides: Partial<MapEdgeBatchInput> = {}
): MapEdgeBatchInput => ({
  id,
  path: `M 0 0 L 1 1 ${id}`,
  layerId: 'navigation',
  color: '#40a6c4',
  opacity: 1,
  strokeWidth: 0.2,
  bidirectional: false,
  ...overrides
});

test('merges roads with identical visual properties into one SVG path batch', () => {
  const batches = buildMapEdgeBatches([edge('A'), edge('B'), edge('C')], false);
  assert.equal(batches.length, 1);
  assert.equal(batches[0]?.edgeCount, 3);
  assert.match(batches[0]?.path ?? '', /M 0 0 L 1 1 A/);
  assert.match(batches[0]?.path ?? '', /M 0 0 L 1 1 C/);
});

test('keeps layers and visual styles in separate batches', () => {
  const batches = buildMapEdgeBatches([
    edge('A'),
    edge('B', { layerId: 'safety' }),
    edge('C', { color: '#e7ad45' }),
    edge('D', { opacity: 0.5 }),
    edge('E', { strokeWidth: 0.4 })
  ], false);
  assert.equal(batches.length, 5);
});

test('arrow modes split bidirectional roads only when arrows are visible', () => {
  const edges = [edge('A'), edge('B', { bidirectional: true })];
  assert.equal(buildMapEdgeBatches(edges, false).length, 1);
  const detailed = buildMapEdgeBatches(edges, true);
  assert.equal(detailed.length, 2);
  assert.ok(detailed.some(batch => batch.markerStart && batch.markerEnd));
  assert.ok(detailed.some(batch => !batch.markerStart && batch.markerEnd));
});

test('empty paths are omitted and batch order remains deterministic', () => {
  const batches = buildMapEdgeBatches([
    edge('A'),
    edge('EMPTY', { path: '' }),
    edge('B', { layerId: 'safety' })
  ], false);
  assert.deepEqual(batches.map(batch => batch.edgeCount), [1, 1]);
  assert.match(batches[0]?.path ?? '', / A$/);
  assert.match(batches[1]?.path ?? '', / B$/);
});
