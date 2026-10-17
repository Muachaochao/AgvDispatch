import test from 'node:test';
import assert from 'node:assert/strict';
import {commissioningCurrent,type CommissioningReport} from '../src/commissioning.ts';
import type {Snapshot} from '../src/types.ts';
const now=Date.parse('2026-10-17T04:00:00Z');
const snapshot={receivedAt:new Date(now).toISOString(),connected:true,source:'backend',map:{id:'M',version:'V'},status:{service:{startedAt:'2026-10-17T03:00:00Z'},execution:{buildId:'build-A'}}} as Snapshot;
const report={generatedAt:new Date(now).toISOString(),runtime:{buildId:'build-A'},serviceStartedAt:'2026-10-17T03:00:00Z',mapId:'M',mapVersion:'V',checks:[]} as unknown as CommissioningReport;
test('联调观察过期、断连及未来时间不沿用当前通过',()=>{
 assert.equal(commissioningCurrent(report,snapshot,now),true);
 assert.equal(commissioningCurrent(report,{...snapshot,connected:false},now),false);
 assert.equal(commissioningCurrent(report,snapshot,now+16000),false);
 assert.equal(commissioningCurrent({...report,generatedAt:'invalid'},snapshot,now),false);
 assert.equal(commissioningCurrent({...report,generatedAt:new Date(now+2000).toISOString()},snapshot,now),false);
});
test('同版本号后台重启、构建改变或地图切换均撤销联调观察',()=>{
 assert.equal(commissioningCurrent(report,{...snapshot,status:{...snapshot.status!,service:{version:'1.0.0.0',startedAt:'2026-10-17T03:59:00Z'}}},now),false);
 assert.equal(commissioningCurrent({...report,runtime:{...report.runtime,buildId:'build-B'}},snapshot,now),false);
 assert.equal(commissioningCurrent({...report,mapVersion:'V2'},snapshot,now),false);
 assert.equal(commissioningCurrent({...report,checks:[{id:'map-changed'} as any]},snapshot,now),false);
 assert.equal(commissioningCurrent(report,{...snapshot,status:{...snapshot.status!,execution:undefined}},now),false);
});
