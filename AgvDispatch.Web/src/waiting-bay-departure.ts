import type {Snapshot} from './types.ts';
import {isFresh} from './state.ts';
export interface DeparturePage {
 mapId:string;mapVersion:string;mapDefinitionHash:string;
 assessment:{checkedAt:string;validUntil:string|null;stepId:string;resourceId:string;taskId:string|null;vehicleId:string|null;
 mapId:string|null;mapVersionId:string|null;available:boolean;departureEvidenceVerified:boolean;reason:string;leaseIds:string[];
 resourcesReleased:boolean;releaseAuthorized:boolean};
}
export function departureCurrent(p:DeparturePage|null,s:Snapshot|null,taskId:string,stepId:string,resourceId:string,now:number){
 if(!p||!s?.map||!isFresh(s,now))return false;
 const a=p.assessment,age=now-Date.parse(a.checkedAt),task=s.tasks?.items.find(t=>t.taskId===taskId);
 if(!task||['Completed','Cancelled','Faulted','CancellationPending','CancellationReview'].includes(task.stage)||
  p.mapId!==s.map.id||p.mapVersion!==s.map.version||!p.mapDefinitionHash||p.mapDefinitionHash!==s.map.definitionHash||
  a.stepId!==stepId||a.resourceId!==resourceId||a.resourcesReleased!==false||a.releaseAuthorized!==false||
  !Number.isFinite(age)||age< -1000||age>5000)return false;
 if(a.taskId!==null&&a.taskId!==taskId||a.vehicleId!==null&&a.vehicleId!==task.vehicleId||
  a.mapId!==null&&a.mapId!==s.map.id||a.mapVersionId!==null&&a.mapVersionId!==s.map.version)return false;
 if(a.available&&(a.taskId!==taskId||a.vehicleId!==task.vehicleId))return false;
 return !a.departureEvidenceVerified||a.available&&!!a.validUntil&&now<Date.parse(a.validUntil);
}
