import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  alarmHistoryError,
  alarmHistoryUrl,
  countAlarmSummary,
  demoAlarmPage,
  emptyAlarmHistoryQuery
} from '../src/alarm-history.ts';
import type { Alarm } from '../src/types.ts';

function alarm(overrides: Partial<Alarm>): Alarm {
  return {
    alarmId: 'ALM-1',
    ruleKey: 'rule.vehicle',
    sourceType: 'Vehicle',
    sourceId: 'AGV-01',
    code: 'V001',
    title: '车辆通信异常',
    message: '车辆连接中断',
    severity: 'Warning',
    status: 'Open',
    firstOccurredAt: '2026-10-30T08:00:00+08:00',
    lastObservedAt: '2026-10-30T08:05:00+08:00',
    acknowledgedAt: null,
    recoveredAt: null,
    ...overrides
  };
}

const alarms: Alarm[] = [
  alarm({ alarmId: 'ALM-OPEN', sourceId: 'AGV-01', severity: 'Warning', status: 'Open' }),
  alarm({ alarmId: 'ALM-CRITICAL', sourceId: 'AGV-02', title: '急停触发', severity: 'Critical', status: 'Acknowledged',
    acknowledgedAt: '2026-10-30T08:10:00+08:00', lastObservedAt: '2026-10-30T08:20:00+08:00' }),
  alarm({ alarmId: 'ALM-RECOVERED', sourceId: 'PLC-01', sourceType: 'Plc', title: 'PLC 通信恢复', severity: 'Info', status: 'Recovered',
    firstOccurredAt: '2026-10-29T08:00:00+08:00', lastObservedAt: '2026-10-29T08:20:00+08:00', recoveredAt: '2026-10-29T08:20:00+08:00' }),
  alarm({ alarmId: 'ALM-OLD', sourceId: 'AGV-03', title: '历史路径占用', severity: 'Error', status: 'Recovered',
    firstOccurredAt: '2026-10-20T08:00:00+08:00', lastObservedAt: '2026-10-20T08:20:00+08:00', recoveredAt: '2026-10-20T08:20:00+08:00' })
];

describe('alarm history query', () => {
  it('validates time range, filters and page size', () => {
    assert.match(alarmHistoryError({ ...emptyAlarmHistoryQuery(), status: 'unknown' }), /状态/);
    assert.match(alarmHistoryError({ ...emptyAlarmHistoryQuery(), severity: 'fatal' }), /级别/);
    assert.match(alarmHistoryError({ ...emptyAlarmHistoryQuery(), pageSize: 10 }), /每页/);
    assert.match(alarmHistoryError({
      ...emptyAlarmHistoryQuery(),
      from: '2026-10-30T10:00',
      to: '2026-10-30T09:00'
    }), /开始时间/);
  });

  it('builds a server query with time bounds and applied filters', () => {
    const url = alarmHistoryUrl({
      search: 'AGV-02',
      status: 'acknowledged',
      severity: 'critical',
      from: '2026-10-30T08:00',
      to: '2026-10-30T09:00',
      pageSize: 100
    }, 3);
    assert.match(url, /^\/api\/alarms\?/);
    assert.match(url, /search=AGV-02/);
    assert.match(url, /status=acknowledged/);
    assert.match(url, /severity=critical/);
    assert.match(url, /page=3/);
    assert.match(url, /pageSize=100/);
    assert.match(url, /from=2026-10-30T00%3A00%3A00.000Z/);
    assert.match(url, /to=2026-10-30T01%3A00%3A00.000Z/);
  });

  it('keeps summary independent from list status and severity filters', () => {
    const page = demoAlarmPage(alarms, {
      search: '',
      status: 'acknowledged',
      severity: 'critical',
      from: '',
      to: '',
      pageSize: 50
    }, 1);
    assert.deepEqual(page.summary, { total: 4, open: 1, acknowledged: 1, recovered: 2, critical: 1 });
    assert.equal(page.totalCount, 1);
    assert.equal(page.items[0].alarmId, 'ALM-CRITICAL');
  });

  it('filters by first occurrence time and paginates newest observations first', () => {
    const page = demoAlarmPage(alarms, {
      search: 'AGV',
      status: 'all',
      severity: 'all',
      from: '2026-10-30T00:00',
      to: '2026-10-31T00:00',
      pageSize: 20
    }, 1);
    assert.equal(page.summary.total, 2);
    assert.deepEqual(page.items.map(item => item.alarmId), ['ALM-CRITICAL', 'ALM-OPEN']);
    assert.equal(page.totalCount, 2);
    assert.equal(page.page, 1);
    assert.equal(page.pageSize, 20);
  });

  it('counts all alarm lifecycle states', () => {
    assert.deepEqual(countAlarmSummary(alarms), {
      total: 4,
      open: 1,
      acknowledged: 1,
      recovered: 2,
      critical: 1
    });
  });
});
