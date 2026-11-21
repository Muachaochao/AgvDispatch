<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import type { RoadNetPayload } from "@/api/roadnet";
import type { AgvView } from "@/types";
import MapNavigationToolbar from "./MapNavigationToolbar.vue";
import MapOverview from "./MapOverview.vue";
import { mapBounds, mapNavigationItems, toggleMapFullscreen, type MapBounds, type MapLocation, type MapPoint } from "@/integration/map-navigation";

const props = defineProps<{
  roadnet: RoadNetPayload | null;
  agvs?: AgvView[];
  showGrid?: boolean;
  observed?: boolean;
  selectable?: boolean;
  vehicleSelectable?: boolean;
  exactGeometry?: boolean;
  selectedNodeIds?: string[];
}>();

const emit = defineEmits<{
  nodeSelect: [node: { id: string; type: string }];
  vehicleSelect: [id: string];
}>();

const container = ref<HTMLDivElement | null>(null);
const surface = ref<HTMLDivElement | null>(null);
const staticItems = shallowRef<MapLocation[]>([]);
const located = shallowRef<MapLocation | null>(null);
const zoom = ref(100);
const showLabels = ref(false);
const marked = ref(false);
const dragging = ref(false);
const viewport = shallowRef<MapBounds | null>(null);
const items = computed<MapLocation[]>(() => [...staticItems.value, ...(props.agvs ?? []).map(car => ({
  id: car.id, x: car.x, y: car.y, kind: "vehicle" as const,
}))]);
const locationGroup = new THREE.Group();
const labelsGroup = new THREE.Group();
const ground = new THREE.Plane(new THREE.Vector3(0, 1, 0));
const groundRay = new THREE.Raycaster();
let defaultDistance = 1;
let overviewDirty = true, lastOverview = 0;

let scene: THREE.Scene;
let camera: THREE.PerspectiveCamera;
let renderer: THREE.WebGLRenderer;
let controls: OrbitControls;
let grid: THREE.GridHelper | null = null;
let raf = 0;
let resizeObserver: ResizeObserver | null = null;

const netGroup = new THREE.Group();
const agvGroup = new THREE.Group();
const pathGroup = new THREE.Group();
const rawNodes = new Map<string, { id: string; x: number; y: number; type: string }>();
const displayNodes = new Map<string, { id: string; x: number; y: number; type: string }>();
const nodeMeshes = new Map<string, THREE.Mesh>();
const nodeVisuals = new Map<string, THREE.Mesh>();
let renderedPoints: Array<{ x: number; y: number }> = [];
const raycaster = new THREE.Raycaster();
const pointer = new THREE.Vector2();
let pointerStart: { id: number; x: number; y: number } | null = null;
const groundHit = new THREE.Vector3();

interface AgvMesh {
  group: THREE.Group;
  box: THREE.Mesh;
  target: THREE.Vector3;
  targetHeading: number;
  status: string;
}

const agvMeshes = new Map<string, AgvMesh>();

const NODE_COLORS: Record<string, number> = {
  CHARGER: 0x67c23a,
  WORKSTATION: 0x409eff,
  BUFFER: 0xe6a23c,
  JUNCTION: 0x909399,
};

const STATUS_COLORS: Record<string, number> = {
  MOVING: 0x409eff,
  WAITING: 0xf56c6c,
  LOADING: 0xe6a23c,
  IDLE: 0x909399,
  CHARGING: 0x67c23a,
  OFFLINE: 0x65748a,
  FAULT: 0xef7692,
  BLOCKED: 0xee9c63,
  DONE: 0x303133,
};

