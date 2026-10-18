import type {Snapshot,TrafficRequest,WebMap,WaitingPage,WaitingQuery} from './types.ts';
export function waitingTime(r:TrafficRequest){
 if(r.waitingClockValid===false)return '时钟异常，暂停计时';
 if(r.waitingSeconds===null||r.waitingSeconds===undefined||!Number.isFinite(r.waitingSeconds)||r.waitingSeconds<0)return '等待时长未知';
 const seconds=Math.floor(r.waitingSeconds),text=seconds>=60?`${Math.floor(seconds/60)}分${seconds%60}秒`:`${seconds}秒`;
 return r.waitingStartKnown?text:`已观察${text}（实际起点未知）`;
}
export function resourceView(map:WebMap|null|undefined,id:string){
 const matches=map?.resources.filter(r=>r.id===id)??[],points=matches[0]?.boundary;
 if(matches.length!==1||!points||points.length<3||points.length>10000||points.some(p=>!Number.isFinite(p.x)||!Number.isFinite(p.y)||Math.abs(p.x)>1e7||Math.abs(p.y)>1e7))return null;
 let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity;for(const p of points){minX=Math.min(minX,p.x);maxX=Math.max(maxX,p.x);minY=Math.min(minY,p.y);maxY=Math.max(maxY,p.y);}
 if(maxX<=minX||maxY<=minY)return null;return{x:(minX+maxX)/2,y:(minY+maxY)/2,span:Math.max(2,maxX-minX,maxY-minY)};
}
export function demoWaitingPage(s:Snapshot,q:WaitingQuery):WaitingPage{
 const waiting=s.traffic?.requests.filter(r=>r.state==='Waiting')??[];
 const matched=waiting.filter(r=>(q.minimumPriority===null||(r.priority!==null&&r.priority!==undefined&&r.priority>=q.minimumPriority))&&(!q.blockingResourceId||r.blockers.some(b=>b.resourceId.toUpperCase()===q.blockingResourceId!.toUpperCase())));
 const priority=(r:TrafficRequest)=>r.priority??-1,seconds=(r:TrafficRequest)=>r.waitingSeconds??-1;
 matched.sort((a,b)=>(q.sort==='PriorityThenWait'?priority(b)-priority(a)||seconds(b)-seconds(a):seconds(b)-seconds(a)||priority(b)-priority(a))||a.stepId.localeCompare(b.stepId));
 return {mapId:s.map?.id??null,mapVersion:s.map?.version??null,mapDefinitionHash:s.map?.definitionHash??null,page:{capturedAt:s.traffic?.capturedAt??s.receivedAt,totalWaiting:waiting.length,totalMatched:matched.length,pageNumber:q.pageNumber,pageSize:q.pageSize,items:matched.slice((q.pageNumber-1)*q.pageSize,q.pageNumber*q.pageSize),blockingResources:[...new Set(waiting.flatMap(r=>r.blockers.map(b=>b.resourceId)))].sort()}};
}
export function sameWaitingMap(p:WaitingPage|null,map:WebMap|null|undefined){return !!p&&!!map&&p.mapId===map.id&&p.mapVersion===map.version&&!!p.mapDefinitionHash&&p.mapDefinitionHash===map.definitionHash;}
