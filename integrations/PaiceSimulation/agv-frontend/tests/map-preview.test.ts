import assert from 'node:assert/strict'
import test from 'node:test'
import type { NetEdge, NetNode } from '../src/api/roadnet.ts'
import { previewNodePositions } from '../src/integration/map-navigation.ts'

function node(id: string, x: number, y: number, type: NetNode['type'] = 'JUNCTION'): NetNode {
  return { id, x, y, type }
}

function edge(from: string, to: string): NetEdge {
  return { id: `${from}-${to}`, from, to, length: 0.05, direction: 'ONE_WAY', speed: 0.75, capacity: 1 }
}

test('dense previews extend short workstation branches on both sides of the road', () => {
  const nodes = [node('north', 0, -0.05, 'WORKSTATION'), node('south', 1, 0.2, 'WORKSTATION'),
    ...Array.from({ length: 29 }, (_, i) => node(`road-${i}`, i, 0))]
  const edges = [edge('north', 'road-0'), edge('road-0', 'north'), edge('road-1', 'south')]

  const preview = previewNodePositions(nodes, edges)
  assert.deepEqual(preview.get('north'), { ...nodes[0], y: -1.2 })
  assert.deepEqual(preview.get('south'), { ...nodes[1], y: 1.2 })
  assert.deepEqual(preview.get('road-0'), nodes[2])
  assert.equal(nodes[0]?.y, -0.05)
  assert.equal(nodes[1]?.y, 0.2)
  assert.equal(previewNodePositions(nodes, edges, false).get('north')?.y, -0.05)
  assert.equal(previewNodePositions(nodes.slice(0, 30), edges).get('north')?.y, -0.05)
})

test('a long branch, coincident point and a through-station preserve source positions', () => {
  const nodes = [node('long', 0, 1.5, 'WORKSTATION'), node('coincident', 1, 0, 'WORKSTATION'),
    node('through', 2, 0.1, 'WORKSTATION'), ...Array.from({ length: 28 }, (_, i) => node(`road-${i}`, i, 0))]
  const edges = [edge('long', 'road-0'), edge('road-1', 'coincident'), edge('through', 'road-2'), edge('through', 'road-3')]
  const preview = previewNodePositions(nodes, edges)
  for (const station of nodes.slice(0, 3)) assert.deepEqual(preview.get(station.id), station)
})