function init() {
  const el = container.value;
  if (!el) return;

  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0b1526);
  scene.fog = new THREE.Fog(0x0b1526, 90, 220);

  camera = new THREE.PerspectiveCamera(
    50,
    el.clientWidth / el.clientHeight,
    0.1,
    1000,
  );
  camera.position.set(25, 32, 42);

  renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(el.clientWidth, el.clientHeight);
  el.appendChild(renderer.domElement);
  renderer.domElement.addEventListener("pointerdown", onPointerDown);
  renderer.domElement.addEventListener("pointermove", onPointerMove);
  renderer.domElement.addEventListener("pointerup", onPointerUp);
  renderer.domElement.addEventListener("pointercancel", cancelPointer);
  renderer.domElement.addEventListener("lostpointercapture", cancelPointer);

  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.maxPolarAngle = Math.PI / 2.05;
  controls.target.set(15, 0, 12);
  controls.addEventListener("change", markOverviewDirty);

  scene.add(new THREE.AmbientLight(0xffffff, 0.85));
  const dir = new THREE.DirectionalLight(0xffffff, 1.1);
  dir.position.set(30, 60, 20);
  scene.add(dir);

  scene.add(netGroup);
  scene.add(agvGroup);
  scene.add(pathGroup);
  scene.add(locationGroup);
  scene.add(labelsGroup);

  resizeObserver = new ResizeObserver(resize);
  resizeObserver.observe(el);

  animate();
}

function updateNodeSelection() {
  const selected = new Set(props.selectedNodeIds ?? []);
  nodeVisuals.forEach((mesh, id) => {
    const material = mesh.material as THREE.MeshStandardMaterial;
    const active = marked.value || selected.has(id) || (located.value?.kind === "node" && located.value.id === id);
    material.emissiveIntensity = active ? 1.3 : 0.25;
    mesh.scale.setScalar(active ? 1.7 : 1);
  });
}

function onPointerDown(event: PointerEvent) {
  dragging.value = true;
  pointerStart = (props.selectable || props.vehicleSelectable) && event.button === 0 && event.isPrimary
    ? { id: event.pointerId, x: event.clientX, y: event.clientY } : null;
}
function onPointerMove(event: PointerEvent) {
  if (pointerStart?.id !== event.pointerId) return;
  if (Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y) > 5) pointerStart = null;
}
function cancelPointer() {
  dragging.value = false;
  pointerStart = null;
}
function onPointerUp(event: PointerEvent) {
  onPointerMove(event);
  const shouldSelect = event.button === 0 && pointerStart?.id === event.pointerId;
  cancelPointer();
  if (!shouldSelect) return;
  const rect = renderer.domElement.getBoundingClientRect();
  if (props.vehicleSelectable) {
    let nearest: string | undefined, distance = 16;
    for (const [id, mesh] of agvMeshes) {
      const point = groundHit.copy(mesh.group.position).project(camera);
      if (point.z < -1 || point.z > 1) continue;
      const pixels = Math.hypot(event.clientX - rect.left - (point.x + 1) * rect.width / 2,
        event.clientY - rect.top - (1 - point.y) * rect.height / 2);
      if (pixels < distance) { nearest = id; distance = pixels; }
    }
    if (nearest) return emit("vehicleSelect", nearest);
  }
  if (!props.selectable) return;
  pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
  pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
  camera.updateMatrixWorld();
  raycaster.setFromCamera(pointer, camera);
  const hit = raycaster.intersectObjects([...nodeMeshes.values()], false)[0];
  let id = hit?.object.userData.nodeId as string | undefined;
  let type = hit?.object.userData.nodeType as string | undefined;
  if (!id) {
    let nearestDistance = 48;
    for (const [nodeId, mesh] of nodeMeshes) {
      const projected = mesh.position.clone().project(camera);
      const distance = Math.hypot(event.clientX - rect.left - ((projected.x + 1) / 2) * rect.width,
        event.clientY - rect.top - ((1 - projected.y) / 2) * rect.height);
      if (distance < nearestDistance) { nearestDistance = distance; id = nodeId; type = mesh.userData.nodeType as string; }
    }
  }
  if (id && type) emit("nodeSelect", { id, type });
}

function resize() {
  const el = container.value;
  if (!el || !renderer) return;
  const width = el.clientWidth;
  const height = el.clientHeight;
  if (width === 0 || height === 0) return;
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
  renderer.setSize(width, height);
  markOverviewDirty();
}

