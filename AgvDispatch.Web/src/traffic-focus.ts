import type {Snapshot,TaskDetail} from './types.ts';
import {isFresh,mayPlot} from './state.ts';
import {diagnosticFresh} from './task-diagnostics.ts';
import {taskRoute} from './task-route.ts';
export interface TrafficFocus {taskId:string;vehicleId:string;edgeIds:string[];held:string[];blocked:string[];blockers:string[];unplotted:string[];reason:string;truncated:boolean;routeMode:'Current'|'Historical'|'None';terminal:boolean}
export function trafficFocus(s:Snapshot|null,d:TaskDetail|null,now:number,allowDemo=false):TrafficFocus {
 const empty:TrafficFocus={taskId:'',vehicleId:'',edgeIds:[],held:[],blocked:[],blockers:[],unplotted:[],reason:'选择任务后联动；需有效的同图后台观察',truncated:false,routeMode:'None',terminal:false};
 if(!s?.map||!d||!isFresh(s,now)||!diagnosticFresh(true,d.diagnosticsObservedAt,now)||(s.source==='demo'&&!allowDemo))return empty;
 const current=s.tasks?.items.find(t=>t.taskId===d.task.taskId);if(!current||current.vehicleId!==d.task.vehicleId||current.stage!==d.task.stage)return empty;
 const f={...empty,taskId:d.task.taskId,vehicleId:d.task.vehicleId??''};
 const terminal=['Completed','Cancelled','Faulted'].includes(d.task.stage);f.terminal=terminal;
 const currentStep=d.steps.find(x=>x.isCurrent);
 const latestRouteStep=terminal?[...d.steps].reverse().find(step=>[1,3,5,7].includes(step.sequence)&&(d.commands??[]).some(command=>command.stepId===step.stepId&&command.route)):undefined;
 const routeStep=currentStep??latestRouteStep;
 const r=routeStep?taskRoute(d,routeStep.sequence,s.map,true,now):{route:null,reason:terminal?'任务已结束，但没有可核对的原命令路线快照':'无当前步骤，原任务路线不自动回推'};
 f.edgeIds=r.route?.edgeIds??[];f.routeMode=r.route?(currentStep?'Current':'Historical'):'None';
 f.reason=currentStep||!r.route?r.reason:`任务${d.task.stage==='Completed'?'已完成':d.task.stage==='Cancelled'?'已取消':'已故障'}，显示最后一段已发送命令的历史路线；只用于追溯，不代表当前执行。${r.reason}`;
 const traffic=s.traffic;const age=now-Date.parse(traffic?.capturedAt??'');
 if(traffic&&Number.isFinite(age)&&age>=-1000&&age<=7000){
  const same=(o:{mapId?:string|null;mapVersionId?:string|null})=>o.mapId===s.map!.id&&o.mapVersionId===s.map!.version;
  const known=new Set(s.map.resources.map(x=>x.id));const held=new Set<string>(),blocked=new Set<string>(),vehicles=new Set<string>();
  for(const q of traffic.requests.filter(x=>x.taskId===f.taskId&&x.vehicleId===f.vehicleId&&same(x))){
   for(const h of q.heldResources??[])if(same(h)&&known.has(h.resourceId))held.add(h.resourceId);
   if(q.state==='Waiting'&&q.blockingObservationCurrent)for(const b of q.blockers??[])if(same(b)&&known.has(b.resourceId)&&q.resourceIds.includes(b.resourceId)&&b.taskId!==f.taskId){blocked.add(b.resourceId);vehicles.add(b.vehicleId);}
  }
  f.held=[...held];f.blocked=[...blocked];f.blockers=[...vehicles];f.truncated=traffic.requestsTruncated;
 }else f.reason+='；路权观察未提供或已失效';
 f.unplotted=[f.vehicleId,...f.blockers].filter(id=>id&&!s.status?.agvs.some(v=>v.vehicleId===id&&mayPlot(s,v,now)));
 return f;
}
