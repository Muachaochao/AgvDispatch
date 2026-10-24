import type {AreaAnnotation} from './workbench.ts';
export function annotationLabel(a:AreaAnnotation){
  const xs=a.boundary.map(p=>p.x),ys=a.boundary.map(p=>p.y);
  return {x:(Math.min(...xs)+Math.max(...xs))/2,y:Math.max(...ys)};
}