function animate(now = 0) {
  raf = requestAnimationFrame(animate);
  const k = props.observed ? 1 : 0.35;
  agvMeshes.forEach((mesh) => {
    if (!props.observed && mesh.group.position.distanceToSquared(mesh.target) > 0.000001) {
      mesh.group.position.lerp(mesh.target, k);
    } else {
      mesh.group.position.copy(mesh.target);
    }
    const current = mesh.group.rotation.y;
    const delta = Math.atan2(
      Math.sin(mesh.targetHeading - current),
      Math.cos(mesh.targetHeading - current),
    );
    mesh.group.rotation.y = current + delta * k;
  });
  controls?.update();
  if (located.value?.kind === "vehicle") {
    const vehicle = agvMeshes.get(located.value.id);
    if (vehicle) locationGroup.position.set(vehicle.group.position.x, 0, vehicle.group.position.z);
  }
  if (overviewDirty && now - lastOverview >= 130) {
    lastOverview = now;
    overviewDirty = false;
    updateOverview();
  }
  renderer?.render(scene, camera);
  // Live observations move in one-second steps and do not need 60 idle renders per second.
  if (props.observed && !overviewDirty && agvMeshes.size && [...agvMeshes.values()].every(mesh =>
    mesh.group.position.distanceToSquared(mesh.target) < 0.000001)) {
    cancelAnimationFrame(raf);
    raf = 0;
  }
}
function requestRender() {
  if (!raf && renderer) raf = requestAnimationFrame(animate);
}
function markOverviewDirty() { overviewDirty = true; requestRender(); }

function updateOverview() {
  if (!camera || !controls) return;
  zoom.value = Math.round(defaultDistance / camera.position.distanceTo(controls.target) * 100);
  camera.updateMatrixWorld();
  const corners: MapPoint[] = [];
  for (const x of [-1, 1]) for (const y of [-1, 1]) {
    groundRay.setFromCamera(new THREE.Vector2(x, y), camera);
    const hit = groundRay.ray.intersectPlane(ground, new THREE.Vector3());
    if (hit) corners.push({ x: hit.x, y: hit.z });
  }
  viewport.value = corners.length === 4 ? mapBounds(corners) : null;
}

function centerAt(point: MapPoint) {
  if (!controls) return;
  const next = new THREE.Vector3(point.x, 0, point.y);
  camera.position.add(next.clone().sub(controls.target));
  controls.target.copy(next);
  controls.update();
  markOverviewDirty();
  if (showLabels.value) refreshLabels();
}

function zoomBy(factor: number) {
  if (!controls) return;
  const offset = camera.position.clone().sub(controls.target);
  offset.setLength(THREE.MathUtils.clamp(offset.length() / factor, controls.minDistance, controls.maxDistance));
  camera.position.copy(controls.target).add(offset);
  controls.update();
  markOverviewDirty();
}

function pan(x: number, y: number) {
  if (!controls) return;
  const step = camera.position.distanceTo(controls.target) * 0.075;
  centerAt({ x: controls.target.x + x * step, y: controls.target.z + y * step });
}

function locate(item: MapLocation) {
  located.value = item;
  marked.value = false;
  centerAt(item);
  const distance = camera.position.distanceTo(controls.target);
  if (distance > defaultDistance / 2) zoomBy(distance / (defaultDistance / 2));
  disposeGroup(locationGroup);
  locationGroup.position.set(item.x, 0, item.y);
  // A separate marker also locates ordinary junctions hidden in a dense map.
  const marker = new THREE.Mesh(new THREE.RingGeometry(0.48, 0.7, 24), new THREE.MeshBasicMaterial({ color: 0xdc9bfb, side: THREE.DoubleSide, depthTest: false }));
  marker.rotation.x = -Math.PI / 2;
  marker.position.y = 0.08;
  locationGroup.add(marker);
  const label = makeLabel(item.id, true);
  label.position.y = 1.05;
  locationGroup.add(label);
  updateNodeSelection();
}

function clearLocation() {
  located.value = null;
  marked.value = false;
  disposeGroup(locationGroup);
  updateNodeSelection();
}

function refreshLabels() {
  disposeGroup(labelsGroup);
  if (!showLabels.value || !controls) return;
  const target = controls.target;
  const nearest = staticItems.value.filter(item => item.kind === "node")
    .sort((a, b) => Math.hypot(a.x - target.x, a.y - target.z) - Math.hypot(b.x - target.x, b.y - target.z))
    .slice(0, 30);
  for (const item of nearest) {
    const label = makeLabel(item.id);
    label.position.set(item.x, 0.8, item.y);
    label.scale.set(1.8, 0.9, 1);
    labelsGroup.add(label);
  }
}

