import * as THREE from 'three';
import {areaCenter,type DisplayArea} from './display-areas';

export class DisplayAreasScene {
  readonly group=new THREE.Group();
  private labels:{button:HTMLButtonElement;anchor:THREE.Vector3}[]=[];
  private key='';
  constructor(private layer:HTMLElement,private select:(area:DisplayArea)=>void){}
  update(areas:DisplayArea[],key:string){
    if(this.key===key)return;this.key=key;this.clear();
    for(const a of areas){
      const mesh=new THREE.Mesh(new THREE.ShapeGeometry(new THREE.Shape(a.boundary.map(p=>new THREE.Vector2(p.x,p.y)))),new THREE.MeshBasicMaterial({color:a.color,transparent:true,opacity:.12,side:THREE.DoubleSide,depthWrite:false}));
      mesh.rotation.x=-Math.PI/2;mesh.position.y=-.065;mesh.userData.displayArea=a;this.group.add(mesh);
      if(a.showBoundary){const points=[...a.boundary,a.boundary[0]!].map(p=>new THREE.Vector3(p.x,-.06,-p.y));this.group.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(points),new THREE.LineBasicMaterial({color:a.color,transparent:true,opacity:.65,depthWrite:false})));}
      if(a.showName){const button=document.createElement('button');button.className='display-area-label';button.textContent=a.name;button.style.color=a.color;button.title=`${a.name} · ${a.stationIds.length}个站点，点击查看`;button.onclick=()=>this.select(a);this.layer.append(button);const p=areaCenter(a);this.labels.push({button,anchor:new THREE.Vector3(p.x,.01,-p.y)});}
    }
  }
  hit(ray:THREE.Raycaster){const area=ray.intersectObjects(this.group.children,false).find(h=>h.object.userData.displayArea)?.object.userData.displayArea as DisplayArea|undefined;if(area)this.select(area);}
  layout(camera:THREE.Camera,width:number,height:number){
    const bounds=this.layer.getBoundingClientRect();
    const occupied=Array.from(this.layer.querySelectorAll<HTMLElement>('.scene-map-label:not([hidden])')).map(el=>{const r=el.getBoundingClientRect();return {left:r.left-bounds.left,top:r.top-bounds.top,right:r.right-bounds.left,bottom:r.bottom-bounds.top};});
    for(const {button,anchor} of this.labels){
      const p=anchor.clone().project(camera);button.hidden=p.z<-1||p.z>1||Math.abs(p.x)>1||Math.abs(p.y)>1;if(button.hidden)continue;
      const x=(p.x+1)*width/2,y=(1-p.y)*height/2,w=button.offsetWidth,h=button.offsetHeight;
      let found=false;
      for(const offset of [0,-30,30,-60,60,-90,90]){const left=Math.min(Math.max(4,x-w/2),width-w-4),top=y-h/2+offset;const r={left,top,right:left+w,bottom:top+h};if(top<0||r.bottom>height||occupied.some(o=>r.left<o.right+4&&r.right>o.left-4&&r.top<o.bottom+4&&r.bottom>o.top-4))continue;button.style.transform=`translate(${left}px,${top}px)`;occupied.push(r);found=true;break;}button.hidden=!found;
    }
  }
  clear(){this.group.traverse(o=>{const m=o as THREE.Mesh;m.geometry?.dispose();if(m.material)(Array.isArray(m.material)?m.material:[m.material]).forEach(m=>m.dispose());});this.group.clear();this.labels.forEach(l=>l.button.remove());this.labels=[];}
  dispose(){this.clear();this.group.removeFromParent();}
}
