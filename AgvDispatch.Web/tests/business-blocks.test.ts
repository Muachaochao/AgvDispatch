import { test } from 'node:test';
import assert from 'node:assert/strict';
import { businessVehicleBlock, summarizeBusinessBlocks } from '../src/business-blocks.ts';
test('正常健康说明不当作车辆故障或无呼叫时的业务启动阻塞', () => {
  assert.equal(businessVehicleBlock({vehicleId:'AGV-01',available:false,explanation:'在线空闲，定位正常。'}),'');
  assert.match(businessVehicleBlock({vehicleId:'AGV-01',available:false,explanation:'车辆离线'}),/车辆离线/);
  assert.match(businessVehicleBlock({vehicleId:'AGV-01',available:false,explanation:'28阶段出区配置缺失'}),/出区配置缺失/);
});
test('重复写入门禁合并，保留涉及工位与数量', () => {
  const text = (id: string) => `第 1 步（前往取货点）：PLC ${id}/Ack：所属 PLC 写入开关未开启；第 9 步（结束 PLC 握手）：PLC ${id}/Ack：所属 PLC 写入开关未开启`;
  assert.deepEqual(summarizeBusinessBlocks(['CNC-001','CNC-002'].map(stationId=>({stationId,explanation:text(stationId)}))),[
    {reason:'PLC 写入权限未开启',stations:['CNC-001','CNC-002'],count:4,permission:true}
  ]);
});
test('真正缺失点位、出区配置和未知阻塞不归并为权限问题', () => {
  const result=summarizeBusinessBlocks([{stationId:'CNC-001',explanation:'第 1 步（取货）：PLC CNC-001/Ack：点位未配置；缺少 DumperExit.BodyProfiles；未知配置问题'}]);
  assert.equal(result.length,3);assert.ok(result.every(g=>!g.permission));assert.ok(result[0].reason.includes('点位未配置'));
});