function disposeMaterial(material: THREE.Material | THREE.Material[]) {
  const list = Array.isArray(material) ? material : [material];
  for (const item of list) {
    const map = (item as THREE.MeshStandardMaterial).map;
    map?.dispose();
    item.dispose();
  }
}

function disposeGroup(group: THREE.Group) {
  group.traverse((obj) => {
    if (obj instanceof THREE.Mesh || obj instanceof THREE.Line) {
      obj.geometry.dispose();
      disposeMaterial(obj.material);
    } else if (obj instanceof THREE.Sprite) {
      // 标签精灵没有 geometry，但持有 CanvasTexture，必须一起释放
      disposeMaterial(obj.material);
    }
  });
  group.clear();
}

function rebuildRoadNet(net: RoadNetPayload | null) {
  if (!scene) return;
  disposeGroup(netGroup);
  nodeMeshes.clear();
  nodeVisuals.clear();
  if (!net || net.nodes.length === 0) return;

  const nodeMap = new Map(net.nodes.map((n) => [n.id, n]));
  const dense = net.nodes.length > 200;
  const lineBatches = new Map<number, { vertices: number[]; opacity: number }>();
  const arrowBatches = new Map<number, THREE.Matrix4[]>();
  const arrowUp = new THREE.Vector3(0, 1, 0);
  const arrowRotation = new THREE.Quaternion();
  rawNodes.clear();
  displayNodes.clear();
  renderedPoints = [];
  for (const node of net.nodes) {
    const value = { id: node.id, x: node.x, y: node.y, type: node.type };
    rawNodes.set(node.id, value);
    displayNodes.set(node.id, { ...value });
  }
  if (dense && !props.exactGeometry) {
    for (const node of net.nodes) {
      if (node.type !== "WORKSTATION") continue;
      const connection = net.edges.find((edge) => {
        if (edge.from !== node.id && edge.to !== node.id) return false;
        const other = nodeMap.get(edge.from === node.id ? edge.to : edge.from);
        return other?.type === "JUNCTION";
      });
      if (!connection) continue;
      const junction = nodeMap.get(
        connection.from === node.id ? connection.to : connection.from,
      );
      if (!junction) continue;
      const dx = node.x - junction.x;
      const dy = node.y - junction.y;
      const length = Math.hypot(dx, dy);
      const minimumDisplayLength = 1.2;
      if (length >= minimumDisplayLength) continue;
      const ux = length > 1e-6 ? dx / length : 0;
      const uy = length > 1e-6 ? dy / length : (node.y >= junction.y ? 1 : -1);
      displayNodes.set(node.id, {
        ...displayNodes.get(node.id)!,
        x: junction.x + ux * minimumDisplayLength,
        y: junction.y + uy * minimumDisplayLength,
      });
    }
  }

  for (const edge of net.edges) {
    const from = nodeMap.get(edge.from);
    const to = nodeMap.get(edge.to);
    if (!from || !to) continue;
    const displayFrom = displayNodes.get(edge.from) ?? from;
    const displayTo = displayNodes.get(edge.to) ?? to;
    const isStationBranch =
      from.type === "WORKSTATION" || to.type === "WORKSTATION";
    const isServiceBranch =
      ["WAIT", "BUFFER", "CHARGER"].includes(from.type) ||
      ["WAIT", "BUFFER", "CHARGER"].includes(to.type) ||
      [from.id, to.id].some((id) => /^(WAIT|CHARGE)/.test(id));
    const lineColor = isStationBranch
      ? 0x35d6c4
      : isServiceBranch
        ? 0xe7aa42
        : 0x275580;
    const lineOpacity = isStationBranch || isServiceBranch ? 1 : 0.82;
    const lineHeight = isStationBranch || isServiceBranch ? 0.12 : 0.06;
    let batch = lineBatches.get(lineColor);
    if (!batch) {
      batch = { vertices: [], opacity: lineOpacity };
      lineBatches.set(lineColor, batch);
    }
    const vertices = batch.vertices;

    if (edge.points?.length) {
      const points = edge.points.map((point) => ({ ...point }));
      points[0] = { x: displayFrom.x, y: displayFrom.y };
      points[points.length - 1] = { x: displayTo.x, y: displayTo.y };
      renderedPoints.push(...points);
      for (let index = 1; index < points.length; index++) {
        const previous = points[index - 1]!, point = points[index]!;
        vertices.push(previous.x, lineHeight, previous.y, point.x, lineHeight, point.y);
      }
      continue;
    }
    const start = new THREE.Vector3(displayFrom.x, lineHeight, displayFrom.y);
    const end = new THREE.Vector3(displayTo.x, lineHeight, displayTo.y);
    renderedPoints.push({ x: start.x, y: start.z }, { x: end.x, y: end.z });
    const dirVec = end.clone().sub(start);
    const len = dirVec.length();
    if (len < 0.01) continue;

    const addLine = (a: THREE.Vector3, b: THREE.Vector3) => {
      vertices.push(a.x, a.y, a.z, b.x, b.y, b.z);
    };
    const addArrow = (position: THREE.Vector3, direction: THREE.Vector3) => {
      if (dense) return;
      const arrows = arrowBatches.get(lineColor) ?? [];
      arrowRotation.setFromUnitVectors(arrowUp, direction.clone().normalize());
      arrows.push(new THREE.Matrix4().makeRotationFromQuaternion(arrowRotation).setPosition(position));
      arrowBatches.set(lineColor, arrows);
    };

    const dual = edge.direction === "TWO_WAY" && edge.laneMode === "DUAL";
    if (dual) {
      const nx = -dirVec.z / len;
      const nz = dirVec.x / len;
      const offset = new THREE.Vector3(nx, 0, nz).multiplyScalar(0.22);
      addLine(start.clone().add(offset), end.clone().add(offset));
      addLine(end.clone().sub(offset), start.clone().sub(offset));
      const mid = start.clone().add(end).multiplyScalar(0.5);
      addArrow(mid.clone().add(offset), dirVec);
      addArrow(mid.clone().sub(offset), dirVec.clone().negate());
    } else {
      addLine(start, end);
      const mid = start.clone().add(end).multiplyScalar(0.5);
      if (edge.direction === "ONE_WAY") {
        if (!dense) addArrow(mid, dirVec);
      } else if (!dense) {
        // 单车道双向：一条线，两个方向各放一个锥，位置错开避免重叠
        addArrow(start.clone().lerp(end, 1 / 3), dirVec);
        addArrow(start.clone().lerp(end, 2 / 3), dirVec.clone().negate());
      }
    }
  }

  for (const [color, { vertices, opacity }] of lineBatches) {
    if (!vertices.length) continue;
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(vertices, 3));
    netGroup.add(new THREE.LineSegments(geometry,
      new THREE.LineBasicMaterial({ color, transparent: true, opacity })));
  }
  for (const [color, arrows] of arrowBatches) {
    if (!arrows.length) continue;
    const mesh = new THREE.InstancedMesh(new THREE.ConeGeometry(0.32, 0.9, 10),
      new THREE.MeshStandardMaterial({ color, metalness: 0.1, roughness: 0.8 }), arrows.length);
    arrows.forEach((matrix, index) => mesh.setMatrixAt(index, matrix));
    netGroup.add(mesh);
  }

  for (const node of net.nodes) {
    const display = displayNodes.get(node.id) ?? node;
    const isWaitingNode = /^WAIT/.test(node.id);
    if (dense && node.type === "JUNCTION" && !isWaitingNode) continue;
    const color = isWaitingNode ? 0xe7aa42 : NODE_COLORS[node.type] ?? 0x909399;
    const radius =
      node.type === "CHARGER" ? 0.3 : node.type === "WORKSTATION" ? 0.25 : isWaitingNode ? 0.23 : 0.16;
    const height = dense ? 0.24 : 0.5;
    const mesh = new THREE.Mesh(
      new THREE.CylinderGeometry(
        dense ? radius : 0.55,
        dense ? radius : 0.55,
        height,
        18,
      ),
      new THREE.MeshStandardMaterial({
        color,
        emissive: color,
        emissiveIntensity: 0.25,
        roughness: 0.5,
      }),
    );
    mesh.position.set(display.x, height / 2, display.y);
    mesh.userData.nodeId = node.id;
    mesh.userData.nodeType = node.type;
    nodeVisuals.set(node.id, mesh);
    netGroup.add(mesh);
    if (node.type === "WORKSTATION") {
      const hitArea = new THREE.Mesh(
        new THREE.SphereGeometry(dense ? 1.4 : 0.9, 10, 8),
        new THREE.MeshBasicMaterial({
          transparent: true,
          opacity: 0,
          depthWrite: false,
        }),
      );
      hitArea.position.set(display.x, height / 2, display.y);
      hitArea.userData.nodeId = node.id;
      hitArea.userData.nodeType = node.type;
      nodeMeshes.set(node.id, hitArea);
      netGroup.add(hitArea);
    }

    if (!dense) {
      const sprite = makeLabel(node.id);
      sprite.position.set(display.x, dense ? 0.8 : 1.9, display.y);
      netGroup.add(sprite);
    }
  }

  if (dense) {
    const chargers = net.nodes.filter((node) => node.type === "CHARGER");
    if (chargers.length) {
      const label = makeLabel("双充电位");
      label.position.set(
        chargers.reduce((sum, node) => sum + node.x, 0) / chargers.length,
        1.25,
        chargers.reduce((sum, node) => sum + node.y, 0) / chargers.length,
      );
      label.scale.set(2.6, 1.0, 1);
      netGroup.add(label);
    }
  }
  updateNodeSelection();
}

