import type { TaskListSummary, TaskPage, TaskRow } from './types.ts';
import { isRunningStage } from './state.ts';

export type TaskHistoryQuery = { search: string; filter: string; from: string; to: string; pageSize: number };
export type TaskTimePreset = 'today' | 'yesterday' | 'week' | 'all';
export const emptyTaskHistoryQuery = (): TaskHistoryQuery => ({ search: '', filter: 'all', from: '', to: '', pageSize: 50 });
export function localDateTime(date: Date) {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}T${p(date.getHours())}:${p(date.getMinutes())}`;
}
export function taskTimeRange(preset: TaskTimePreset, now = new Date()) {
  if (preset === 'all') return { from: '', to: '' };
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const end = new Date(start); end.setDate(end.getDate() + 1);
  if (preset === 'yesterday') { start.setDate(start.getDate() - 1); end.setDate(end.getDate() - 1); }
  if (preset === 'week') start.setDate(start.getDate() - 6);
  return { from: localDateTime(start), to: localDateTime(end) };
}
export function taskHistoryError(query: TaskHistoryQuery) {
  if (!['all', 'waiting', 'running', 'failed', 'completed', 'cancelled'].includes(query.filter)) return '请选择有效任务状态。';
  if (![20, 50, 100, 200].includes(query.pageSize)) return '请选择每页20、50、100或200条。';
  if (query.search.length > 128) return '关键词最多128字符。';
  const from = query.from ? Date.parse(query.from) : null, to = query.to ? Date.parse(query.to) : null;
  if ((from !== null && !Number.isFinite(from)) || (to !== null && !Number.isFinite(to))) return '请输入有效的创建时间。';
  if (from !== null && to !== null && from >= to) return '开始时间必须早于结束时间。';
  return '';
}
export function taskHistoryUrl(query: TaskHistoryQuery, page: number) {
  const error = taskHistoryError(query); if (error) throw new Error(error);
  if (!Number.isInteger(page) || page < 1) throw new Error('任务页码无效。');
  const filters: Record<string, string> = { all: 'All', waiting: 'Waiting', running: 'Running', failed: 'Failed', completed: 'Completed', cancelled: 'Cancelled' };
  const params = new URLSearchParams({ filter: filters[query.filter]!, search: query.search.trim(), page: String(page), pageSize: String(query.pageSize) });
  if (query.from) params.set('from', new Date(query.from).toISOString());
  if (query.to) params.set('to', new Date(query.to).toISOString());
  return '/api/tasks?' + params.toString();
}
export function countTaskStages(items: TaskRow[]): TaskListSummary {
  const summary: TaskListSummary = { total: items.length, running: 0, waiting: 0, completed: 0, cancelled: 0, failed: 0, other: 0 };
  for (const task of items) {
    if (task.stage === 'Completed') summary.completed++;
    else if (task.stage === 'Cancelled') summary.cancelled++;
    else if (['Faulted', 'CancellationPending', 'CancellationReview'].includes(task.stage)) summary.failed++;
    else if (['Created', 'WaitingForVehicle'].includes(task.stage)) summary.waiting++;
    else if (isRunningStage(task.stage) || task.stage === 'Assigned') summary.running++;
    else summary.other++;
  }
  return summary;
}
export function completeTaskSummary(page: Pick<TaskPage, 'items' | 'totalCount' | 'summary'> | null | undefined): TaskListSummary | null {
  if (!page) return null;
  const s = page.summary;
  if (s && ['total', 'running', 'waiting', 'completed', 'cancelled', 'failed', 'other'].every(key => Number.isInteger(s[key as keyof TaskListSummary]) && s[key as keyof TaskListSummary] >= 0) &&
    s.total === page.totalCount && s.running + s.waiting + s.completed + s.cancelled + s.failed + s.other === s.total) return s;
  // Older backends/demo fixtures can be counted only when the entire result is present.
  return page.items.length === page.totalCount ? countTaskStages(page.items) : null;
}
export function taskStageSegments(summary: TaskListSummary | null) {
  const s = summary ?? { running: 0, waiting: 0, completed: 0, cancelled: 0, failed: 0, other: 0 };
  return [
    { label: '执行', value: s.running, color: '#31c6e6' },
    { label: '待派车', value: s.waiting, color: '#e9b34f' },
    { label: '完成', value: s.completed, color: '#25d3a4' },
    { label: '已取消', value: s.cancelled, color: '#8297b8' },
    { label: '异常', value: s.failed, color: '#ee668b' },
    ...(s.other ? [{ label: '其他', value: s.other, color: '#b48ae8' }] : [])
  ];
}
export function demoTaskPage(items: TaskRow[], query: TaskHistoryQuery, page: number): TaskPage {
  const from = query.from ? Date.parse(query.from) : -Infinity, to = query.to ? Date.parse(query.to) : Infinity;
  const matched = items.filter(t => {
    const time = Date.parse(t.createdAt);
    const stage = query.filter === 'all' || (query.filter === 'completed' ? t.stage === 'Completed' : query.filter === 'cancelled' ? t.stage === 'Cancelled' :
      query.filter === 'failed' ? ['Faulted', 'CancellationPending', 'CancellationReview'].includes(t.stage) : query.filter === 'waiting' ? ['Created', 'WaitingForVehicle'].includes(t.stage) : isRunningStage(t.stage));
    return stage && time >= from && time < to && `${t.taskId} ${t.stationId} ${t.vehicleId ?? ''}`.toLowerCase().includes(query.search.trim().toLowerCase());
  });
  return { items: matched.slice((page - 1) * query.pageSize, page * query.pageSize), totalCount: matched.length, page, pageSize: query.pageSize, summary: countTaskStages(matched) };
}
