import type {Snapshot,TaskDetail} from './types.ts';
import {isFresh,mayPlot} from './state.ts';
import {diagnosticFresh} from './task-diagnostics.ts';
import {taskRoute} from './task-route.ts';
export interface TrafficFocus {taskId:string;vehicleId:string;edgeIds:string[];held:string[];blocked:string[];blockers:string[];unplotted:string[];reason:string;truncated:boolean;routeMode:'Current'|'Historical'|'None';routeCount:number;routeExpectedCount:number;terminal:boolean}
export function trafficFocus(s:Snapshot|null,d:TaskDetail|null,now:number,allowDemo=false):TrafficFocus {
 const empty:TrafficFocus={taskId:'',vehicleId:'',edgeIds:[],held:[],blocked:[],blockers:[],unplotted:[],reason:'选择任务后联动；需有效的同图后台观察',truncated:false,routeMode:'None',routeCount:0,routeExpectedCount:0,terminal:false};
 if(!s?.map||!d||!isFresh(s,now)||!diagnosticFresh(true,d.diagnosticsObservedAt,now)||(s.source==='demo'&&!allowDemo))return empty;
 const current=s.tasks?.items.find(t=>t.taskId===d.task.taskId);if(!current||current.vehicleId!==d.task.vehicleId||current.stage!==d.task.stage)return empty;
 const f={...empty,taskId:d.task.taskId,vehicleId:d.task.vehicleId??''};
 const terminal=['Completed','Cancelled','Faulted'].includes(d.task.stage);f.terminal=terminal;
 const currentStep=d.steps.find(x=>x.isCurrent);
 const currentRoute=currentStep?taskRoute(d,currentStep.sequence,s.map,true,now):null;
 const historicalSteps=terminal?d.steps.filter(step=>[1,3,5,7].includes(step.sequence)&&(d.commands??[]).some(command=>command.stepId===step.stepId&&command.route)):[];
 const historicalRoutes=historicalSteps.map(step=>taskRoute(d,step.sequence,s.map,true,now)).filter(result=>!!result.route);
 const historicalEdges=[...new Set(historicalRoutes.flatMap(result=>result.route?.edgeIds??[]))];
 f.edgeIds=currentRoute?.route?.edgeIds??historicalEdges;
 f.routeMode=currentRoute?.route?'Current':historicalEdges.length?'Historical':'None';
 f.routeCount=currentRoute?.route?1:historicalRoutes.length;
 f.routeExpectedCount=currentRoute?.route?1:historicalSteps.length;
 f.reason=currentRoute?.route?currentRoute.reason:historicalEdges.length?`任务${d.task.stage==='Completed'?'已完成':d.task.stage==='Cancelled'?'已取消':'已故障'}，显示${historicalRoutes.length}/${historicalSteps.length}段可核对的历史命令路线；只用于追溯，不代表当前执行。`:terminal?'任务已结束，但没有可核对的原命令路线快照':'无当前步骤，原任务路线不自动回推';
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