function makeLabel(text: string, badge = false): THREE.Sprite {
  const canvas = document.createElement("canvas");
  canvas.width = 128;
  canvas.height = 64;
  const ctx = canvas.getContext("2d")!;
  ctx.clearRect(0, 0, 128, 64);
  if (badge) {
    // 车辆编号加圆角底色：等待或充电时车辆会停在节点上，避免和节点标签糊在一起
    ctx.fillStyle = "rgba(31, 59, 115, 0.88)";
    ctx.strokeStyle = "rgba(120, 180, 255, 0.9)";
    ctx.lineWidth = 3;
    const radius = 16;
    ctx.beginPath();
    ctx.roundRect(6, 10, 116, 44, radius);
    ctx.fill();
    ctx.stroke();
  }
  ctx.font = 'bold 34px "Microsoft YaHei", sans-serif';
  ctx.fillStyle = badge ? "#e8f2ff" : "#dbe6f5";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText(text, 64, 32);
  const texture = new THREE.CanvasTexture(canvas);
  texture.minFilter = THREE.LinearFilter;
  const material = new THREE.SpriteMaterial({
    map: texture,
    transparent: true,
    depthTest: false,
  });
  const sprite = new THREE.Sprite(material);
  sprite.scale.set(3.2, 1.6, 1);
  return sprite;
}

