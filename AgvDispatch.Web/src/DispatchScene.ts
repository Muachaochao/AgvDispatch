import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { makePathObject, makeWaitingArea } from './vendor/agv-web-visible/MeshUtils';
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
  running: '#21dca3', waiting: '#eeb85c', idle: '#ad8aeb', charging: '#5aaee8',
  fault: '#f05c7e', offline: '#6c7d9d', unknown: '#8398b8'
};

export class DispatchScene {
  private scene = new THREE.Scene();
  private camera = new THREE.PerspectiveCamera(42, 1, 0.1, 5000);
  private renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
  private controls: OrbitControls;
  private mapGroup = new THREE.Group();
  private vehicleGroup = new THREE.Group();
  private resourceFocus=new THREE.Group();
  private edges = new Map<string, THREE.Group>();
  private resourceMeshes = new Map<string,THREE.Mesh>();
  private resizeObserver: ResizeObserver;
  private frame = 0;
  private sized=false;
  private mapKey = '';
  private center = new THREE.Vector3();
  private span = 25;
  private mode: 'top' | 'isometric' = 'top';
  private pick = new THREE.Raycaster();
  private onPointer: (event: PointerEvent) => void;
  private onHover: (event: PointerEvent) => void;
  private onLeave = () => { this.hoveredStation=''; };
  private hoveredStation='';
  private labelMode:MapLabelMode='auto';


  private vehiclePool = new Map<string, { group: THREE.Group; label: SceneLabel; ring: THREE.Mesh | null }>();
  private labelLayer=document.createElement('div');
  private displayAreas:DisplayAreasScene;
  private stationLabels:SceneLabel[]=[];
  private viewportWidth=0;
  private viewportHeight=0;
  private ringMaterial = new THREE.MeshBasicMaterial({ color: '#d6f5ff', side: THREE.DoubleSide });

