import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { vehicleCategory, filterAlarms, shortTime, alarmSeverity, alarmStatus } from '../src/dashboard.ts';
import type { Vehicle, Alarm, TaskRow } from '../src/types.ts';

function makeVehicle(overrides: Partial<Vehicle> = {}): Vehicle {
  return {
    vehicleId: 'AGV-01', displayName: 'Test', connection: 'Online', quality: 'Good',
    batteryPercent: 80, currentStation: 'P1', navigationState: '空闲',
    x: 1, y: 2, angle: 0, localizationConfidence: 0.9,
    mapPositionState: 'Normal', mapPositionMessage: '', lastSuccessfulPollAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(), blocked: false, emergencyStopped: false, ...overrides
  };
}

describe('vehicleCategory', () => {
  it('returns offline when not current', () => {
    assert.equal(vehicleCategory(makeVehicle(), [], false), 'offline');
  });

  it('returns offline when disconnected', () => {
    assert.equal(vehicleCategory(makeVehicle({ connection: 'Offline' }), [], true), 'offline');
  });

  it('returns fault when emergency stopped', () => {
    assert.equal(vehicleCategory(makeVehicle({ emergencyStopped: true }), [], true), 'fault');
  });

  it('returns charging when charging', () => {
    assert.equal(vehicleCategory(makeVehicle({ charging: true }), [], true), 'charging');
  });

  it('returns running when has executing task', () => {
    const tasks: TaskRow[] = [{ taskId: 'T1', stationId: 'S1', vehicleId: 'AGV-01', stage: 'Executing', priority: 50, createdAt: '', updatedAt: '' }];
    assert.equal(vehicleCategory(makeVehicle(), tasks, true), 'running');
  });

  it('returns idle when navigation idle', () => {
    assert.equal(vehicleCategory(makeVehicle({ navigationState: '空闲' }), [], true), 'idle');
  });
});

describe('filterAlarms', () => {
  const alarms: Alarm[] = [
    { alarmId: 'A1', sourceType: 'Task', sourceId: 'T1', code: 'ERR1', title: '超时告警', message: 'test', severity: 'Warning', status: 'Open', firstOccurredAt: '', lastObservedAt: '', acknowledgedAt: null, recoveredAt: null },
    { alarmId: 'A2', sourceType: 'Vehicle', sourceId: 'V1', code: 'ERR2', title: '通信异常', message: 'test2', severity: 'Error', status: 'Recovered', firstOccurredAt: '', lastObservedAt: '', acknowledgedAt: null, recoveredAt: '' }
  ];

  it('returns all when no filters', () => {
    assert.equal(filterAlarms(alarms, '', 'all', 'all').length, 2);
  });

  it('filters by status', () => {
    assert.equal(filterAlarms(alarms, '', 'Open', 'all').length, 1);
    assert.equal(filterAlarms(alarms, '', 'Open', 'all')[0].alarmId, 'A1');
  });

  it('filters by severity', () => {
    assert.equal(filterAlarms(alarms, '', 'all', 'Error').length, 1);
  });

  it('filters by search text', () => {
    assert.equal(filterAlarms(alarms, '超时', 'all', 'all').length, 1);
  });
});

describe('shortTime', () => {
  it('returns dash for null/undefined', () => {
    assert.equal(shortTime(null), '—');
    assert.equal(shortTime(undefined), '—');
  });

  it('returns formatted string for valid date', () => {
    const result = shortTime('2026-10-17T10:30:00Z');
    assert.ok(result.includes('2026'));
  });
});

describe('alarmSeverity / alarmStatus', () => {
  it('translates severity', () => {
    assert.equal(alarmSeverity('Warning'), '警告');
    assert.equal(alarmSeverity('Critical'), '严重');
    assert.equal(alarmSeverity('Unknown'), 'Unknown');
  });

  it('translates status', () => {
    assert.equal(alarmStatus('Open'), '未确认');
    assert.equal(alarmStatus('Recovered'), '已恢复');
  });
});