function createAgvMesh(agv: AgvView): AgvMesh {
  const group = new THREE.Group();
  const length = agv.lengthMeters ?? 1.5, width = agv.widthMeters ?? 1;
  const body = new THREE.Mesh(
    new THREE.BoxGeometry(length, 0.55, width),
    new THREE.MeshStandardMaterial({
      color: 0x409eff,
      metalness: 0.3,
      roughness: 0.4,
    }),
  );
  body.position.y = 0.45;
  group.add(body);

  const nose = new THREE.Mesh(
    new THREE.ConeGeometry(0.3, 0.7, 12),
    new THREE.MeshStandardMaterial({
      color: 0xffffff,
      emissive: 0xffffff,
      emissiveIntensity: 0.4,
    }),
  );
  nose.rotation.z = -Math.PI / 2;
  nose.position.set(length / 2, 0.45, 0);
  group.add(nose);
  const box = new THREE.Mesh(new THREE.BoxGeometry(length * 0.72, 0.32, width * 0.82),
    new THREE.MeshStandardMaterial({ color: 0xe2b568, roughness: 0.7 }));
  box.position.y = 0.88;
  box.visible = false;
  group.add(box);
  const sprite = makeLabel(agv.id, true);
  sprite.position.set(0, 1.6, 0);
  sprite.scale.set(2.4, 1.2, 1);
  group.add(sprite);

  agvGroup.add(group);
  return {
    group,
    box,
    target: new THREE.Vector3(0, 0.45, 0),
    targetHeading: 0,
    status: "IDLE",
  };
}

