import {demoSnapshot,stepNames} from './demo.ts';
import type {Snapshot,TaskDetail} from './types.ts';
export const rehearsalTitles=['两车冲突等待','取消请求已记录','3003接受，等待1110结束','1110结束，仍需现场核对','恢复核对中，条件未齐','模拟现场核对及原占用处置完成','关闭取消，等待全场放行','全场核对放行（不自动派车）'];
export function rehearsalNext(phase:number,confirmed:boolean[]){if(phase===4&&(confirmed.length!==4||!confirmed.every(Boolean)))return phase;return Math.min(phase+1,7);}
export function rehearsalSnapshot(phase:number):Snapshot{
 const s=demoSnapshot(2),now=s.receivedAt,map=s.map!;map.definitionHash='DEMO-CONTENT';
 const task=s.tasks!.items[0]!;task.stage=phase===0?'GoingToDumper':phase<3?'CancellationPending':phase<6?'CancellationReview':'Cancelled';
 const held=phase<5,owner={resourceId:'SHARED-01',mapId:map.id,mapVersionId:map.version};
 s.resources![0]!.occupiedCount=held?1:0;s.resources![0]!.leases=held?[{taskId:task.taskId,vehicleId:'AGV-01'}]:[];
 s.status!.agvs[0]!.x=10;s.status!.agvs[0]!.y=4;s.status!.agvs[0]!.navigationState=phase<3?'原运输尚未确认结束':'已停止，待核对';
 s.traffic={capturedAt:now,requestsTruncated:false,requests:[{taskId:task.taskId,vehicleId:'AGV-01',stepId:'DEMO-S3',mapId:map.id,mapVersionId:map.version,state:held?'Retained':'Released',reason:held?'原占用保留':'模拟人工处置后解除',resourceIds:['SHARED-01'],heldResources:held?[owner]:[],blockers:[],blockingObservationCurrent:false},
 {taskId:'DEMO-TASK-002',vehicleId:'AGV-02',stepId:'DEMO-T2-S1',mapId:map.id,mapVersionId:map.version,state:held?'Waiting':'NoLease',reason:held?'共享区域由AGV-01保留':'无当前阻塞，仍未派车',resourceIds:['SHARED-01'],heldResources:[],blockers:held?[{...owner,taskId:task.taskId,vehicleId:'AGV-01',stepId:'DEMO-S3',heartbeatExpired:phase>=3}]:[],blockingObservationCurrent:held,waitingStartKnown:true,waitingClockValid:true,waitingSeconds:120,priority:20}]};
 return s;
}
export function rehearsalDetail(s:Snapshot,id:string,phase:number):TaskDetail|null{
 const task=s.tasks?.items.find(t=>t.taskId===id);if(!task)return null;const main=id==='DEMO-TASK-001';
 return {task,diagnosticsObservedAt:s.receivedAt,waitingExplanation:'合成演示：'+rehearsalTitles[phase],steps:stepNames.map((stepType,i)=>({sequence:i+1,stepId:main?'DEMO-S'+(i+1):'DEMO-T2-S'+(i+1),stepType,status:phase>=6&&main?'ManualIntervention':i<2&&main?'Completed':'Pending',isCurrent:main?i===2:i===0,waitingExplanation:''})),
 commands:main?[{commandId:'DEMO-C3',stepId:'DEMO-S3',status:'Sent',attemptCount:1,route:{taskId:id,stepId:'DEMO-S3',commandId:'DEMO-C3',vehicleId:'AGV-01',mapId:s.map!.id,mapVersionId:s.map!.version,mapDefinitionHash:'DEMO-CONTENT',nodeIds:['PICK1','CROSS1','WAIT'],vendorSites:['LM-PICK1','LM-CROSS1','LM-WAIT'],edgeIds:['E02','E03'],action:'JackLoad'}}]:[],
 cancellation:main&&phase>0?{requestId:'DEMO-CANCEL',reason:'合成取消演示',requestedAt:s.receivedAt,sendAttempts:phase>=2?1:0,sentAt:phase>=2?s.receivedAt:undefined,confirmedAt:phase>=3?s.receivedAt:undefined}:undefined,
 resources:main&&phase<5?[{resourceId:'SHARED-01',vehicleId:'AGV-01',expiresAt:s.receivedAt,heartbeatExpired:phase>=3}]:[],plcConditions:[]};
}
