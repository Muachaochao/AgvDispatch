import type { TaskDetail,WebMap,TaskRoute } from './types.ts';
import { diagnosticFresh } from './task-diagnostics.ts';
export function taskRoute(detail:TaskDetail,sequence:number,map:WebMap|null|undefined,fresh:boolean,now:number):{route:TaskRoute|null;reason:string}{
 const fail=(reason:string)=>({route:null,reason});
 if(!map||!diagnosticFresh(fresh,detail.diagnosticsObservedAt,now))return fail('诊断或运行地图已失效，请等待后台新观察');
 const step=detail.steps.find(s=>s.sequence===sequence);if(!step)return fail('步骤不存在');
 const transport=detail.steps.filter(s=>s.sequence<=sequence&&[1,3,5,7].includes(s.sequence)).at(-1);if(!transport)return fail('没有原运输步骤');
 const commands=detail.commands?.filter(c=>c.stepId===transport.stepId)??[];if(commands.length!==1||!commands[0]!.route)return fail('没有唯一原命令路线快照；不按当前最短路补造');
 const c=commands[0]!,r=c.route!;
 if(r.taskId!==detail.task.taskId||r.stepId!==transport.stepId||r.commandId!==c.commandId||r.vehicleId!==detail.task.vehicleId||r.mapId!==map.id||r.mapVersionId!==map.version||!map.definitionHash||r.mapDefinitionHash!==map.definitionHash)return fail('原命令归属、地图版本或内容摘要与当前图不符');
 if(r.edgeIds.length<1||r.edgeIds.length>2048||r.nodeIds.length!==r.edgeIds.length+1||r.vendorSites.length!==r.nodeIds.length)return fail('原路线明细不完整或超预算');
 const nodes=new Map(map.nodes.map(n=>[n.id,n]));if(nodes.size!==map.nodes.length||new Set(map.edges.map(e=>e.id)).size!==map.edges.length)return fail('当前图编号不唯一');
 for(let i=0;i<r.nodeIds.length;i++){const n=nodes.get(r.nodeIds[i]!);if(!n||n.agvSiteId!==r.vendorSites[i])return fail('原业务/厂家端点与当前图不一致');}
 for(let i=0;i<r.edgeIds.length;i++){const edges=map.edges.filter(e=>e.id===r.edgeIds[i]);if(edges.length!==1)return fail('原路段缺少唯一几何');const e=edges[0]!;if(!((e.from===r.nodeIds[i]&&e.to===r.nodeIds[i+1])||(e.bidirectional&&e.to===r.nodeIds[i]&&e.from===r.nodeIds[i+1]))||e.points.length<2||e.points.length>33||e.points.some(p=>!Number.isFinite(p.x)||!Number.isFinite(p.y)))return fail('原路段方向或几何不符');}
 return{route:r,reason:`第${transport.sequence}步原命令路线 · ${r.action}；仅路线/端点，不代表车体实时位置或放行`};
}
