import type { ProgressCondition } from './types.ts';

export function diagnosticFresh(connected: boolean, observedAt: string | undefined, now: number): boolean {
  const age = now - Date.parse(observedAt ?? '');
  return connected && Number.isFinite(age) && age >= -1000 && age <= 10000;
}

export function conditionLabel(condition: ProgressCondition, fresh: boolean): string {
  if (!fresh) return '观察已过期';
  if (condition.quality !== 'Good' || condition.currentValue === null) return '未知';
  if (condition.role.includes('待人工核对')) return condition.satisfied ? '回读符合，仍待人工核对' : '待人工核对';
  if (condition.role === '复位目标') return condition.satisfied ? '符合目标' : '待复位';
  return condition.result === 'Pass' ? '满足' : condition.result === 'Wait' ? '等待' : '未知';
}

export function boolText(value: boolean | null | undefined): string {
  return value === true ? '1' : value === false ? '0' : '未知';
}

export function stepStatusLabel(status: string, fallback: string): string {
  return status === 'Ready' ? '待协调执行' : fallback;
}
