import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { makePoint, makePathObject, makeWaitingArea, makeLabel } from './vendor/agv-web-visible/MeshUtils';
import type { Snapshot, WebMap } from './types';
import { mayPlot } from './state';
import { vehicleCategory } from './dashboard';
import type {TrafficFocus} from './traffic-focus';

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

  private vehiclePool = new Map<string, { group: THREE.Group; label: THREE.Object3D; ring: THREE.Mesh | null }>();
  private bodyGeometry = new THREE.BoxGeometry(1.2, 0.6, 0.85);
  private ringGeometry = new THREE.RingGeometry(0.78, 0.85, 32);
  private ringMaterial = new THREE.MeshBasicMaterial({ color: '#d6f5ff', side: THREE.DoubleSide });

  constructor(private container: HTMLElement, private onSelect: (id: string) => void) {
    this.scene.background = new THREE.Color('#0b1229');
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    container.appendChild(this.renderer.domElement);
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = true;
    this.controls.maxPolarAngle = Math.PI / 2 - 0.03;
    this.scene.add(new THREE.HemisphereLight(0xc6e8ff, 0x26374b, 3));
    const light = new THREE.DirectionalLight(0xffffff, 2); light.position.set(10, 25, 10); this.scene.add(light);
    this.scene.add(this.mapGroup, this.vehicleGroup,this.resourceFocus);
    this.resizeObserver = new ResizeObserver(() => {
      const { width, height } = container.getBoundingClientRect();
      if (width < 1 || height < 1) return;
      this.renderer.setSize(width, height); this.camera.aspect = width / Math.max(height, 1); this.camera.updateProjectionMatrix(); if(!this.sized){this.sized=true;this.view(this.mode);}
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
      }
    };
    this.renderer.domElement.addEventListener('pointerup', this.onPointer);
    this.view('top');
    const animate = () => { this.frame = requestAnimationFrame(animate); this.controls.update(); this.renderer.render(this.scene, this.camera); };
    animate();
  }

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
          const body = new THREE.Mesh(this.bodyGeometry, material);
          group.add(body);
          const heading = new THREE.ArrowHelper(new THREE.Vector3(1, 0, 0), new THREE.Vector3(0.2, 0.5, 0), 1, '#ffffff', 0.3, 0.2);
          group.add(heading);
          const label = makeLabel({ name: vehicle.vehicleId, type: 'vehicle', x: 0, y: 0, z: -1.1 });
          label.scale.multiplyScalar(0.65);
          entry = { group, label, ring: null };
          this.vehiclePool.set(vehicle.vehicleId, entry);
        }

        entry.group.position.set(vehicle.x!, 0.4, -vehicle.y!);
        entry.group.rotation.y = vehicle.angle ?? 0;
        const body = entry.group.children[0] as THREE.Mesh;
        (body.material as THREE.MeshStandardMaterial).color.set(color);

        entry.label.position.set(vehicle.x!, 0.4, -vehicle.y! - 1.1);

        const role=focus?.blockers.includes(vehicle.vehicleId)?'blocker':focus?.vehicleId===vehicle.vehicleId?'task':vehicle.vehicleId===selected?'selected':'';
        if (role && !entry.ring) {
          const ring = new THREE.Mesh(this.ringGeometry, this.ringMaterial.clone());
          ring.rotation.x = -Math.PI / 2;
          ring.position.y = -0.29;
          entry.group.add(ring);
          entry.ring = ring;
        } else if (!role && entry.ring) {
          entry.group.remove(entry.ring); (entry.ring.material as THREE.Material).dispose();
          entry.ring = null;
        }

        if(entry.ring)(entry.ring.material as THREE.MeshBasicMaterial).color.set(role==='blocker'?'#ff657e':role==='task'?'#47e4b3':'#d6f5ff');
        if (!entry.group.parent) this.vehicleGroup.add(entry.group);
        if (!entry.label.parent) this.vehicleGroup.add(entry.label);
      }
    }

    for (const [id, entry] of this.vehiclePool) {
      if (!activeIds.has(id)) {
        if (entry.group.parent) this.vehicleGroup.remove(entry.group);
        if (entry.label.parent) this.vehicleGroup.remove(entry.label);
      }
    }
  }

  private drawMap(map: WebMap | null) {
    this.clearResourceFocus();this.clearGroup(this.mapGroup); this.edges.clear();this.resourceMeshes.clear();
    for (const [, entry] of this.vehiclePool) {
      if (entry.group.parent) this.vehicleGroup.remove(entry.group);
      if (entry.label.parent) this.vehicleGroup.remove(entry.label);
    }
    this.vehiclePool.clear();

    if (!map?.nodes.length) return;
    let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity;
    const add=(p:{x:number;y:number})=>{if(!Number.isFinite(p.x)||!Number.isFinite(p.y))return;minX=Math.min(minX,p.x);maxX=Math.max(maxX,p.x);minY=Math.min(minY,-p.y);maxY=Math.max(maxY,-p.y);};
    map.nodes.forEach(add);map.edges.forEach(e=>e.points.forEach(add));map.resources.forEach(r=>r.boundary?.forEach(add));if(!Number.isFinite(minX))return;
    this.center.set((minX + maxX) / 2, 0, (minY + maxY) / 2);
    this.span = Math.max(maxX - minX, maxY - minY, 15);
    const grid = new THREE.GridHelper(Math.ceil(this.span * 1.5), 30, '#294258', '#1b2d40');
    grid.position.copy(this.center); grid.position.y = -0.12; this.mapGroup.add(grid);

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
      const markerGroup = makePoint({ name: node.agvSiteId ?? node.id, type: 'station', x: node.x, y: 0, z: -node.y, color: node.enabled ? '#43d9b1' : '#637083' });
      const marker = markerGroup.children[0] as THREE.Mesh; marker.geometry.dispose(); marker.geometry = new THREE.RingGeometry(0.18, 0.23, 24); marker.rotation.x = -Math.PI / 2;
      const label = markerGroup.children[1]; label.scale.multiplyScalar(0.65); label.position.z += 0.8;
      this.mapGroup.add(markerGroup);
    }
    for (const resource of map.resources) {
      if (!resource.boundary?.length) continue;
      const shape = new THREE.Shape(resource.boundary.map(p => new THREE.Vector2(p.x, p.y)));
      const mesh = new THREE.Mesh(new THREE.ShapeGeometry(shape), new THREE.MeshBasicMaterial({ color: '#e4aa4f', transparent: true, opacity: 0.12, side: THREE.DoubleSide }));
      mesh.rotation.x = -Math.PI / 2; mesh.position.y = -0.03; this.mapGroup.add(mesh);this.resourceMeshes.set(resource.id,mesh);
    }
    for (const node of map.nodes.filter(n => n.type === 'WaitingPoint' || n.id === 'WAIT'))
      this.mapGroup.add(makeWaitingArea({ name: '', type: 'waiting', x: node.x, y: 0, z: -node.y, width: 2, height: 1.8 }));
    this.view(this.mode);
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
    cancelAnimationFrame(this.frame);
    this.resizeObserver.disconnect();
    this.controls.dispose();
    this.renderer.domElement.removeEventListener('pointerup', this.onPointer);
    this.clearGroup(this.mapGroup);
    this.clearGroup(this.vehicleGroup);this.clearResourceFocus();
    this.bodyGeometry.dispose();
    this.ringGeometry.dispose();
    this.ringMaterial.dispose();
    for (const [, entry] of this.vehiclePool) {
      const body = entry.group.children[0] as THREE.Mesh;
      (body.material as THREE.Material).dispose();
    }
    this.vehiclePool.clear();
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }
}
