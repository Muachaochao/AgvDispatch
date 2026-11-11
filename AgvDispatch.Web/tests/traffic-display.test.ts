import test from 'node:test';
import assert from 'node:assert/strict';
import {taskWait,leaseOwners,blockerOwners,resourceHandovers} from '../src/traffic-display.ts';
import {rehearsalSnapshot} from '../src/traffic-rehearsal.ts';
test('资源交接只取同图精确资源，并保留实际占用车辆和时间',()=>{
 const s=rehearsalSnapshot(0),r=s.traffic!.requests[1]!;
 r.resourceIds=['RA'];r.mapId=s.map!.id;r.mapVersionId=s.map!.version;
 const owner={...r,taskId:'OWNER',stepId:'OWNER-S',vehicleId:'OLD'};
 s.traffic!.requests.push(owner);
 const event={eventId:'RELEASE',taskId:owner.taskId,stepId:owner.stepId,vehicleId:owner.vehicleId,
  kind:'ResourceReleased',reason:'verified',resourceIds:['RA','OTHER'],occurredAt:new Date().toISOString()};
 s.traffic!.events=[event,{...event,eventId:'PREFIX',resourceIds:['RA-OTHER']},
  {...event,eventId:'MISSING-OWNER',stepId:'UNKNOWN'}];
 const before=JSON.stringify(s);const result=resourceHandovers(s,r);
 assert.equal(result.length,1);assert.equal(result[0]!.vehicleId,'OLD');assert.deepEqual(result[0]!.resourceIds,['RA']);
 assert.equal(JSON.stringify(s),before);
 owner.mapVersionId='OTHER';assert.equal(resourceHandovers(s,r).length,0);
});
function fixture(){const s=rehearsalSnapshot(0),r=s.traffic!.requests.find(r=>r.state==='Waiting')!;const t=s.tasks!.items.find(t=>t.taskId===r.taskId)!;return {s,r,t,now:Date.parse(s.receivedAt)};}
test('候车原因覆盖显示而不改业务阶段和原始快照',()=>{const {s,t,now}=fixture(),before=JSON.stringify(s);assert.equal(taskWait(s,t,now)?.label,'路权／候车位等待');assert.equal(JSON.stringify(s),before);});
test('设备与混合等待及截断分别说明',()=>{const {s,r,t,now}=fixture();r.waitingCategory='Equipment';assert.equal(taskWait(s,t,now)?.label,'倒料设备许可等待');s.traffic!.requests.push({...r,stepId:'OTHER',waitingCategory:'Route',reason:'候车位被占用'});s.traffic!.requestsTruncated=true;const w=taskWait(s,t,now)!;assert.equal(w.label,'多项条件等待');assert.equal(w.count,2);assert.equal(w.truncated,true);assert.match(w.reason,/候车位/);});
test('断连、超时、未来时钟及异图不覆盖',()=>{for(const change of [(s:any)=>s.connected=false,(s:any)=>s.traffic.capturedAt='invalid',(s:any)=>s.traffic.capturedAt=new Date(Date.parse(s.receivedAt)-8000).toISOString(),(s:any)=>s.traffic.capturedAt=new Date(Date.parse(s.receivedAt)+2000).toISOString(),(s:any)=>s.map.version='OTHER']){const {s,t,now}=fixture();change(s);assert.equal(taskWait(s,t,now),null);}});
test('取消故障终态优先，翻页旧任务及车辆不一致不贴等待',()=>{const {s,t,now}=fixture();for(const stage of ['Completed','Cancelled','Faulted','CancellationPending','CancellationReview'])assert.equal(taskWait(s,{...t,stage},now),null);assert.equal(taskWait(s,{...t,vehicleId:'OTHER'},now),null);assert.equal(taskWait(s,{...t,stage:'OTHER'},now),null);assert.equal(taskWait(s,{...t,taskId:'OTHER'},now),null);});
test('等待结束及缺观察不留旧提示',()=>{const {s,r,t,now}=fixture();r.state='ObservedReady';assert.equal(taskWait(s,t,now),null);s.traffic=null;assert.equal(taskWait(s,t,now),null);});
test('同任务多租约合并，异任务同车保留两个归属',()=>{const leases=[{taskId:'T1',vehicleId:'V'},{taskId:'T1',vehicleId:'V'},{taskId:'T2',vehicleId:'V'}],before=JSON.stringify(leases);assert.deepEqual(leaseOwners(leases).map(x=>x.leaseCount),[2,1]);assert.equal(JSON.stringify(leases),before);});
test('阻塞汇总保留步骤和过期标记，不合并跨图跨任务',()=>{const {r}=fixture(),b=r.blockers[0]!;const input=[b,{...b,stepId:'SECOND',heartbeatExpired:true},{...b,taskId:'OTHER'},{...b,mapVersionId:'OTHER'}],before=JSON.stringify(input);const g=blockerOwners(input);assert.equal(g.length,3);assert.equal(g[0]!.stepIds.length,2);assert.equal(g[0]!.heartbeatExpired,true);assert.equal(g[0]!.records.length,2);assert.equal(JSON.stringify(input),before);});
