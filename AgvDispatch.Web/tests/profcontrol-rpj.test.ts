import test from 'node:test';
import assert from 'node:assert/strict';
import { importProfControlRpj } from '../src/profcontrol-rpj.ts';

const rpj = JSON.stringify({
  Global: { Scale: '0.01' },
  ItemsInNet: [{ network: [
    { view: { Type: 'StationView', Center: '(100,200)' }, model: { ID: '"s1"', MarkPointValueMirror: '1' } },
    { view: { Type: 'StationView', Center: '(300,200)' }, model: { ID: '"s2"', MarkPointValueMirror: '2' } },
    { view: { Type: 'StationView', Center: '(500,300)' }, model: { ID: '"s3"', MarkPointValueMirror: '3' } },
    { view: { Type: 'LineView', P1: '(100,200)', P2: '(300,200)', Direction: 'Both', RouteEnable: 'True' },
      model: { ID: '"l1"', AgvSpeed: '1.5' } },
    { view: { Type: 'BezierView', P1: '(300,200)', P2: '(500,300)', C1: '(350,100)', C2: '(450,400)', Direction: 'Both', RouteEnable: 'True' },
      model: { ID: '"b1"', AgvSpeed: '2' } },
    { view: { Type: 'ControlAreaView', Points: '["100,100","500,100","500,400","100,400"]' },
      model: { ID: '"area"', CollisionMaxCapacity: '2' } },
    { view: { Type: 'AgvView' }, model: { Name: 'Agv-1', Width: '1.2', Height: '.8', OnLine: 'True' } },
    { view: { Type: 'AgvView' }, model: { Name: 'Agv-2', Width: '1', Height: '1', OnLine: 'False' } },
    { view: { Type: 'SignalGeneratorView' }, model: { Name: 'calls', SignalCount: '3', Signals: '{"SignalType":[{},{}]}' } },
    { view: { Type: 'PictureView' }, model: {} }
  ] }]
});

test('ProfControl RPJ imports stations, real route geometry, resources and vehicle metadata', () => {
  const result = importProfControlRpj(rpj, 'sample.rpj');
  assert.equal(result.mapDefinition.name, 'sample');
  assert.equal(result.mapDefinition.nodes.length, 3);
  assert.equal(result.mapDefinition.edges.length, 2);
  assert.equal(result.mapDefinition.nodes[0].x, 1);
  assert.equal(result.mapDefinition.nodes[0].y, 2);
  assert.equal(result.mapDefinition.edges[0].speedLimit, 1.5);
  assert.equal(result.mapDefinition.edges[1].geometryKind, 'CubicBezier');
  assert.deepEqual(result.mapDefinition.edges[1].controlPoints, [{ x: 3.5, y: 1 }, { x: 4.5, y: 4 }]);
  assert.equal(result.mapDefinition.resources[0].capacity, 2);
  assert.equal(result.mapDefinition.resources[0].boundary?.length, 4);
  assert.deepEqual(result.sourceVehicles.map(vehicle => vehicle.name), ['Agv-1','Agv-2']);
  assert.deepEqual(result.signalGenerators, [{ name: 'calls', signalCount: 2 }]);
  assert.equal(result.unsupported.PictureView, 1);
  assert.equal(result.unsupported.SignalGeneratorView, 1);
});

test('ProfControl RPJ rejects non-JSON and projects without importable nodes', () => {
  assert.throws(() => importProfControlRpj('not-json'), /有效JSON/);
  assert.throws(() => importProfControlRpj('{"ItemsInNet":[]}'), /没有可导入/);
});