function updateAgvs(list: AgvView[] | undefined) {
  if (!scene) return;
  const seen = new Set<string>();

  for (const agv of list ?? []) {
    seen.add(agv.id);
    let mesh = agvMeshes.get(agv.id);
    if (!mesh) {
      mesh = createAgvMesh(agv);
      mesh.group.position.set(agv.x, 0.45, agv.y);
      agvMeshes.set(agv.id, mesh);
    }
    let x = agv.x;
    let y = agv.y;
    const station = agv.station ? displayNodes.get(agv.station) : undefined;
    const rawStation = agv.station ? rawNodes.get(agv.station) : undefined;
    if (
      station &&
      rawStation &&
      ["WAITING", "LOADING", "IDLE", "CHARGING"].includes(agv.status) &&
      Math.hypot(agv.x - rawStation.x, agv.y - rawStation.y) < 1.2
    ) {
      x += station.x - rawStation.x;
      y += station.y - rawStation.y;
    }
    mesh.target.set(x, 0.45, y);
    mesh.targetHeading = -THREE.MathUtils.degToRad(agv.heading || 0);
    mesh.box.visible = agv.loaded === true;

    if (mesh.status !== agv.status) {
      mesh.status = agv.status;
      const color = STATUS_COLORS[agv.status] ?? 0x409eff;
      const body = mesh.group.children[0] as THREE.Mesh;
      const material = body.material as THREE.MeshStandardMaterial;
      material.color.setHex(color);
      material.emissive = new THREE.Color(color);
      material.emissiveIntensity = agv.status === "WAITING" ? 0.9 : 0.25;
    }
  }

  for (const [id, mesh] of agvMeshes) {
    if (!seen.has(id)) {
      agvGroup.remove(mesh.group);
      disposeGroup(mesh.group);
      agvMeshes.delete(id);
    }
  }
  requestRender();
}

/** 画一条规划路径（用于单车路径演示）。 */
function drawPath(points: MapPoint[]) {
  drawPaths([{ points, color: "#ffd666" }]);
}
function drawPaths(paths: { points: MapPoint[]; color: string }[]) {
  if (!scene) return;
  disposeGroup(pathGroup);
  for (const { points, color } of paths) {
    if (points.length < 2) continue;
    const geometry = new THREE.BufferGeometry().setFromPoints(points.map(p => new THREE.Vector3(p.x, 0.35, p.y)));
    pathGroup.add(new THREE.Line(geometry, new THREE.LineBasicMaterial({ color })));
  }
  requestRender();
}
function clearPath() {
  if (!scene) return;
  disposeGroup(pathGroup);
  requestRender();
}

function fitView() {
  if (!props.roadnet || props.roadnet.nodes.length === 0 || !camera) return;
  const points = renderedPoints.length ? renderedPoints : props.roadnet.nodes;
  const xs = points.map((point) => point.x);
  const ys = points.map((point) => point.y);
  const minX = Math.min(...xs), maxX = Math.max(...xs);
  const minY = Math.min(...ys), maxY = Math.max(...ys);
  const cx = (minX + maxX) / 2;
  const cy = (minY + maxY) / 2;
  const width = Math.max(maxX - minX, 10);
  const depth = Math.max(maxY - minY, 10);
  const span = Math.max(width, depth);
  const gridSize = Math.max(40, Math.ceil(span * 1.45));
  const gridDivisions = Math.max(20, Math.ceil(gridSize / 2));
  if (grid) {
    scene.remove(grid);
    grid.geometry.dispose();
    const materials = Array.isArray(grid.material) ? grid.material : [grid.material];
    materials.forEach((material) => material.dispose());
  }
  grid = new THREE.GridHelper(gridSize, gridDivisions, 0x1e3350, 0x152740);
  grid.position.set(cx, -0.02, cy);
  scene.add(grid);
  const aspect = Math.max(camera.aspect || 1, 0.5);
  const viewHeight = Math.max(depth, width / aspect);
  const distance = (viewHeight / (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)))) * 1.2;
  const viewDirection = new THREE.Vector3(0.02, 1.45, 0.58).normalize();

  camera.near = Math.max(0.1, span / 10000);
  camera.far = Math.max(1000, distance + span * 4);
  camera.updateProjectionMatrix();
  if (scene.fog instanceof THREE.Fog) {
    scene.fog.near = distance * 1.5;
    scene.fog.far = distance * 4.5;
  }
  controls.target.set(cx, 0, cy);
  camera.position.copy(controls.target).addScaledVector(viewDirection, distance);
  defaultDistance = distance;
  controls.minDistance = Math.max(0.8, span * 0.035);
  controls.maxDistance = span * 4;
  controls.update();
  markOverviewDirty();
  if (showLabels.value) refreshLabels();
}

