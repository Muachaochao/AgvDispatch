import type {Point, WebMap} from './types';
export interface DisplayArea {id:string;name:string;color:string;boundary:Point[];stationIds:string[];showName:boolean;showBoundary:boolean}
export interface DisplayAreaDocument {mapId:string;basisHash:string;revision:string;savedAt:string;areas:DisplayArea[]}
export interface DisplayAreaContext {map:WebMap;basisHash:string;document:DisplayAreaDocument|null;applicable:boolean}
export function containsPoint(points:Point[], p:Point):boolean {
  let inside=false;
  for(let i=0,j=points.length-1;i<points.length;j=i++){
    const a=points[j]!,b=points[i]!;
    const cross=(b.x-a.x)*(p.y-a.y)-(b.y-a.y)*(p.x-a.x);
    if(Math.abs(cross)<1e-8&&p.x>=Math.min(a.x,b.x)-1e-8&&p.x<=Math.max(a.x,b.x)+1e-8&&p.y>=Math.min(a.y,b.y)-1e-8&&p.y<=Math.max(a.y,b.y)+1e-8)return true;
    if((a.y>p.y)!==(b.y>p.y)&&p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
  }
  return inside;
}
export function areaCenter(a:DisplayArea):Point {
  const b=a.boundary;return {x:b.reduce((s,p)=>s+p.x,0)/b.length,y:b.reduce((s,p)=>s+p.y,0)/b.length};
}
export function rectangle(a:Point,b:Point):Point[]{return [{x:Math.min(a.x,b.x),y:Math.min(a.y,b.y)},{x:Math.max(a.x,b.x),y:Math.min(a.y,b.y)},{x:Math.max(a.x,b.x),y:Math.max(a.y,b.y)},{x:Math.min(a.x,b.x),y:Math.max(a.y,b.y)}];}
export function suggestedStations(boundary:Point[],map:WebMap){return map.nodes.filter(n=>containsPoint(boundary,n)).map(n=>n.id);}
export function appliesToMap(context:DisplayAreaContext,map:WebMap|null){return !!map&&context.applicable&&context.map.id===map.id&&context.map.version===map.version&&context.map.definitionHash===map.definitionHash;}
