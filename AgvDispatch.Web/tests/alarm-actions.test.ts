import assert from 'node:assert/strict';
import test from 'node:test';
import { alarmAcknowledgeError, alarmAcknowledgeRequest } from '../src/alarm-actions.ts';

test('告警确认必须同时具备确认勾选和说明', () => {
  assert.match(alarmAcknowledgeError('已核对现场', false), /请先确认/);
  assert.match(alarmAcknowledgeError(' ', true), /至少2个字符/);
  assert.equal(alarmAcknowledgeError(' 已联系现场处理 ', true), '');
});

test('告警确认请求固定携带当前告警编号并规范化说明', () => {
  assert.deepEqual(alarmAcknowledgeRequest('ALM-1', '  已联系现场处理  '), {
    alarmId: 'ALM-1',
    comment: '已联系现场处理',
    confirmed: true
  });
  assert.throws(() => alarmAcknowledgeRequest('', '已联系现场处理'), /缺少告警编号/);
});