function loadMap(net: RoadNetPayload | null) {
  clearLocation();
  rebuildRoadNet(net);
  staticItems.value = mapNavigationItems(net, displayNodes);
  fitView();
  if (!net?.nodes.length) viewport.value = null;
}

watch(
  () => props.roadnet,
  (net) => {
    loadMap(net);
  },
  { deep: false },
);

watch(
  () => props.agvs,
  (list) => updateAgvs(list),
  { deep: true },
);
watch(() => props.observed, requestRender);
watch(showLabels, refreshLabels);
watch(marked, updateNodeSelection);

watch(
  () => props.selectedNodeIds,
  () => updateNodeSelection(),
  { deep: true },
);

onMounted(() => {
  init();
  loadMap(props.roadnet);
  updateAgvs(props.agvs);
});

onBeforeUnmount(() => {
  cancelAnimationFrame(raf);
  resizeObserver?.disconnect();
  controls?.dispose();
  renderer?.domElement.removeEventListener("pointerdown", onPointerDown);
  renderer?.domElement.removeEventListener("pointermove", onPointerMove);
  renderer?.domElement.removeEventListener("pointerup", onPointerUp);
  renderer?.domElement.removeEventListener("pointercancel", cancelPointer);
  renderer?.domElement.removeEventListener("lostpointercapture", cancelPointer);
  if (grid) {
    grid.geometry.dispose();
    const materials = Array.isArray(grid.material) ? grid.material : [grid.material];
    materials.forEach((material) => material.dispose());
    grid = null;
  }
  disposeGroup(netGroup);
  disposeGroup(agvGroup);
  disposeGroup(pathGroup);
  disposeGroup(locationGroup);
  disposeGroup(labelsGroup);
  renderer?.dispose();
  if (renderer?.domElement.parentElement) {
    renderer.domElement.parentElement.removeChild(renderer.domElement);
  }
});

defineExpose({ drawPath, drawPaths, clearPath, fitView, locate });
</script>

<template>
  <div ref="surface" class="scene-surface">
    <MapNavigationToolbar v-if="roadnet?.nodes.length" :items="items" :zoom="zoom" :labels="showLabels" :marked="marked || !!located"
      @locate="locate" @zoom="zoomBy" @pan="pan" @fit="fitView" @fullscreen="toggleMapFullscreen(surface)"
      @labels="showLabels = $event" @select-all="marked = true" @clear="clearLocation" />
    <div class="scene-frame">
      <div ref="container" class="scene" :class="{ 'is-dragging': dragging }"></div>
      <MapOverview :map="roadnet" :view="viewport" :target="located" @center="centerAt" @settled="showLabels && refreshLabels()" />
    </div>
  </div>
</template>

<style scoped>
.scene {
  width: 100%;
  height: 100%;
  min-height: 420px;
  border-radius: 8px;
  overflow: hidden;
  background: #0b1526;
  cursor: var(--map-cursor-grab);
}

.scene-surface { display: flex; flex-direction: column; height: 100%; min-width: 0; }
.scene-frame { position: relative; flex: 1; min-height: 420px; }
.scene-surface:fullscreen { background: #fff; padding: 12px; }
.scene-surface:fullscreen .scene-frame { min-height: 0; }
.scene-surface:fullscreen .scene { min-height: 0; }

.scene.is-dragging { cursor: var(--map-cursor-grabbing); }
</style>
