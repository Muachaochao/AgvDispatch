import test from 'node:test';
import assert from 'node:assert/strict';
import {
  selectNodeLabels,
  type MapLabelNode,
  type MapViewRect
} from '../src/map-render-lod.ts';

const view: MapViewRect = { x: 0, y: -100, width: 100, height: 100 };
const node = (
  id: string,
  x: number,
  y: number,
  type = 'Waypoint'
): MapLabelNode => ({ id, x, y, type, enabled: true });

test('auto mode keeps key nodes and selected waypoints when zoomed out', () => {
  const rows = [
    node('W1', 10, 10, 'Waypoint'),
    node('S1', 30, 20, 'Station'),
    node('I1', 50, 30, 'Intersection'),
    node('C1', 70, 40, 'ChargingPoint'),
    node('W2', 90, 50, 'Waypoint')
  ];
  assert.deepEqual(
    selectNodeLabels(rows, { mode: 'auto', zoom: 1, scale: 1, view, selectedIds: ['W2'] }).map(item => item.id),
    ['W2', 'S1', 'I1', 'C1']
  );
});

test('auto mode exposes ordinary waypoints after the detail zoom threshold', () => {
  const rows = [node('W1', 10, 10), node('W2', 30, 20)];
  assert.deepEqual(
    selectNodeLabels(rows, { mode: 'auto', zoom: 2.5, scale: 1, view, selectedIds: [] }).map(item => item.id),
    ['W1', 'W2']
  );
});

test('collision filtering prefers stations and always keeps a selected node', () => {
  const rows = [
    node('W1', 20, 20, 'Waypoint'),
    node('S1', 20.2, 20, 'Station'),
    node('W2', 80, 20, 'Waypoint')
  ];
  const accepted = selectNodeLabels(rows, {
    mode: 'all',
    zoom: 1,
    scale: 1,
    view,
    selectedIds: ['W2']
  });
  assert.deepEqual(accepted.map(item => item.id), ['W2', 'S1']);
});

test('label modes can force all candidates or hide labels entirely', () => {
  const rows = [node('W1', 10, 10), node('W2', 34, 10)];
  assert.deepEqual(
    selectNodeLabels(rows, { mode: 'key', zoom: 10, scale: 1, view, selectedIds: [] }),
    []
  );
  assert.deepEqual(
    selectNodeLabels(rows, { mode: 'all', zoom: 1, scale: 1, view, selectedIds: [] }).map(item => item.id),
    ['W1', 'W2']
  );
  assert.deepEqual(
    selectNodeLabels(rows, { mode: 'none', zoom: 10, scale: 1, view, selectedIds: ['W1'] }),
    []
  );
});

test('labels outside the current view are not considered', () => {
  const rows = [node('S1', 500, 500, 'Station'), node('S2', 50, 50, 'Station')];
  assert.deepEqual(
    selectNodeLabels(rows, { mode: 'auto', zoom: 1, scale: 1, view, selectedIds: [] }).map(item => item.id),
    ['S2']
  );
});
