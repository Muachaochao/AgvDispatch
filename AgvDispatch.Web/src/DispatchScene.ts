import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { makeWaitingArea } from './vendor/agv-web-visible/MeshUtils';
import type { Snapshot, WebMap } from './types';
import { mayPlot } from './state';
import { vehicleCategory } from './dashboard';
import type {TrafficFocus} from './traffic-focus';
import './scene-labels.css';
import {DisplayAreasScene} from './DisplayAreasScene';
import type {DisplayArea} from './display-areas';
import {annotationLabel} from './area-annotations';
import {placeMapLabels,type LabelRect,type MapLabelInput,type MapLabelMode} from './map-label-layout';

const VEHICLE_COLORS: Record<string, string> = {
  running: '#21dca3', waiting: '#eeb85c', blocked: '#f0a64d', idle: '#ad8aeb', charging: '#5aaee8',
  fault: '#f05c7e', offline: '#6c7d9d', unknown: '#8398b8'
};

export class DispatchScene {
  private scene = new THREE.Scene();
  private camera = new THREE.PerspectiveCamera(42, 1, 0.1, 5000);
  private renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'high-performance' });
  private controls: OrbitControls;
  private mapGroup = new THREE.Group();
  private vehicleGroup = new THREE.Group();
  private resourceFocus=new THREE.Group();
  private edgeFocusGroup = new THREE.Group();
  private resourceStatusGroup = new THREE.Group();
  private resourceBounds = new Map<string,{minX:number;maxX:number;minY:number;maxY:number}>();
  private resizeObserver: ResizeObserver;
  private frame = 0;
  private needsRender = true;
  private needsLabelLayout = true;
  private lastLabelLayout = 0;
  private sized=false;
  private mapKey = '';
  private currentMap: WebMap | null = null;
  private edgeFocusKey = '';
  private resourceStatusKey = '';
  private center = new THREE.Vector3();
  private span = 25;
  private mode: 'top' | 'isometric' = 'top';
  private pick = new THREE.Raycaster();
  private onPointer: (event: PointerEvent) => void;
  private onHover: (event: PointerEvent) => void;
  private onLeave = () => { if(this.hoveredStation){this.hoveredStation='';this.requestRender(true);} };
  private onControlsChange = () => { this.requestRender(true); };
  private hoveredStation='';
  private lastHoverAt = 0;
  private labelMode:MapLabelMode='auto';
  private themeObserver: MutationObserver;
  private projected = new THREE.Vector3();


  private vehiclePool = new Map<string, { group: THREE.Group; label: SceneLabel; ring: THREE.Mesh | null }>();
  private labelLayer=document.createElement('div');
  private displayAreas:DisplayAreasScene;
  private stationLabels:SceneLabel[]=[];
  private viewportWidth=0;
  private viewportHeight=0;
  private ringMaterial = new THREE.MeshBasicMaterial({ color: '#d6f5ff', side: THREE.DoubleSide });

  constructor(private container: HTMLElement, private onSelect: (id: string) => void, onAreaSelect:(area:DisplayArea)=>void=()=>{}) {
    this.scene.background = new THREE.Color(this.themeColor('#edf3f8', '#0b1229'));
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
    container.appendChild(this.renderer.domElement);
    this.labelLayer.className='scene-label-layer';this.labelLayer.setAttribute('aria-label','车辆与站点标注');container.appendChild(this.labelLayer);
    this.displayAreas=new DisplayAreasScene(this.labelLayer,onAreaSelect);this.scene.add(this.displayAreas.group);
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = true;
    this.controls.maxPolarAngle = Math.PI / 2 - 0.03;
    this.controls.addEventListener('change', this.onControlsChange);
    this.scene.add(new THREE.HemisphereLight(0xc6e8ff, 0x26374b, 3));
    const light = new THREE.DirectionalLight(0xffffff, 2); light.position.set(10, 25, 10); this.scene.add(light);
    this.scene.add(this.mapGroup, this.vehicleGroup,this.resourceFocus);
    this.mapGroup.add(this.edgeFocusGroup, this.resourceStatusGroup);
    this.resizeObserver = new ResizeObserver(() => {
      const { width, height } = container.getBoundingClientRect();
      if (width < 1 || height < 1) return;
      this.viewportWidth=width;this.viewportHeight=height;this.renderer.setSize(width, height); this.camera.aspect = width / Math.max(height, 1); this.camera.updateProjectionMatrix(); if(!this.sized){this.sized=true;this.view(this.mode);}else this.requestRender(true);
    });
    this.resizeObserver.observe(container);
    this.themeObserver=new MutationObserver(()=>this.applyTheme());
    this.themeObserver.observe(document.documentElement,{attributes:true,attributeFilter:['data-theme','class']});
    this.onPointer = event => {
      const rect = this.renderer.domElement.getBoundingClientRect();
      this.pick.setFromCamera(new THREE.Vector2((event.clientX - rect.left) / rect.width * 2 - 1, -(event.clientY - rect.top) / rect.height * 2 + 1), this.camera);
      const hit = this.pick.intersectObjects(this.vehicleGroup.children, true)[0];
      if (hit) {
        let object: THREE.Object3D | null = hit.object;
        while (object && !object.userData.vehicleId) object = object.parent;
        if (object) this.onSelect(object.userData.vehicleId);
      } else this.displayAreas.hit(this.pick);
    };
    this.renderer.domElement.addEventListener('pointerup', this.onPointer);
    this.onHover=event=>{
      if(event.buttons)return;
      const now=performance.now();if(now-this.lastHoverAt<45)return;this.lastHoverAt=now;
      const rect=this.renderer.domElement.getBoundingClientRect();
      const x=event.clientX-rect.left,y=event.clientY-rect.top;
      let nearest=14,hovered='';
      for(const label of this.stationLabels){
        if(label.area)continue;
        const p=this.projected.copy(label.anchor).project(this.camera);
        if(p.z<-1||p.z>1)continue;
        const distance=Math.hypot((p.x+1)*rect.width/2-x,(1-p.y)*rect.height/2-y);
        if(distance<nearest){nearest=distance;hovered=label.id;}
      }
      if(hovered!==this.hoveredStation){this.hoveredStation=hovered;this.requestRender(true);}
    };
    this.renderer.domElement.addEventListener('pointermove',this.onHover);
    this.renderer.domElement.addEventListener('pointerleave',this.onLeave);
    this.view('top');
    this.requestRender(true);
  }

  setDisplayAreas(areas:DisplayArea[],key:string){this.displayAreas.update(areas,key);this.requestRender(true);}
  setLabelMode(mode:MapLabelMode){this.labelMode=mode;this.requestRender(true);}
  private themeColor(light:string,dark:string){return document.documentElement.dataset.theme==='light'?light:dark;}
  private applyTheme(){
    const light=document.documentElement.dataset.theme==='light';
    this.scene.background=new THREE.Color(light?'#edf3f8':'#0b1229');
    this.renderer.domElement.style.background=light?'#edf3f8':'#0b1229';
    if(this.currentMap)this.drawMap(this.currentMap);
    this.requestRender(true);
  }

  update(snapshot: Snapshot | null, selected = '', selectedEdge = '', focus?:TrafficFocus, now=Date.now()) {
    const map = snapshot?.map;
    const key = map ? `${snapshot?.source}:${map.id}:${map.version}:${map.definitionHash}` : '';
    if (key !== this.mapKey) { this.mapKey = key; this.drawMap(map ?? null); }
    this.updateEdgeFocus(map ?? null, selectedEdge, focus);
    this.updateResourceStatus(map ?? null, focus);
    const activeIds = new Set<string>();
    if (snapshot) {
      for (const vehicle of snapshot.status?.agvs ?? []) {
        if (!mayPlot(snapshot, vehicle, now)) continue;
        activeIds.add(vehicle.vehicleId);
        const category = vehicleCategory(vehicle, snapshot.tasks?.items ?? [], true, now);
        const color = VEHICLE_COLORS[category] ?? VEHICLE_COLORS.unknown;

        let entry = this.vehiclePool.get(vehicle.vehicleId);
        if (!entry) {
          const group = new THREE.Group();
          group.userData.vehicleId = vehicle.vehicleId;
          const material = new THREE.MeshStandardMaterial({ color, roughness: 0.4 });
          const deckMaterial = new THREE.MeshStandardMaterial({ color, roughness: 0.3, metalness: 0.12 });
          const platformMaterial = new THREE.MeshStandardMaterial({ color: '#d8eef7', roughness: 0.35, metalness: 0.25 });
          const chassis = new THREE.Mesh(new THREE.BoxGeometry(0.94, 0.2, 0.64), material);
          chassis.position.y = 0.11; chassis.userData.vehiclePaint = true;
          const deck = new THREE.Mesh(new THREE.BoxGeometry(0.78, 0.08, 0.54), deckMaterial);
          deck.position.y = 0.25; deck.userData.vehiclePaint = true;
          const liftingPlatform = new THREE.Mesh(new THREE.BoxGeometry(0.42, 0.045, 0.4), platformMaterial);
          liftingPlatform.position.y = 0.31;
          const forkLeft = new THREE.Mesh(new THREE.BoxGeometry(0.46, 0.035, 0.07), platformMaterial);
          forkLeft.position.set(0.18, 0.325, 0.18);
          const forkRight = forkLeft.clone(); forkRight.position.z = -0.18;
          group.add(chassis, deck, liftingPlatform, forkLeft, forkRight);
          const heading = new THREE.ArrowHelper(new THREE.Vector3(1, 0, 0), new THREE.Vector3(0.48, 0.23, 0), 0.42, '#ffffff', 0.16, 0.1);
          group.add(heading);
          const label = this.makeSceneLabel(vehicle.vehicleId,'vehicle');
          entry = { group, label, ring: null };
          this.vehiclePool.set(vehicle.vehicleId, entry);
        }

        entry.group.position.set(vehicle.x!, 0.13, -vehicle.y!);
        entry.group.rotation.y = vehicle.angle ?? 0;
        entry.group.traverse(object => {
          if (object instanceof THREE.Mesh && object.userData.vehiclePaint)
            (object.material as THREE.MeshStandardMaterial).color.set(color);
        });

        entry.label.anchor.set(vehicle.x!,0.68,-vehicle.y!);entry.label.active=true;entry.label.element.style.setProperty('--label-color',color);

        const role=focus?.blockers.includes(vehicle.vehicleId)?'blocker':focus?.vehicleId===vehicle.vehicleId?'task':vehicle.vehicleId===selected?'selected':'';
        if (role && !entry.ring) {
          const ring = new THREE.Mesh(new THREE.RingGeometry(0.5, 0.55, 40), this.ringMaterial.clone());
          ring.rotation.x = -Math.PI / 2;
          ring.position.y = -0.11;
          entry.group.add(ring);
          entry.ring = ring;
        } else if (!role && entry.ring) {
          entry.group.remove(entry.ring); (entry.ring.material as THREE.Material).dispose();
          entry.ring = null;
        }

        if(entry.ring)(entry.ring.material as THREE.MeshBasicMaterial).color.set(role==='blocker'?'#ff657e':role==='task'?'#47e4b3':'#d6f5ff');
        if (!entry.group.parent) this.vehicleGroup.add(entry.group);

      }
    }

    for (const [id, entry] of this.vehiclePool) {
      if (!activeIds.has(id)) {
        if (entry.group.parent) this.vehicleGroup.remove(entry.group);
        entry.label.active=false;entry.label.element.hidden=true;if(entry.label.leader)entry.label.leader.hidden=true;
      }
    }
    this.requestRender(false);
  }

  private drawMap(map: WebMap | null) {
    this.currentMap=map;
    this.clearResourceFocus();this.clearGroup(this.mapGroup);
    this.mapGroup.add(this.edgeFocusGroup,this.resourceStatusGroup);
    this.resourceBounds.clear();this.edgeFocusKey='';this.resourceStatusKey='';
    for (const [, entry] of this.vehiclePool) {
      if (entry.group.parent) this.vehicleGroup.remove(entry.group);
      entry.label.active=false;entry.label.element.hidden=true;if(entry.label.leader)entry.label.leader.hidden=true;
    }
    for(const entry of this.vehiclePool.values()){entry.label.element.remove();entry.label.leader?.remove();}
    this.vehiclePool.clear();
    this.stationLabels.forEach(label=>{label.element.remove();label.leader?.remove();});this.stationLabels=[];this.hoveredStation='';

    if (!map?.nodes.length) return;
    let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity;
    const add=(p:{x:number;y:number})=>{if(!Number.isFinite(p.x)||!Number.isFinite(p.y))return;minX=Math.min(minX,p.x);maxX=Math.max(maxX,p.x);minY=Math.min(minY,-p.y);maxY=Math.max(maxY,-p.y);};
    map.nodes.forEach(add);map.edges.forEach(e=>e.points.forEach(add));map.resources.forEach(r=>r.boundary?.forEach(add));map.areaAnnotations?.forEach(a=>a.boundary.forEach(add));if(!Number.isFinite(minX))return;
    this.center.set((minX + maxX) / 2, 0, (minY + maxY) / 2);
    this.span = Math.max(maxX - minX, maxY - minY, 15);
    const ratio=Math.min(devicePixelRatio,map.nodes.length>500?1.25:1.5);
    this.renderer.setPixelRatio(ratio);
    if(this.viewportWidth>0&&this.viewportHeight>0)this.renderer.setSize(this.viewportWidth,this.viewportHeight);
    const grid = new THREE.GridHelper(Math.ceil(this.span * 1.5), 30, this.themeColor('#b8c9d8','#294258'), this.themeColor('#dbe5ee','#1b2d40'));
    grid.position.copy(this.center); grid.position.y = -0.12; this.mapGroup.add(grid);

    for(const area of map.areaAnnotations??[]){
      if(area.boundary.length<3)continue;
      const shape=new THREE.Shape(area.boundary.map(p=>new THREE.Vector2(p.x,p.y)));
      const mesh=new THREE.Mesh(new THREE.ShapeGeometry(shape),new THREE.MeshBasicMaterial({color:area.fillColor,transparent:true,opacity:Math.min(area.fillOpacity,.12),side:THREE.DoubleSide,depthWrite:false}));
      mesh.rotation.x=-Math.PI/2;mesh.position.y=-.08;this.mapGroup.add(mesh);
      const points=[...area.boundary,area.boundary[0]!].map(p=>new THREE.Vector3(p.x,-.06,-p.y));
      this.mapGroup.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(points),new THREE.LineBasicMaterial({color:area.strokeColor})));
      const position=annotationLabel(area),label=this.makeSceneLabel(area.text,'station');
      label.id='area:'+area.id;label.anchor.set(position.x,.02,-position.y);label.area=true;label.priority=30;label.element.style.color=area.strokeColor;label.element.classList.add('vendor-area');this.stationLabels.push(label);
    }

    this.addEdgeBatch(map);
    for (const node of map.nodes) {
      const label=this.makeSceneLabel(node.agvSiteId??node.id,'station');label.anchor.set(node.x,.05,-node.y);
      label.id=node.id;label.priority=node.type==='Station'||node.type==='WaitingPoint'?20:10;

      label.element.classList.toggle('disabled',!node.enabled);this.stationLabels.push(label);
    }
    this.addNodeMarkers(map);
    this.addResourceFill(map);
    for (const node of map.nodes.filter(n => n.type === 'WaitingPoint' || n.id === 'WAIT'))
      this.mapGroup.add(makeWaitingArea({ name: '', type: 'waiting', x: node.x, y: 0, z: -node.y, width: 1.15, height: 0.9 }));
    this.stationLabels.sort((a,b)=>Number(!!b.area)-Number(!!a.area)||b.priority-a.priority||a.id.localeCompare(b.id));
    this.view(this.mode);
    this.requestRender(true);
  }

  private updateEdgeFocus(map: WebMap | null, selectedEdge: string, focus?: TrafficFocus) {
    const ids=[...new Set(focus?.edgeIds??[])];
    const key=`${selectedEdge}|${ids.slice().sort().join(',')}`;
    if(key===this.edgeFocusKey)return;
    this.edgeFocusKey=key;
    this.clearGroup(this.edgeFocusGroup);
    if(!map)return;
    const selected=map.edges.find(edge=>edge.id===selectedEdge);
    if(selected)this.addEdgeOverlay([selected],this.themeColor('#075f72','#ffffff'),3);
    const active=map.edges.filter(edge=>ids.includes(edge.id)&&edge.id!==selectedEdge);
    if(active.length)this.addEdgeOverlay(active,'#20a982',2);
  }

  private addEdgeOverlay(edges: WebMap['edges'], color: string, renderOrder: number) {
    const positions:number[]=[];
    for(const edge of edges)for(let i=1;i<edge.points.length;i++){
      const a=edge.points[i-1]!,b=edge.points[i]!;
      positions.push(a.x,.12,-a.y,b.x,.12,-b.y);
    }
    if(!positions.length)return;
    const geometry=new THREE.BufferGeometry();
    geometry.setAttribute('position',new THREE.Float32BufferAttribute(positions,3));
    const line=new THREE.LineSegments(geometry,new THREE.LineBasicMaterial({color,depthWrite:false,transparent:true,opacity:.98}));
    line.renderOrder=renderOrder;
    this.edgeFocusGroup.add(line);
  }

  private updateResourceStatus(map: WebMap | null, focus?: TrafficFocus) {
    const blocked=[...new Set(focus?.blocked??[])];
    const held=[...new Set((focus?.held??[]).filter(id=>!blocked.includes(id)))];
    const key=`${blocked.slice().sort().join(',')}|${held.slice().sort().join(',')}`;
    if(key===this.resourceStatusKey)return;
    this.resourceStatusKey=key;
    this.clearGroup(this.resourceStatusGroup);
    if(!map)return;
    this.addResourceOverlay(map,blocked,'#ff657e',.42);
    this.addResourceOverlay(map,held,'#ffbd59',.3);
  }

  private addResourceOverlay(map: WebMap, ids: string[], color: string, opacity: number) {
    if(!ids.length)return;
    const geometry=this.buildResourceGeometry(map,new Set(ids),-.015);
    if(!geometry)return;
    const mesh=new THREE.Mesh(geometry,new THREE.MeshBasicMaterial({color,transparent:true,opacity,side:THREE.DoubleSide,depthWrite:false}));
    mesh.renderOrder=1;
    this.resourceStatusGroup.add(mesh);
  }

  private addEdgeBatch(map: WebMap) {
    const positions:number[]=[],colors:number[]=[];
    for(const edge of map.edges){
      const color=new THREE.Color(edge.enabled?this.themeColor('#5e91a6','#3e8da8'):this.themeColor('#a9b7c5','#455365'));
      for(let i=1;i<edge.points.length;i++){
        const a=edge.points[i-1]!,b=edge.points[i]!;
        positions.push(a.x,.01,-a.y,b.x,.01,-b.y);
        colors.push(color.r,color.g,color.b,color.r,color.g,color.b);
      }
    }
    if(positions.length){
      const geometry=new THREE.BufferGeometry();
      geometry.setAttribute('position',new THREE.Float32BufferAttribute(positions,3));
      geometry.setAttribute('color',new THREE.Float32BufferAttribute(colors,3));
      const lines=new THREE.LineSegments(geometry,new THREE.LineBasicMaterial({vertexColors:true,depthWrite:false,transparent:true,opacity:.92}));
      lines.renderOrder=0;
      this.mapGroup.add(lines);
    }

    const arrows:number[]=[];
    for(const edge of map.edges){
      if(!edge.enabled||edge.points.length<2)continue;
      const index=Math.floor((edge.points.length-1)/2),a=edge.points[index]!,b=edge.points[index+1]!;
      const dx=b.x-a.x,dz=a.y-b.y,length=Math.hypot(dx,dz);
      if(length<.001)continue;
      const ux=dx/length,uz=dz/length,px=-uz,pz=ux;
      const mx=(a.x+b.x)/2,mz=(-a.y-b.y)/2;
      const tipX=mx+ux*.28,tipZ=mz+uz*.28;
      const baseX=mx-ux*.2,baseZ=mz-uz*.2;
      arrows.push(tipX,.07,tipZ,baseX+px*.13,.07,baseZ+pz*.13,baseX-px*.13,.07,baseZ-pz*.13);
    }
    if(arrows.length){
      const geometry=new THREE.BufferGeometry();
      geometry.setAttribute('position',new THREE.Float32BufferAttribute(arrows,3));
      const mesh=new THREE.Mesh(geometry,new THREE.MeshBasicMaterial({color:this.themeColor('#497f95','#65abc0'),side:THREE.DoubleSide,depthWrite:false}));
      mesh.renderOrder=0;
      this.mapGroup.add(mesh);
    }
  }

  private addNodeMarkers(map: WebMap) {
    const addInstances=(nodes:WebMap['nodes'],color:string)=>{
      if(!nodes.length)return;
      const geometry=new THREE.RingGeometry(.18,.23,16);
      const material=new THREE.MeshBasicMaterial({color,side:THREE.DoubleSide,depthWrite:false});
      const instances=new THREE.InstancedMesh(geometry,material,nodes.length),dummy=new THREE.Object3D();
      dummy.rotation.x=-Math.PI/2;
      nodes.forEach((node,index)=>{
        dummy.position.set(node.x,.04,-node.y);dummy.updateMatrix();instances.setMatrixAt(index,dummy.matrix);
      });
      instances.instanceMatrix.needsUpdate=true;
      instances.computeBoundingSphere();
      this.mapGroup.add(instances);
    };
    addInstances(map.nodes.filter(node=>node.enabled),this.themeColor('#159a78','#43d9b1'));
    addInstances(map.nodes.filter(node=>!node.enabled),this.themeColor('#9aa8b7','#637083'));
  }

  private addResourceFill(map: WebMap) {
    for(const resource of map.resources){
      if(!resource.boundary?.length)continue;
      let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity;
      for(const point of resource.boundary){minX=Math.min(minX,point.x);maxX=Math.max(maxX,point.x);minY=Math.min(minY,point.y);maxY=Math.max(maxY,point.y);}
      if(Number.isFinite(minX)&&maxX>minX&&maxY>minY)this.resourceBounds.set(resource.id,{minX,maxX,minY,maxY});
    }
    const geometry=this.buildResourceGeometry(map,null,-.03);
    if(!geometry)return;
    const mesh=new THREE.Mesh(geometry,new THREE.MeshBasicMaterial({color:'#e4aa4f',transparent:true,opacity:.09,side:THREE.DoubleSide,depthWrite:false}));
    mesh.renderOrder=-1;
    this.mapGroup.add(mesh);
  }

  private buildResourceGeometry(map: WebMap, ids:Set<string>|null, y:number) {
    const parts:THREE.BufferGeometry[]=[];
    for(const resource of map.resources){
      if(ids&&!ids.has(resource.id))continue;
      if(!resource.boundary||resource.boundary.length<3)continue;
      const shape=new THREE.Shape(resource.boundary.map(point=>new THREE.Vector2(point.x,point.y)));
      const geometry=new THREE.ShapeGeometry(shape);
      geometry.rotateX(-Math.PI/2);geometry.translate(0,y,0);
      parts.push(geometry);
    }
    if(!parts.length)return null;
    const merged=mergeGeometries(parts,false);
    for(const part of parts)part.dispose();
    return merged;
  }

  private makeSceneLabel(text:string,kind:'vehicle'|'station'):SceneLabel {
    const element=document.createElement('div');element.className=`scene-map-label ${kind}`;element.textContent=text;element.title=text;
    this.labelLayer.append(element);
    const fontSize=kind==='vehicle'?12:11;
    const width=Math.min(220,Math.ceil(Array.from(text).reduce((sum,char)=>sum+(/[\u3400-\u9fff]/.test(char)?fontSize:fontSize*.58),0)+(kind==='vehicle'?12:9)));
    return {id:kind+':'+text,priority:kind==='vehicle'?100:5,element,kind,anchor:new THREE.Vector3(),active:true,width,height:kind==='vehicle'?21:18};
  }

  private layoutLabels(){
    const width=this.viewportWidth,height=this.viewportHeight;if(!width||!height)return;
    this.camera.updateMatrixWorld();const occupied:LabelRect[]=[];
    for(const entry of this.vehiclePool.values())if(entry.label.active){
      entry.group.updateMatrixWorld(true);const box=new THREE.Box3().setFromObject(entry.group.children[0]);
      const corners=[];
      for(const x of [box.min.x,box.max.x])for(const y of [box.min.y,box.max.y])for(const z of [box.min.z,box.max.z]){
        const p=new THREE.Vector3(x,y,z).project(this.camera);if(p.z>=-1&&p.z<=1)corners.push({x:(p.x+1)*width/2,y:(1-p.y)*height/2});
      }
      if(corners.length)occupied.push({left:Math.min(...corners.map(p=>p.x))-5,top:Math.min(...corners.map(p=>p.y))-5,
        right:Math.max(...corners.map(p=>p.x))+5,bottom:Math.max(...corners.map(p=>p.y))+5});
    }
    const zoom=this.camera.position.distanceTo(this.center)/Math.max(this.span,1);
    const autoLimit=zoom<.5?800:zoom<.8?480:260;
    const stationLabels=this.labelMode==='none'
      ? this.stationLabels.filter(label=>label.id===this.hoveredStation)
      : this.labelMode==='auto'?this.stationLabels.slice(0,autoLimit):this.stationLabels;
    if(this.hoveredStation&&!stationLabels.some(label=>label.id===this.hoveredStation)){
      const focused=this.stationLabels.find(label=>label.id===this.hoveredStation);if(focused)stationLabels.push(focused);
    }
    const labels=[...Array.from(this.vehiclePool.values()).map(e=>e.label),...stationLabels];
    const candidateIds=new Set(labels.map(label=>label.id));
    for(const label of this.stationLabels)if(!candidateIds.has(label.id)){
      label.element.hidden=true;if(label.leader)label.leader.hidden=true;
    }
    const inputs:MapLabelInput[]=[];
    const byId=new Map<string,SceneLabel>();
    for(const label of labels){
      const el=label.element;if(label.leader)label.leader.hidden=true;
      if(!label.active){el.hidden=true;continue;}
      const p=this.projected.copy(label.anchor).project(this.camera);
      if(p.z<-1||p.z>1||Math.abs(p.x)>1||Math.abs(p.y)>1){el.hidden=true;continue;}
      const focused=label.id===this.hoveredStation;
      el.classList.toggle('focused',focused);
      const x=(p.x+1)*width/2,y=(1-p.y)*height/2;
      inputs.push({id:label.id,x,y,width:label.width,height:label.height,
        kind:label.area?'area':label.kind,priority:label.priority,focused});
      byId.set(label.id,label);
    }
    const shown=new Set<string>();
    for(const placed of placeMapLabels(inputs,width,height,occupied,this.labelMode)){
      const label=byId.get(placed.id)!;
      shown.add(label.id);
      label.element.hidden=false;
      label.element.style.transform=`translate3d(${Math.round(placed.rect.left)}px,${Math.round(placed.rect.top)}px,0)`;
      if(placed.leader){
        if(!label.leader){label.leader=document.createElement('div');label.leader.className='scene-label-leader';this.labelLayer.append(label.leader);}
        label.leader.hidden=false;label.leader.style.width=`${placed.leader.length}px`;
        label.leader.style.transform=`translate3d(${placed.leader.x}px,${placed.leader.y}px,0) rotate(${placed.leader.angle}rad)`;
      }
    }
    for(const label of labels)if(!shown.has(label.id))label.element.hidden=true;
  }

  clearResourceFocus(){this.clearGroup(this.resourceFocus);}
  focusResource(id:string,bounds:{x:number;y:number;span:number}){
    const box=this.resourceBounds.get(id);if(!box)return;
    this.clearResourceFocus();
    const outline=new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(box.minX,.15,-box.minY),new THREE.Vector3(box.maxX,.15,-box.minY),new THREE.Vector3(box.maxX,.15,-box.maxY),new THREE.Vector3(box.minX,.15,-box.maxY)]),new THREE.LineBasicMaterial({color:this.themeColor('#075f72','#ffffff'),depthTest:false}));outline.renderOrder=10;this.resourceFocus.add(outline);
    const target=new THREE.Vector3(bounds.x,0,-bounds.y),direction=this.camera.position.clone().sub(this.controls.target).normalize();
    const distance=Math.max(4,bounds.span/(2*Math.tan(THREE.MathUtils.degToRad(this.camera.fov/2)))*1.5/Math.min(1,this.camera.aspect));
    this.controls.target.copy(target);this.camera.position.copy(target).add(direction.multiplyScalar(distance));this.controls.update();
    this.requestRender(true);
  }
  view(mode: 'top' | 'isometric') {
    this.mode = mode;
    const fit = 1 / Math.min(this.camera.aspect, 1);
    // Large factory maps need the camera to fit against the viewport width too.
    // Keeping the old vertical-only distance leaves more than half of a 1920px
    // wide dispatch canvas empty even though the map itself is complete.
    const largeMap = this.stationLabels.length > 500;
    const offset = largeMap
      ? (mode === 'top' ? new THREE.Vector3(0, this.span * 1.02, 0.01) : new THREE.Vector3(this.span * 0.64, this.span * 0.98, this.span * 0.76))
      : (mode === 'top' ? new THREE.Vector3(0, this.span * 1.75, 0.01) : new THREE.Vector3(this.span * 0.72, this.span * 1.15, this.span * 0.9));
    this.camera.position.copy(this.center).add(offset.multiplyScalar(fit));
    this.controls.target.copy(this.center); this.controls.update();
    this.requestRender(true);
  }

  zoom(factor: number) {
    const distance = this.camera.position.clone().sub(this.controls.target);
    const next = distance.length() * factor;
    if (next < 2 || next > 5000) return;
    this.camera.position.copy(this.controls.target).add(distance.multiplyScalar(factor));
    this.controls.update();
    this.requestRender(true);
  }

  private requestRender(layoutLabels:boolean) {
    this.needsRender=true;
    if(layoutLabels)this.needsLabelLayout=true;
    if(!this.frame)this.frame=requestAnimationFrame(this.animate);
  }

  private animate=()=>{
    this.frame=0;
    const moved=this.controls.update();
    if(moved){this.needsRender=true;this.needsLabelLayout=true;}
    const now=performance.now();
    if(this.needsLabelLayout&&now-this.lastLabelLayout>=70){
      this.layoutLabels();this.lastLabelLayout=now;this.needsLabelLayout=moved;
    }
    if(this.needsRender){
      this.displayAreas.layout(this.camera,this.viewportWidth,this.viewportHeight);
      this.renderer.render(this.scene,this.camera);
      this.needsRender=false;
    }
    if(moved||this.needsLabelLayout)this.requestRender(false);
  };

  private clearGroup(group: THREE.Group) {
    group.traverse(object => {
      const mesh = object as THREE.Mesh;
      mesh.geometry?.dispose();
      const materials = mesh.material ? (Array.isArray(mesh.material) ? mesh.material : [mesh.material]) : [];
      for (const material of materials) { (material as THREE.MeshBasicMaterial).map?.dispose(); material.dispose(); }
    });
    group.clear();
  }

  dispose() {
    this.displayAreas.dispose();
    cancelAnimationFrame(this.frame);
    this.resizeObserver.disconnect();
    this.themeObserver.disconnect();
    this.controls.removeEventListener('change',this.onControlsChange);
    this.controls.dispose();
    this.renderer.domElement.removeEventListener('pointerup', this.onPointer);
    this.renderer.domElement.removeEventListener('pointermove',this.onHover);
    this.renderer.domElement.removeEventListener('pointerleave',this.onLeave);
    this.clearGroup(this.mapGroup);
    this.clearGroup(this.vehicleGroup);this.clearResourceFocus();
    this.ringMaterial.dispose();
    this.vehiclePool.clear();
    this.renderer.dispose();
    this.renderer.domElement.remove();
    this.labelLayer.remove();
  }
}

interface SceneLabel {id:string;priority:number;area?:boolean;element:HTMLDivElement;leader?:HTMLDivElement;kind:'vehicle'|'station';anchor:THREE.Vector3;active:boolean;width:number;height:number}
