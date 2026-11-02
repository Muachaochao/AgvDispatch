import test from 'node:test';
import assert from 'node:assert/strict';
import { findChargingRequestSession, readChargingRequestIntent } from '../src/charging-request.ts';

const intent = { requestId: '7d9b60b2-9399-49ed-bacc-4dc7049e5c42', vehicleId: 'AGV-01', requiredPositiveSeconds: 60, returnSiteId: 'LM44' };

test('重载后恢复原申请的完整ID、车辆、连续时长与返程点，不生成新申请', () => {
  assert.deepEqual(readChargingRequestIntent(JSON.stringify(intent)), intent);
  assert.deepEqual(readChargingRequestIntent(JSON.stringify({ ...intent, requiredPositiveSeconds: null, returnSiteId: null })),
    { ...intent, requiredPositiveSeconds: null, returnSiteId: null });
});

test('损坏或无效的原申请不能变成默认参数发送', () => {
  for (const raw of [null, '{', 'null', '{}', JSON.stringify({ ...intent, requestId: 'invalid' }),
    JSON.stringify({ ...intent, vehicleId: '' }), JSON.stringify({ ...intent, requiredPositiveSeconds: -1 }),
    JSON.stringify({ ...intent, requiredPositiveSeconds: '60' }), JSON.stringify({ ...intent, returnSiteId: '' })])
    assert.equal(readChargingRequestIntent(raw), undefined);
});

test('查询只能用唯一原requestId核对受理，不能以同车其他会话代替', () => {
  const original = { requestId: intent.requestId, vehicleId: 'AGV-01', phase: 'WaitingForBusiness' };
  const other = { ...original, requestId: 'another-request' };
  assert.equal(findChargingRequestSession(intent.requestId, [other]), undefined);
  assert.equal(findChargingRequestSession(intent.requestId, [original, other]), original);
  assert.equal(findChargingRequestSession(intent.requestId, [original, original]), undefined);
});
