<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch } from "vue";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import {
  simulationVehicleVisualColor,
  type simulationGeometry,
} from "../simulation";
const props = defineProps<{
  geometry: ReturnType<typeof simulationGeometry>;
  fresh: boolean;
  selected: string;
  focusVehicles: boolean;
  edgeColors: Record<string, string>;
}>();
const emit = defineEmits<{ select: [id: string] }>();
const host = ref<HTMLDivElement | null>(null),
  failure = ref("");
let renderer: THREE.WebGLRenderer | undefined,
  controls: OrbitControls | undefined,
  observer: ResizeObserver | undefined,
  themeObserver: MutationObserver | undefined,
  frame = 0;
const scene = new THREE.Scene(),
  camera = new THREE.PerspectiveCamera(42, 1, 0.01, 100000);
const roads = new THREE.Group(),
  vehicles = new THREE.Group(),
  raycaster = new THREE.Raycaster();
function point(p: { x: number; y: number }, height = 0) {
  return new THREE.Vector3(
    (p.x - 400) / props.geometry.scale,
    height,
    (p.y - 215) / props.geometry.scale,
  );
}
function coordinate(p: { x: number; y: number }) {
  const origin = props.geometry.position({ x: 0, y: 0 });
  return `X ${((p.x - origin.x) / props.geometry.scale).toFixed(3)} m · Y ${((origin.y - p.y) / props.geometry.scale).toFixed(3)} m`;
}
function clear(group: THREE.Group) {
  group.traverse((object) => {
    if (object instanceof THREE.Mesh || object instanceof THREE.Line) {
      object.geometry.dispose();
      const materials = Array.isArray(object.material)
        ? object.material
        : [object.material];
      materials.forEach((material) => material.dispose());
    }
  });
  group.clear();
}
function draw() {
  clear(roads);
  clear(vehicles);
  for (const edge of props.geometry.edges) {
    roads.add(
      new THREE.Line(
        new THREE.BufferGeometry().setFromPoints(
          edge.samples.map((p) => point(p)),
        ),
        new THREE.LineBasicMaterial({
          color: props.edgeColors[edge.key] ?? "#3e8da8",
        }),
      ),
    );
  }
  for (const node of props.geometry.nodes) {
    const mesh = new THREE.Mesh(
      new THREE.CylinderGeometry(0.12, 0.12, 0.04, 12),
      new THREE.MeshBasicMaterial({ color: "#48d7cf" }),
    );
    mesh.position.copy(point(node, 0.02));
    roads.add(mesh);
  }
  for (const car of props.geometry.cars) {
    const group = new THREE.Group();
    group.userData.vehicleId = car.id;
    group.position.copy(point(car, 0.18));
    group.rotation.y = car.angle ?? 0;
    group.add(
      new THREE.Mesh(
        new THREE.BoxGeometry(0.94, 0.32, 0.64),
        new THREE.MeshStandardMaterial({
          color: simulationVehicleVisualColor(car, props.fresh),
        }),
      ),
    );
    const heading = new THREE.Mesh(
      new THREE.ConeGeometry(0.1, 0.25, 3),
      new THREE.MeshBasicMaterial({ color: "#ffffff" }),
    );
    heading.rotation.z = -Math.PI / 2;
    heading.position.set(0.5, 0.2, 0);
    group.add(heading);
    if (car.loaded) {
      const load = new THREE.Mesh(
        new THREE.BoxGeometry(0.6, 0.28, 0.48),
        new THREE.MeshStandardMaterial({ color: "#e8bf64" }),
      );
      load.position.y = 0.3;
      group.add(load);
    }
    if (car.id === props.selected) {
      const ring = new THREE.Mesh(
        new THREE.RingGeometry(0.7, 0.77, 32),
        new THREE.MeshBasicMaterial({
          color: "#ffffff",
          side: THREE.DoubleSide,
        }),
      );
      ring.rotation.x = -Math.PI / 2;
      ring.position.y = -0.13;
      group.add(ring);
    }
    vehicles.add(group);
  }
}
function fit() {
  if (!controls) return;
  const points =
    props.focusVehicles && props.geometry.cars.length
      ? props.geometry.cars
      : props.geometry.nodes;
  const box = new THREE.Box3().setFromPoints(points.map((p) => point(p)));
  if (box.isEmpty()) return;
  const center = box.getCenter(new THREE.Vector3()),
    size = box.getSize(new THREE.Vector3());
  const span = Math.max(size.x / Math.min(camera.aspect, 1), size.z, 4);
  camera.position
    .copy(center)
    .add(new THREE.Vector3(span * 0.4, span * 1.5, span));
  camera.far = Math.max(1000, span * 20);
  camera.updateProjectionMatrix();
  controls.target.copy(center);
  controls.update();
}
function select(event: PointerEvent) {
  if (!renderer) return;
  const rect = renderer.domElement.getBoundingClientRect();
  raycaster.setFromCamera(
    new THREE.Vector2(
      ((event.clientX - rect.left) / rect.width) * 2 - 1,
      1 - ((event.clientY - rect.top) / rect.height) * 2,
    ),
    camera,
  );
  let object: THREE.Object3D | null =
    raycaster.intersectObjects(vehicles.children, true)[0]?.object ?? null;
  while (object && !object.userData.vehicleId) object = object.parent;
  if (object) emit("select", object.userData.vehicleId);
}
function contextLost(event: Event) {
  event.preventDefault();
  cancelAnimationFrame(frame);
  failure.value = "三维绘图连接已中断，请切换二维视图查看。";
}
function applyTheme() {
  const light = document.documentElement.dataset.theme === "light";
  scene.background = new THREE.Color(light ? "#edf3f8" : "#071321");
  if (renderer) renderer.domElement.style.background = light ? "#edf3f8" : "#071321";
  draw();
}
watch(
  () => [props.geometry, props.edgeColors, props.selected, props.fresh],
  draw,
);
watch(() => props.focusVehicles, fit);
watch(() => JSON.stringify(props.geometry.nodes), fit);
onMounted(() => {
  try {
    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    applyTheme();
    scene.add(
      roads,
      vehicles,
      new THREE.HemisphereLight(0xc6e8ff, 0x26374b, 3),
    );
    host.value!.appendChild(renderer.domElement);
    controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.maxPolarAngle = Math.PI / 2 - 0.03;
    renderer.domElement.addEventListener("pointerup", select);
    renderer.domElement.addEventListener("webglcontextlost", contextLost);
    let initial = true;
    observer = new ResizeObserver(() => {
      if (!host.value || !renderer) return;
      const { width, height } = host.value.getBoundingClientRect();
      if (!width || !height) return;
      renderer.setSize(width, height);
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      if (initial) {
        initial = false;
        fit();
      }
    });
    observer.observe(host.value!);
    themeObserver = new MutationObserver(applyTheme);
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "class"] });
    draw();
    const animate = () => {
      frame = requestAnimationFrame(animate);
      controls?.update();
      renderer?.render(scene, camera);
    };
    animate();
  } catch {
    failure.value = "三维视图初始化失败，请切换二维视图查看。";
  }
});
onBeforeUnmount(() => {
  cancelAnimationFrame(frame);
  observer?.disconnect();
  themeObserver?.disconnect();
  controls?.dispose();
  clear(roads);
  clear(vehicles);
  renderer?.domElement.removeEventListener("pointerup", select);
  renderer?.domElement.removeEventListener("webglcontextlost", contextLost);
  renderer?.dispose();
  renderer?.domElement.remove();
});
</script>
<template>
  <div class="simulation-three">
    <div
      ref="host"
      class="three-canvas"
      role="img"
      aria-label="三维仿真路网及车辆位置"
    />
    <p v-if="failure" class="three-error" role="alert">{{ failure }}</p>
    <div class="three-tools">
      <button @click="fit">适应画面</button
      ><span>拖动旋转 · 滚轮缩放 · 右键平移</span>
    </div>
    <div class="three-vehicles" aria-label="三维车辆选择">
      <button
        v-for="car in geometry.cars"
        :key="car.id"
        :aria-pressed="selected === car.id"
        :title="coordinate(car)"
        @click="emit('select', car.id)"
      >
        {{ car.id }} · {{ car.station || "站间"
        }}<small v-if="selected === car.id"
          >{{ coordinate(car) }}{{ fresh ? "" : " · 状态过期" }}</small
        >
      </button>
    </div>
  </div>
</template>
<style scoped>
.simulation-three {
  position: relative;
  height: min(760px, calc(100vh - 285px));
  min-height: 360px;
  border: 1px solid #253e58;
  border-radius: 6px;
  overflow: hidden;
  background: #071321;
}
.three-canvas {
  width: 100%;
  height: 100%;
}
.three-tools {
  position: absolute;
  top: 44px;
  left: 12px;
  display: flex;
  align-items: center;
  gap: 10px;
  color: #8ca8bd;
  font-size: 10px;
}
.three-vehicles {
  position: absolute;
  bottom: 12px;
  left: 12px;
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  max-width: calc(100% - 24px);
}
.three-vehicles button[aria-pressed="true"] {
  border-color: #41d9bf;
  color: #41d9bf;
}
.three-vehicles small {
  display: block;
  font-size: 10px;
}
.three-error {
  position: absolute;
  top: 90px;
  left: 16px;
  color: #ff9b79;
}
@media (max-width: 760px) {
  .simulation-three {
    height: 360px;
  }
  .three-tools span {
    display: none;
  }
}
</style>
