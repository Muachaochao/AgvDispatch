import type { Alarm, AlarmListSummary, AlarmPage } from './types.ts';
import { taskTimeRange, type TaskTimePreset } from './task-history.ts';

export type AlarmHistoryQuery = {
  search: string;
  status: string;
  severity: string;
  from: string;
  to: string;
  pageSize: number;
};
export type AlarmTimePreset = TaskTimePreset;

export const emptyAlarmHistoryQuery = (): AlarmHistoryQuery => ({
  search: '', status: 'all', severity: 'all', from: '', to: '', pageSize: 50
});

export const alarmTimeRange = (preset: AlarmTimePreset, now = new Date()) =>
  taskTimeRange(preset, now);

export function alarmHistoryError(query: AlarmHistoryQuery) {
  if (!['all', 'open', 'acknowledged', 'recovered'].includes(query.status))
    return '请选择有效告警状态。';
  if (!['all', 'info', 'warning', 'error', 'critical'].includes(query.severity))
    return '请选择有效告警级别。';
  if (![20, 50, 100, 200].includes(query.pageSize))
    return '请选择每页20、50、100或200条。';
  if (query.search.trim().length > 128)
    return '关键词最多128字符。';
  const from = query.from ? Date.parse(query.from) : null;
  const to = query.to ? Date.parse(query.to) : null;
  if ((from !== null && !Number.isFinite(from)) || (to !== null && !Number.isFinite(to)))
    return '请输入有效的首次发生时间。';
  if (from !== null && to !== null && from >= to)
    return '开始时间必须早于结束时间。';
  return '';
}

export function alarmHistoryUrl(query: AlarmHistoryQuery, page: number) {
  const error = alarmHistoryError(query);
  if (error) throw new Error(error);
  if (!Number.isInteger(page) || page < 1) throw new Error('告警页码无效。');
  const params = new URLSearchParams({
    search: query.search.trim(),
    status: query.status,
    severity: query.severity,
    page: String(page),
    pageSize: String(query.pageSize)
  });
  if (query.from) params.set('from', new Date(query.from).toISOString());
  if (query.to) params.set('to', new Date(query.to).toISOString());
  return '/api/alarms?' + params.toString();
}

export function countAlarmSummary(alarms: Alarm[]): AlarmListSummary {
  return {
    total: alarms.length,
    open: alarms.filter(item => item.status === 'Open').length,
    acknowledged: alarms.filter(item => item.status === 'Acknowledged').length,
    recovered: alarms.filter(item => item.status === 'Recovered').length,
    critical: alarms.filter(item => item.severity === 'Critical').length
  };
}

const severityRank: Record<string, number> = { Info: 0, Warning: 1, Error: 2, Critical: 3 };
const statusValue: Record<string, string> = {
  open: 'Open', acknowledged: 'Acknowledged', recovered: 'Recovered'
};
const severityValue: Record<string, string> = {
  info: 'Info', warning: 'Warning', error: 'Error', critical: 'Critical'
};

export function demoAlarmPage(alarms: Alarm[], query: AlarmHistoryQuery, page: number): AlarmPage {
  const error = alarmHistoryError(query);
  if (error) throw new Error(error);
  if (!Number.isInteger(page) || page < 1) throw new Error('告警页码无效。');
  const from = query.from ? Date.parse(query.from) : -Infinity;
  const to = query.to ? Date.parse(query.to) : Infinity;
  const search = query.search.trim().toLowerCase();
  const matched = alarms.filter(item => {
    const occurred = Date.parse(item.firstOccurredAt);
    return occurred >= from && occurred < to &&
      (!search || `${item.alarmId} ${item.ruleKey ?? ''} ${item.sourceId} ${item.code} ${item.title} ${item.message}`.toLowerCase().includes(search));
  });
  const summary = countAlarmSummary(matched);
  const filtered = matched.filter(item =>
    (query.status === 'all' || item.status === statusValue[query.status]) &&
    (query.severity === 'all' || item.severity === severityValue[query.severity]))
    .sort((a, b) => Date.parse(b.lastObservedAt) - Date.parse(a.lastObservedAt) ||
      (severityRank[b.severity] ?? -1) - (severityRank[a.severity] ?? -1));
  return {
    items: filtered.slice((page - 1) * query.pageSize, page * query.pageSize),
    totalCount: filtered.length,
    page,
    pageSize: query.pageSize,
    summary
  };
}
