import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { validateDefinition, emptyPlan, parsePlan, routePlan, validateVehicles } from '../src/workbench.ts';

describe('validateDefinition', () => {
  it('passes for empty valid definition', () => {
    const d = emptyPlan().definition;
    assert.equal(validateDefinition(d).length, 0);
  });

  it('fails when missing nodes array', () => {
    assert.ok(validateDefinition({} as any).length > 0);
  });

  it('fails for duplicate node ids', () => {
    const d = emptyPlan().definition;
    d.nodes = [
      { id: 'P1', x: 0, y: 0, type: 'Waypoint', enabled: true, resourceIds: [], agvSiteId: null },
      { id: 'P1', x: 1, y: 1, type: 'Waypoint', enabled: true, resourceIds: [], agvSiteId: null }
    ];
    assert.ok(validateDefinition(d).length > 0);
  });

  it('fails for invalid edge endpoints', () => {
    const d = emptyPlan().definition;
    d.nodes = [{ id: 'P1', x: 0, y: 0, type: 'Waypoint', enabled: true, resourceIds: [], agvSiteId: null }];
    d.edges = [{ id: 'E1', from: 'P1', to: 'MISSING', distance: 1, speedLimit: 0.5, enabled: true, bidirectional: false, resourceIds: [], geometryKind: 'Line' }];
    assert.ok(validateDefinition(d).length > 0);
  });
});

describe('parsePlan', () => {
  it('round-trips an empty plan', () => {
    const plan = emptyPlan();
    const parsed = parsePlan(JSON.stringify(plan));
    assert.equal(parsed.schema, 'agv-web-plan');
    assert.equal(parsed.version, 1);
  });

  it('rejects oversized input', () => {
    assert.throws(() => parsePlan('x'.repeat(9 * 1024 * 1024)));
  });
});

describe('routePlan', () => {
  it('returns error for less than 2 waypoints', () => {
    const d = emptyPlan().definition;
    assert.ok(routePlan(d, ['P1']).error.length > 0);
  });

  it('finds route between two connected nodes', () => {
    const d = emptyPlan().definition;
    d.nodes = [
      { id: 'A', x: 0, y: 0, type: 'Waypoint', enabled: true, resourceIds: [], agvSiteId: null },
      { id: 'B', x: 5, y: 0, type: 'Waypoint', enabled: true, resourceIds: [], agvSiteId: null }
    ];
    d.edges = [{ id: 'E1', from: 'A', to: 'B', distance: 5, speedLimit: 0.5, enabled: true, bidirectional: false, resourceIds: [], geometryKind: 'Line' }];
    const result = routePlan(d, ['A', 'B']);
    assert.equal(result.error, '');
    assert.equal(result.distance, 5);
    assert.deepEqual(result.edges, ['E1']);
  });

  it('reports unreachable nodes', () => {
    const d = emptyPlan().definition;
    d.nodes = [
      { id: 'A', x: 0, y: 0, type: 'Waypoint', enabled: true, resourceIds: [], agvSiteId: null },
      { id: 'B', x: 5, y: 0, type: 'Waypoint', enabled: true, resourceIds: [], agvSiteId: null }
    ];
    d.edges = [];
    assert.ok(routePlan(d, ['A', 'B']).error.includes('不可达'));
  });
});

describe('validateVehicles', () => {
  it('passes for empty list', () => {
    assert.equal(validateVehicles([]).length, 0);
  });

  it('fails for duplicate vehicle ids', () => {
    const v = { vehicleId: 'AGV-01', displayName: 'V1', ipAddress: '192.168.1.1', port: 19204, navigationPort: 19206, enabled: true };
    assert.ok(validateVehicles([v, { ...v }]).length > 0);
  });

  it('fails for invalid IP', () => {
    const v = { vehicleId: 'AGV-01', displayName: 'V1', ipAddress: 'not-an-ip', port: 19204, navigationPort: 19206, enabled: true };
    assert.ok(validateVehicles([v]).length > 0);
  });
});