  constructor(private container: HTMLElement, private onSelect: (id: string) => void, onAreaSelect:(area:DisplayArea)=>void=()=>{}) {
    this.scene.background = new THREE.Color('#0b1229');
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    container.appendChild(this.renderer.domElement);
    this.labelLayer.className='scene-label-layer';this.labelLayer.setAttribute('aria-label','车辆与站点标注');container.appendChild(this.labelLayer);
    this.displayAreas=new DisplayAreasScene(this.labelLayer,onAreaSelect);this.scene.add(this.displayAreas.group);
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = true;
    this.controls.maxPolarAngle = Math.PI / 2 - 0.03;
    this.scene.add(new THREE.HemisphereLight(0xc6e8ff, 0x26374b, 3));
    const light = new THREE.DirectionalLight(0xffffff, 2); light.position.set(10, 25, 10); this.scene.add(light);
    this.scene.add(this.mapGroup, this.vehicleGroup,this.resourceFocus);
    this.resizeObserver = new ResizeObserver(() => {
      const { width, height } = container.getBoundingClientRect();
      if (width < 1 || height < 1) return;
      this.viewportWidth=width;this.viewportHeight=height;this.renderer.setSize(width, height); this.camera.aspect = width / Math.max(height, 1); this.camera.updateProjectionMatrix(); if(!this.sized){this.sized=true;this.view(this.mode);}
    });
    this.resizeObserver.observe(container);
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
      const rect=this.renderer.domElement.getBoundingClientRect();
      const x=event.clientX-rect.left,y=event.clientY-rect.top;
      let nearest=14;this.hoveredStation='';
      for(const label of this.stationLabels){
        if(label.area)continue;
        const p=label.anchor.clone().project(this.camera);
        if(p.z<-1||p.z>1)continue;
        const distance=Math.hypot((p.x+1)*rect.width/2-x,(1-p.y)*rect.height/2-y);
        if(distance<nearest){nearest=distance;this.hoveredStation=label.id;}
      }
    };
    this.renderer.domElement.addEventListener('pointermove',this.onHover);
    this.renderer.domElement.addEventListener('pointerleave',this.onLeave);
    this.view('top');
    const animate = () => { this.frame = requestAnimationFrame(animate); this.controls.update();this.layoutLabels();this.displayAreas.layout(this.camera,this.viewportWidth,this.viewportHeight); this.renderer.render(this.scene, this.camera); };
    animate();
  }

  setDisplayAreas(areas:DisplayArea[],key:string){this.displayAreas.update(areas,key);}
  setLabelMode(mode:MapLabelMode){this.labelMode=mode;this.layoutLabels();}

  update(snapshot: Snapshot | null, selected = '', selectedEdge = '', focus?:TrafficFocus, now=Date.now()) {
    const map = snapshot?.map;
    const key = map ? `${snapshot?.source}:${map.id}:${map.version}:${map.definitionHash}` : '';
    if (key !== this.mapKey) { this.mapKey = key; this.drawMap(map ?? null); }

    const enabled=new Set(map?.edges.filter(e=>e.enabled).map(e=>e.id)??[]);
    for (const [id, group] of this.edges) group.traverse(object => {
      if (object instanceof THREE.Line) (object.material as THREE.LineBasicMaterial).color.set(focus?.edgeIds.includes(id) ? '#47e4b3' : id === selectedEdge ? '#ffffff' : enabled.has(id) ? '#3e8da8' : '#455365');
    });

    for(const [id,mesh] of this.resourceMeshes){const material=mesh.material as THREE.MeshBasicMaterial;const blocked=focus?.blocked.includes(id),held=focus?.held.includes(id);material.color.set(blocked?'#ff657e':held?'#ffbd59':'#e4aa4f');material.opacity=blocked ? .5 : held ? .35 : .12;}
    const activeIds = new Set<string>();
    if (snapshot) {
      for (const vehicle of snapshot.status?.agvs ?? []) {
        if (!mayPlot(snapshot, vehicle, now)) continue;
        activeIds.add(vehicle.vehicleId);
        const category = vehicleCategory(vehicle, snapshot.tasks?.items ?? [], true);
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
        entry.label.active=false;entry.label.element.hidden=true;entry.label.leader.hidden=true;
      }
    }
  }

  private drawMap(map: WebMap | null) {
    this.clearResourceFocus();this.clearGroup(this.mapGroup); this.edges.clear();this.resourceMeshes.clear();
    for (const [, entry] of this.vehiclePool) {
      if (entry.group.parent) this.vehicleGroup.remove(entry.group);
      entry.label.active=false;entry.label.element.hidden=true;entry.label.leader.hidden=true;
    }
    for(const entry of this.vehiclePool.values()){entry.label.element.remove();entry.label.leader.remove();}
    this.vehiclePool.clear();
    this.stationLabels.forEach(label=>{label.element.remove();label.leader.remove();});this.stationLabels=[];this.hoveredStation='';

    if (!map?.nodes.length) return;
    let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity;
    const add=(p:{x:number;y:number})=>{if(!Number.isFinite(p.x)||!Number.isFinite(p.y))return;minX=Math.min(minX,p.x);maxX=Math.max(maxX,p.x);minY=Math.min(minY,-p.y);maxY=Math.max(maxY,-p.y);};
    map.nodes.forEach(add);map.edges.forEach(e=>e.points.forEach(add));map.resources.forEach(r=>r.boundary?.forEach(add));map.areaAnnotations?.forEach(a=>a.boundary.forEach(add));if(!Number.isFinite(minX))return;
    this.center.set((minX + maxX) / 2, 0, (minY + maxY) / 2);
    this.span = Math.max(maxX - minX, maxY - minY, 15);
    const grid = new THREE.GridHelper(Math.ceil(this.span * 1.5), 30, '#294258', '#1b2d40');
    grid.position.copy(this.center); grid.position.y = -0.12; this.mapGroup.add(grid);

    for(const area of map.areaAnnotations??[]){
      if(area.boundary.length<3)continue;
      const shape=new THREE.Shape(area.boundary.map(p=>new THREE.Vector2(p.x,p.y)));
      const mesh=new THREE.Mesh(new THREE.ShapeGeometry(shape),new THREE.MeshBasicMaterial({color:area.fillColor,transparent:true,opacity:Math.min(area.fillOpacity,.12),side:THREE.DoubleSide,depthWrite:false}));
      mesh.rotation.x=-Math.PI/2;mesh.position.y=-.08;this.mapGroup.add(mesh);
      const points=[...area.boundary,area.boundary[0]!].map(p=>new THREE.Vector3(p.x,-.06,-p.y));
      this.mapGroup.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(points),new THREE.LineBasicMaterial({color:area.strokeColor})));
      const position=annotationLabel(area),label=this.makeSceneLabel(area.text,'station');
      label.id='area:'+area.id;label.anchor.set(position.x,.02,-position.y);label.area=true;label.element.style.color=area.strokeColor;label.element.classList.add('vendor-area');this.stationLabels.push(label);
    }

    for (const edge of map.edges) {
      const group = makePathObject(edge.id, edge.points.map(p => ({ x: p.x, y: 0, z: -p.y })), edge.enabled ? 0x3e8da8 : 0x455365);
      this.mapGroup.add(group); this.edges.set(edge.id, group);
      if (edge.enabled && edge.points.length > 1) {
        const index = Math.floor((edge.points.length - 1) / 2), a = edge.points[index], b = edge.points[index + 1];
        const dir = new THREE.Vector3(b.x - a.x, 0, -(b.y - a.y));
        if (dir.length() > 0.001) this.mapGroup.add(new THREE.ArrowHelper(dir.normalize(), new THREE.Vector3(a.x, 0.08, -a.y), 0.65, 0x65abc0, 0.22, 0.15));
      }
    }
    for (const node of map.nodes) {
      const marker=new THREE.Mesh(new THREE.RingGeometry(0.18,0.23,32),new THREE.MeshBasicMaterial({color:node.enabled?'#43d9b1':'#637083',side:THREE.DoubleSide}));
      marker.rotation.x=-Math.PI/2;marker.position.set(node.x,.04,-node.y);this.mapGroup.add(marker);
      const label=this.makeSceneLabel(node.agvSiteId??node.id,'station');label.anchor.set(node.x,.05,-node.y);
      label.id=node.id;label.priority=node.type==='Station'||node.type==='WaitingPoint'?20:10;

      label.element.classList.toggle('disabled',!node.enabled);this.stationLabels.push(label);
    }
    for (const resource of map.resources) {
      if (!resource.boundary?.length) continue;
      const shape = new THREE.Shape(resource.boundary.map(p => new THREE.Vector2(p.x, p.y)));
      const mesh = new THREE.Mesh(new THREE.ShapeGeometry(shape), new THREE.MeshBasicMaterial({ color: '#e4aa4f', transparent: true, opacity: 0.09, side: THREE.DoubleSide }));
      mesh.rotation.x = -Math.PI / 2; mesh.position.y = -0.03; this.mapGroup.add(mesh);this.resourceMeshes.set(resource.id,mesh);
    }
    for (const node of map.nodes.filter(n => n.type === 'WaitingPoint' || n.id === 'WAIT'))
      this.mapGroup.add(makeWaitingArea({ name: '', type: 'waiting', x: node.x, y: 0, z: -node.y, width: 1.15, height: 0.9 }));
    this.view(this.mode);
  }

  private makeSceneLabel(text:string,kind:'vehicle'|'station'):SceneLabel {
    const element=document.createElement('div');element.className=`scene-map-label ${kind}`;element.textContent=text;element.title=text;
    const leader=document.createElement('div');leader.className='scene-label-leader';this.labelLayer.append(leader,element);return {id:kind+':'+text,priority:kind==='vehicle'?100:5,element,leader,kind,anchor:new THREE.Vector3(),active:true};
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
    const labels=[...Array.from(this.vehiclePool.values()).map(e=>e.label),...this.stationLabels];
    const inputs:MapLabelInput[]=[];
    const byId=new Map<string,SceneLabel>();
    for(const label of labels){
      const el=label.element;label.leader.hidden=true;
      if(!label.active){el.hidden=true;continue;}
      const p=label.anchor.clone().project(this.camera);
      if(p.z<-1||p.z>1||Math.abs(p.x)>1||Math.abs(p.y)>1){el.hidden=true;continue;}
      // Reveal only for measurement. Placement either shows it near its node or
      // hides it for this zoom level; no distant edge-of-viewport fallback.
      el.hidden=false;
      const focused=label.id===this.hoveredStation;
      el.classList.toggle('focused',focused);
      const x=(p.x+1)*width/2,y=(1-p.y)*height/2;
      inputs.push({id:label.id,x,y,width:el.offsetWidth,height:el.offsetHeight,
        kind:label.area?'area':label.kind,priority:label.priority,focused});
      byId.set(label.id,label);el.hidden=true;
    }
    for(const placed of placeMapLabels(inputs,width,height,occupied,this.labelMode)){
      const label=byId.get(placed.id)!;
      label.element.hidden=false;
      label.element.style.transform=`translate3d(${Math.round(placed.rect.left)}px,${Math.round(placed.rect.top)}px,0)`;
      if(placed.leader){
        label.leader.hidden=false;label.leader.style.width=`${placed.leader.length}px`;
        label.leader.style.transform=`translate3d(${placed.leader.x}px,${placed.leader.y}px,0) rotate(${placed.leader.angle}rad)`;
      }
    }
  }

  clearResourceFocus(){this.clearGroup(this.resourceFocus);}
  focusResource(id:string,bounds:{x:number;y:number;span:number}){
    const mesh=this.resourceMeshes.get(id);if(!mesh)return;
    this.clearResourceFocus();const box=new THREE.Box3().setFromObject(mesh);
    const outline=new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(box.min.x,.15,box.min.z),new THREE.Vector3(box.max.x,.15,box.min.z),new THREE.Vector3(box.max.x,.15,box.max.z),new THREE.Vector3(box.min.x,.15,box.max.z)]),new THREE.LineBasicMaterial({color:'#ffffff',depthTest:false}));outline.renderOrder=10;this.resourceFocus.add(outline);
    const target=new THREE.Vector3(bounds.x,0,-bounds.y),direction=this.camera.position.clone().sub(this.controls.target).normalize();
    const distance=Math.max(4,bounds.span/(2*Math.tan(THREE.MathUtils.degToRad(this.camera.fov/2)))*1.5/Math.min(1,this.camera.aspect));
    this.controls.target.copy(target);this.camera.position.copy(target).add(direction.multiplyScalar(distance));this.controls.update();
  }
  view(mode: 'top' | 'isometric') {
    this.mode = mode;
    const fit = 1 / Math.min(this.camera.aspect, 1);
    this.camera.position.copy(this.center).add((mode === 'top' ? new THREE.Vector3(0, this.span * 1.75, 0.01) : new THREE.Vector3(this.span * 0.72, this.span * 1.15, this.span * 0.9)).multiplyScalar(fit));
    this.controls.target.copy(this.center); this.controls.update();
  }

  zoom(factor: number) {
    const distance = this.camera.position.clone().sub(this.controls.target);
    const next = distance.length() * factor;
    if (next < 2 || next > 5000) return;
    this.camera.position.copy(this.controls.target).add(distance.multiplyScalar(factor));
    this.controls.update();
  }

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

interface SceneLabel {id:string;priority:number;area?:boolean;element:HTMLDivElement;leader:HTMLDivElement;kind:'vehicle'|'station';anchor:THREE.Vector3;active:boolean}
