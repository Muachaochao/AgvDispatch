import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { makePoint, makePathObject, makeWaitingArea, makeLabel } from './vendor/agv-web-visible/MeshUtils';
import type { Snapshot, WebMap } from './types';
import { mayPlot } from './state';
import { vehicleCategory } from './dashboard';

export class DispatchScene {
  private scene = new THREE.Scene();
  private camera = new THREE.PerspectiveCamera(42, 1, 0.1, 5000);
  private renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
  private controls: OrbitControls;
  private mapGroup = new THREE.Group();
  private vehicleGroup = new THREE.Group();
  private edges = new Map<string, THREE.Group>();
  private resizeObserver: ResizeObserver;
  private frame = 0;
  private key = '';
  private center = new THREE.Vector3();
  private span = 25;
  private mode: 'top' | 'isometric' = 'top';
  private pick = new THREE.Raycaster();
  private onPointer: (event: PointerEvent) => void;

  constructor(private container: HTMLElement, private onSelect: (id: string) => void) {
    this.scene.background = new THREE.Color('#0b1229');
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    container.appendChild(this.renderer.domElement);
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = true;
    this.controls.maxPolarAngle = Math.PI / 2 - 0.03;
    this.scene.add(new THREE.HemisphereLight(0xc6e8ff, 0x26374b, 3));
    const light = new THREE.DirectionalLight(0xffffff, 2); light.position.set(10, 25, 10); this.scene.add(light);
    this.scene.add(this.mapGroup, this.vehicleGroup);
    this.resizeObserver = new ResizeObserver(() => {
      const { width, height } = container.getBoundingClientRect();
      if (width < 1 || height < 1) return;
      this.renderer.setSize(width, height); this.camera.aspect = width / Math.max(height, 1); this.camera.updateProjectionMatrix(); this.view(this.mode);
    });
    this.resizeObserver.observe(container);
    this.onPointer = event => {
      const rect = this.renderer.domElement.getBoundingClientRect();
      this.pick.setFromCamera(new THREE.Vector2((event.clientX - rect.left) / rect.width * 2 - 1, -(event.clientY - rect.top) / rect.height * 2 + 1), this.camera);
      const hit = this.pick.intersectObjects(this.vehicleGroup.children, true)[0];
      if (hit) { let object: THREE.Object3D | null = hit.object; while (object && !object.userData.vehicleId) object = object.parent;
        if (object) this.onSelect(object.userData.vehicleId); }
    };
    this.renderer.domElement.addEventListener('pointerup', this.onPointer);
    this.view('top');
    const animate = () => { this.frame = requestAnimationFrame(animate); this.controls.update(); this.renderer.render(this.scene, this.camera); }; animate();
  }

  update(snapshot: Snapshot | null, selected = '', selectedEdge = '') {
    const map = snapshot?.map;
    const key = map ? JSON.stringify([snapshot?.source, map.id, map.version, map.nodes, map.edges, map.resources]) : '';
    if (key !== this.key) { this.key = key; this.drawMap(map ?? null); }
    for (const [id, group] of this.edges) group.traverse(object => {
      if (object instanceof THREE.Line) (object.material as THREE.LineBasicMaterial).color.set(id === selectedEdge ? '#ffffff' : '#3e8da8');
    });
    this.clear(this.vehicleGroup);
    if (!snapshot) return;
    for (const vehicle of snapshot.status?.agvs ?? []) {
      if (!mayPlot(snapshot, vehicle)) continue;
      const group = new THREE.Group(); group.position.set(vehicle.x!, 0.4, -vehicle.y!); group.rotation.y = vehicle.angle ?? 0;
      group.userData.vehicleId = vehicle.vehicleId;
      const colors = { running: '#21dca3', waiting: '#eeb85c', idle: '#ad8aeb', charging: '#5aaee8', fault: '#f05c7e', offline: '#6c7d9d', unknown: '#8398b8' };
      const material = new THREE.MeshStandardMaterial({ color: colors[vehicleCategory(vehicle, snapshot.tasks?.items ?? [], true)], roughness: 0.4 });
      const body = new THREE.Mesh(new THREE.BoxGeometry(1.2, 0.6, 0.85), material); group.add(body);
      if (vehicle.vehicleId === selected) { const ring = new THREE.Mesh(new THREE.RingGeometry(0.78,0.85,32),new THREE.MeshBasicMaterial({color:'#d6f5ff',side:THREE.DoubleSide}));ring.rotation.x=-Math.PI/2;ring.position.y=-0.29;group.add(ring); }
      const heading = new THREE.ArrowHelper(new THREE.Vector3(1, 0, 0), new THREE.Vector3(0.2, 0.5, 0), 1, '#ffffff', 0.3, 0.2); group.add(heading);
      this.vehicleGroup.add(group);
      const label = makeLabel({ name: vehicle.vehicleId, type: 'vehicle', x: vehicle.x!, y: 0, z: -vehicle.y! - 1.1 });
      label.scale.multiplyScalar(0.65); this.vehicleGroup.add(label);
    }
  }

