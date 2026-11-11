import type {Snapshot, TaskRow, TrafficBlocker, TrafficRequest} from './types.ts';
import {isFresh} from './state.ts';

export function resourceHandovers(snapshot:Snapshot|null, request:TrafficRequest|null){
 if(!snapshot?.map||!request||request.mapId!==snapshot.map.id||request.mapVersionId!==snapshot.map.version)return [];
 const ids=new Set(request.resourceIds.map(id=>id.toLowerCase()));
 const identity=(task:string,step:string,vehicle:string)=>JSON.stringify([task,step,vehicle]);
 const owners=new Set((snapshot.traffic?.requests??[]).filter(r=>r.mapId===request.mapId&&r.mapVersionId===request.mapVersionId)
  .map(r=>identity(r.taskId,r.stepId,r.vehicleId)));
 return (snapshot.traffic?.events??[]).filter(e=>['ResourceAcquired','ResourceReleased','SegmentReleased'].includes(e.kind)&&
  owners.has(identity(e.taskId,e.stepId,e.vehicleId))&&e.resourceIds.some(id=>ids.has(id.toLowerCase())))
  .map(e=>({...e,resourceIds:e.resourceIds.filter(id=>ids.has(id.toLowerCase()))}))
  .sort((a,b)=>Date.parse(b.occurredAt)-Date.parse(a.occurredAt));
}

// Display evidence only: never change task stage, occupancy counts or release policy.
export function taskWait(s:Snapshot|null,t:TaskRow,now:number){
 const age=now-Date.parse(s?.traffic?.capturedAt??'');
 if(!isFresh(s,now)||!s?.map||!Number.isFinite(age)||age< -1000||age>7000||
   ['Completed','Cancelled','Faulted','CancellationPending','CancellationReview'].includes(t.stage))return null;
 const current=s.tasks?.items.find(x=>x.taskId===t.taskId);
 if(!current||current.stage!==t.stage||current.vehicleId!==t.vehicleId)return null;
 const rows=s.traffic?.requests.filter(r=>r.taskId===t.taskId&&r.vehicleId===t.vehicleId&&r.state==='Waiting'&&
   r.mapId===s.map!.id&&r.mapVersionId===s.map!.version)??[];
 if(!rows.length)return null;
 return {label:rows.every(r=>r.waitingCategory==='Equipment')?'倒料设备许可等待':rows.every(r=>r.waitingCategory!=='Equipment')?'路权／候车位等待':'多项条件等待',
   reason:[...new Set(rows.map(r=>r.reason).filter(Boolean))].join('；'),count:rows.length,truncated:s.traffic?.requestsTruncated??false};
}

export function leaseOwners(leases:{taskId:string;vehicleId:string}[]){
 const groups=new Map<string,{taskId:string;vehicleId:string;leaseCount:number}>();
 for(const l of leases){const key=JSON.stringify([l.taskId,l.vehicleId]);const g=groups.get(key);
  if(g)g.leaseCount++;else groups.set(key,{...l,leaseCount:1});}
 return [...groups.values()];
}

export function blockerOwners(blockers:TrafficBlocker[]){
 const groups=new Map<string,TrafficBlocker & {stepIds:string[];records:TrafficBlocker[]}>();
 for(const b of blockers){const key=JSON.stringify([b.resourceId,b.mapId,b.mapVersionId,b.taskId,b.vehicleId]);const g=groups.get(key);
  if(g){g.heartbeatExpired ||= b.heartbeatExpired;if(!g.stepIds.includes(b.stepId))g.stepIds.push(b.stepId);g.records.push(b);}
  else groups.set(key,{...b,stepIds:[b.stepId],records:[b]});}
 return [...groups.values()];
}
