import test from 'node:test';
import assert from 'node:assert/strict';
import { buildMapNodeBatches, type MapNodeBatchInput } from '../src/map-node-batching.ts';

const node = (
  id: string,
  overrides: Partial<MapNodeBatchInput> = {}
): MapNodeBatchInput => ({
  id,
  x: 10,
  y: 20,
  layerId: 'navigation',
  fill: '#0d344a',
  stroke: '#59bfd9',
  ...overrides
});

test('identical node styles collapse into one navigable overview path', () => {
  const batches = buildMapNodeBatches([
    node('A', { x: 10 }),
    node('B', { x: 20 }),
    node('C', { x: 30 })
  ], 1);
  assert.equal(batches.length, 1);
  assert.equal(batches[0]?.nodeCount, 3);
  assert.equal((batches[0]?.path.match(/ a /g) ?? []).length, 6);
});

test('selected, disabled and layer-specific node styles remain separate', () => {
  const batches = buildMapNodeBatches([
    node('A'),
    node('B', { fill: '#effaff', stroke: '#70e0f2' }),
    node('C', { fill: '#344254', stroke: '#71829a' }),
    node('D', { layerId: 'safety' })
  ], 1);
  assert.equal(batches.length, 4);
});

test('invalid radii are clamped and deterministic order is retained', () => {
  const batches = buildMapNodeBatches([node('A'), node('B', { layerId: 'safety' })], 0);
  assert.equal(batches.length, 2);
  assert.match(batches[0]?.path ?? '', /^M /);
  assert.match(batches[1]?.path ?? '', /^M /);
});