  private drawMap(map: WebMap | null) {
    this.clear(this.mapGroup); this.edges.clear();
    if (!map?.nodes.length) return;
    const xs = map.nodes.map(n => n.x), ys = map.nodes.map(n => -n.y);
    if (![...xs, ...ys].every(Number.isFinite)) return;
    const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
    this.center.set((minX + maxX) / 2, 0, (minY + maxY) / 2); this.span = Math.max(maxX - minX, maxY - minY, 15);
    const grid = new THREE.GridHelper(Math.ceil(this.span * 1.5), 30, '#294258', '#1b2d40'); grid.position.copy(this.center); grid.position.y = -0.12; this.mapGroup.add(grid);
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
      const marker = markerGroup.children[0] as THREE.Mesh; marker.geometry.dispose(); marker.geometry = new THREE.RingGeometry(0.18,0.23,24); marker.rotation.x=-Math.PI/2;
      const label = markerGroup.children[1]; label.scale.multiplyScalar(0.65); label.position.z += 0.8;
      this.mapGroup.add(markerGroup);
    }
    for (const resource of map.resources) {
      if (!resource.boundary?.length) continue;
      const shape = new THREE.Shape(resource.boundary.map(p => new THREE.Vector2(p.x, p.y)));
      const mesh = new THREE.Mesh(new THREE.ShapeGeometry(shape), new THREE.MeshBasicMaterial({ color: '#e4aa4f', transparent: true, opacity: 0.12, side: THREE.DoubleSide }));
      mesh.rotation.x = -Math.PI / 2; mesh.position.y = -0.03; this.mapGroup.add(mesh);
    }
    // Reuse the upstream station area helper for visual waiting pads, not traffic authorization.
    for (const node of map.nodes.filter(n => n.type === 'WaitingPoint' || n.id === 'WAIT'))
      this.mapGroup.add(makeWaitingArea({ name: '', type: 'waiting', x: node.x, y: 0, z: -node.y, width: 2, height: 1.8 }));
    this.view(this.mode);
  }

  view(mode: 'top' | 'isometric') {
    this.mode = mode;
    const fit = 1 / Math.min(this.camera.aspect, 1);
    this.camera.position.copy(this.center).add((mode === 'top' ? new THREE.Vector3(0, this.span * 1.75, 0.01) : new THREE.Vector3(this.span * 0.72, this.span * 1.15, this.span * 0.9)).multiplyScalar(fit));
    this.controls.target.copy(this.center); this.controls.update();
  }
  zoom(factor: number) { const distance=this.camera.position.clone().sub(this.controls.target);const next=distance.length()*factor;
    if(next<2||next>5000)return;this.camera.position.copy(this.controls.target).add(distance.multiplyScalar(factor));this.controls.update(); }
  private clear(group: THREE.Group) {
    group.traverse(object => {
      const mesh = object as THREE.Mesh;
      mesh.geometry?.dispose();
      const materials = mesh.material ? (Array.isArray(mesh.material) ? mesh.material : [mesh.material]) : [];
      for (const material of materials) { (material as THREE.MeshBasicMaterial).map?.dispose(); material.dispose(); }
    }); group.clear();
  }
  dispose() { cancelAnimationFrame(this.frame); this.resizeObserver.disconnect(); this.controls.dispose();
    this.renderer.domElement.removeEventListener('pointerup', this.onPointer); this.clear(this.mapGroup); this.clear(this.vehicleGroup); this.renderer.dispose(); this.renderer.domElement.remove(); }
}
