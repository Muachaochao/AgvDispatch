export interface AlarmAcknowledgeRequest {
  alarmId: string;
  comment: string;
  confirmed: true;
}

export function alarmAcknowledgeError(comment: string, confirmed: boolean) {
  if (!confirmed) return '请先确认：告警确认不等于故障恢复或责任解除。';
  const normalized = comment.trim();
  if (normalized.length < 2) return '请填写至少2个字符的现场核对说明。';
  if (normalized.length > 1000) return '确认说明最多1000个字符。';
  return '';
}

export function alarmAcknowledgeRequest(alarmId: string, comment: string): AlarmAcknowledgeRequest {
  if (!alarmId) throw new Error('缺少告警编号。');
  return { alarmId, comment: comment.trim(), confirmed: true };
}
