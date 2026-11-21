<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import SimulationWorkshopMap from "@/components/SimulationWorkshopMap.vue";
import { dispatchSceneMap, type DispatchCar, type Point, type SourceMap } from "@/integration/dispatch-adapter";
import { dispatchRead } from "@/integration/dispatch-api";

interface ReplayFrame {
  time: number;
  vehicles?: (Point & { vehicleId: string; angle?: number; site?: string; waitReason?: string; loaded?: boolean; moving?: boolean; offline?: boolean; taskId?: string | null })[];
  tasks?: { taskId: string; stationId?: string; vehicleId?: string; stage?: string; step?: string; waitReason?: string }[];
}
interface DispatchReplay {
  id: string;
  timeline: ReplayFrame[];
  map?: SourceMap | null;
  mapMessage?: string | null;
  sampled?: boolean;
  totalSamples?: number;
}

const props = defineProps<{ reportId: string }>();
const replay = shallowRef<DispatchReplay | null>(null);
const frameIndex = ref(0);
const loading = ref(false);
const playing = ref(false);
const videoProgress = ref<number | null>(null);
const error = ref("");
const notice = ref("");
const lifetime = new AbortController();
const timeline = computed(() => replay.value?.timeline ?? []);
const frame = computed(() => timeline.value[frameIndex.value]);
const map = computed(() => replay.value?.map ? dispatchSceneMap(replay.value.map) : null);
const cars = computed<Record<string, DispatchCar>>(() => Object.fromEntries(
  (frame.value?.vehicles ?? []).filter(validPosition).map(car => [car.vehicleId, {
    x: car.x, y: car.y, angle: car.angle, station: car.site, waitReason: car.waitReason,
    loaded: car.loaded, moving: car.moving, offline: car.offline, current_task: car.taskId,
  }]),
));
const selectedVehicle = ref("");
const location = shallowRef<{ vehicleId: string } | null>(null);
function locateVehicle(vehicleId: string) {
  selectedVehicle.value = vehicleId;
  location.value = { vehicleId };
}
const stageNames: Record<string, string> = {
  WaitingForVehicle: "待派车", SubmittingToAgv: "下发命令", GoingToPickup: "前往取箱",
  PickingUp: "取箱", GoingToDumper: "运送满箱", WaitingForDumper: "等待倒料",
  Dumping: "倒料／返箱", WaitingForPlcConfirmation: "PLC 核验",
  Completed: "已完成", Faulted: "故障", Cancelled: "已取消",
};

function validPosition(point: Point) {
  return Number.isFinite(point.x) && Number.isFinite(point.y);
}
function displayTime(time: number) {
  return new Date(time * 1000).toLocaleString();
}
async function load() {
  if (loading.value) return;
  loading.value = true;
  error.value = "";
  try {
    const result = await dispatchRead<DispatchReplay>(
      `/api/simulation/replay?id=${encodeURIComponent(props.reportId)}`, lifetime.signal,
    );
    if (!lifetime.signal.aborted) {
      replay.value = result;
      frameIndex.value = 0;
    }
  } catch (reason) {
    if (!lifetime.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    loading.value = false;
  }
}
function togglePlayback() {
  if (!playing.value && frameIndex.value >= timeline.value.length - 1) frameIndex.value = 0;
  playing.value = !playing.value;
}
watch(playing, (active, _old, onCleanup) => {
  if (!active) return;
  const timer = setInterval(() => {
    if (frameIndex.value < timeline.value.length - 1) frameIndex.value++;
    else playing.value = false;
  }, 125);
  onCleanup(() => clearInterval(timer));
});
onBeforeUnmount(() => lifetime.abort());

function videoRenderer() {
  const canvas = document.createElement("canvas");
  canvas.width = 1280;
  canvas.height = 720;
  const context = canvas.getContext("2d");
  if (!context) throw new Error("浏览器无法创建视频画布");
  const geometry = map.value;
  const positions = geometry?.nodes.length ? geometry.nodes : timeline.value.flatMap(
    sample => (sample.vehicles ?? []).filter(validPosition).map(car => ({ x: car.x, y: -car.y })),
  );
  const xs = positions.map(point => point.x);
  const ys = positions.map(point => point.y);
  const minX = positions.length ? Math.min(...xs) : 0;
  const maxX = positions.length ? Math.max(...xs) : 0;
  const minY = positions.length ? Math.min(...ys) : 0;
  const maxY = positions.length ? Math.max(...ys) : 0;
  const scale = Math.min(1180 / Math.max(1, maxX - minX), 455 / Math.max(1, maxY - minY));
  const x = (value: number) => 640 + (value - (minX + maxX) / 2) * scale;
  const y = (value: number) => 315 + (value - (minY + maxY) / 2) * scale;
  const background = document.createElement("canvas");
  background.width = canvas.width;
  background.height = canvas.height;
  const base = background.getContext("2d")!;
  base.fillStyle = "#101827";
  base.fillRect(0, 0, canvas.width, canvas.height);
  base.strokeStyle = "#445a76";
  base.lineWidth = 1.25;
  base.beginPath();
  for (const edge of geometry?.edges ?? []) {
    const points = edge.points ?? [];
    if (!points.length) continue;
    base.moveTo(x(points[0]!.x), y(points[0]!.y));
    for (const point of points.slice(1)) base.lineTo(x(point.x), y(point.y));
  }
  base.stroke();
  function draw(sample: ReplayFrame) {
    context!.drawImage(background, 0, 0);
    context!.font = "22px sans-serif";
    context!.fillStyle = "#edf4ff";
    context!.fillText(`${replay.value?.map?.name || "业务仿真"} · ${displayTime(sample.time)}`, 30, 46);
    const palette = ["#53e2d1", "#ffd36a", "#86b8ff", "#fe94ae", "#b5a3ff"];
    const vehicles = sample.vehicles ?? [];
    context!.font = "16px sans-serif";
    vehicles.forEach((car, index) => {
      context!.fillStyle = palette[index % palette.length]!;
      if (validPosition(car)) {
        const cx = x(car.x), cy = y(-car.y);
        context!.beginPath();
        context!.arc(cx, cy, 6, 0, Math.PI * 2);
        context!.fill();
        context!.fillText(car.vehicleId, cx + 8, cy - 8);
      }
      const task = sample.tasks?.find(item => item.vehicleId === car.vehicleId && item.stage !== "Completed");
      const stage = task ? stageNames[task.stage ?? ""] || task.stage : "";
      const row = [car.vehicleId, car.site, task?.stationId, stage, task?.step, task?.waitReason || car.waitReason];
      context!.fillText(row.filter(Boolean).join(" · "), 30, 577 + index * 24, 1210);
    });
  }
  return { canvas, draw };
}

async function exportVideo() {
  if (videoProgress.value !== null || !timeline.value.length) return;
  error.value = "";
  notice.value = "";
  const mimeType = typeof MediaRecorder === "undefined" ? undefined
    : ["video/webm;codecs=vp8", "video/webm"].find(type => MediaRecorder.isTypeSupported(type));
  if (!mimeType || !HTMLCanvasElement.prototype.captureStream) {
    error.value = "当前浏览器不支持 WebM 导出";
    return;
  }
  const source = timeline.value;
  const stride = Math.max(1, Math.ceil(source.length / 599));
  const frames = source.filter((_, index) => index % stride === 0 || index === source.length - 1);
  let stream: MediaStream | undefined;
  let recorder: MediaRecorder | undefined;
  videoProgress.value = 0;
  try {
    const { canvas, draw } = videoRenderer();
    draw(frames[0]!);
    stream = canvas.captureStream(10);
    recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: 2_500_000 });
    const chunks: BlobPart[] = [];
    recorder.ondataavailable = event => { if (event.data.size) chunks.push(event.data); };
    const finished = new Promise<Blob | null>(resolve => {
      recorder!.onstop = () => resolve(new Blob(chunks, { type: mimeType }));
      recorder!.onerror = () => resolve(null);
    });
    recorder.start();
    for (const [index, sample] of frames.entries()) {
      if (lifetime.signal.aborted || recorder.state !== "recording") break;
      draw(sample);
      videoProgress.value = Math.round((index + 1) * 100 / frames.length);
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    if (recorder.state === "recording") recorder.stop();
    if (lifetime.signal.aborted) return;
    const blob = await finished;
    if (lifetime.signal.aborted) return;
    if (!blob?.size) throw new Error("浏览器未生成可用的 WebM 视频");
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `simulation-replay-${props.reportId}.webm`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    notice.value = `回放视频已导出 · ${frames.length} 帧`;
  } catch (reason) {
    if (!lifetime.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    stream?.getTracks().forEach(track => track.stop());
    videoProgress.value = null;
  }
}
</script>

<template>
  <section class="dispatch-replay" aria-label="仿真回放">
    <div class="replay-controls">
      <el-button size="small" :loading="loading" :disabled="playing || videoProgress !== null" @click="load">载入时间轴</el-button>
      <el-button size="small" :disabled="timeline.length < 2" @click="togglePlayback">{{ playing ? "暂停回放" : "播放回放" }}</el-button>
      <el-button size="small" :disabled="!timeline.length" :loading="videoProgress !== null" @click="exportVideo">
        {{ videoProgress === null ? "导出回放视频" : `正在录制 ${videoProgress}%` }}
      </el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="replay && !frame">本轮没有可回放的观察采样</p>
    <template v-if="frame">
      <label class="replay-timeline">
        <span>{{ displayTime(frame.time) }} · {{ frameIndex + 1 }} / {{ timeline.length }}{{ replay?.sampled ? `（原始 ${replay.totalSamples ?? "多条"} 条采样）` : "" }}</span>
        <input v-model.number="frameIndex" type="range" min="0" :max="timeline.length - 1" aria-label="回放时间点" @input="playing = false" />
      </label>
      <SimulationWorkshopMap v-if="replay?.map" :definition="replay.map" :cars="cars" :fresh="true"
        :selected-vehicle="selectedVehicle" :location="location" @select-vehicle="locateVehicle" />
      <p v-else>{{ replay?.mapMessage || "该轮报告未提供地图快照" }}</p>
      <el-table :data="frame.vehicles ?? []" size="small" empty-text="该采样没有车辆反馈">
        <el-table-column label="车辆" width="105"><template #default="{ row }">
          <el-button link type="primary" @click="locateVehicle(row.vehicleId)">{{ row.vehicleId }}</el-button>
        </template></el-table-column>
        <el-table-column prop="site" label="当前点位" min-width="180" />
        <el-table-column label="坐标 (m)" min-width="150"><template #default="{ row }">
          {{ validPosition(row) ? `${row.x.toFixed(2)}, ${row.y.toFixed(2)}` : "—" }}
        </template></el-table-column>
        <el-table-column prop="waitReason" label="等待原因" min-width="190" />
      </el-table>
      <el-table :data="frame.tasks ?? []" size="small" empty-text="该采样没有业务任务">
        <el-table-column prop="stationId" label="工位" width="105" />
        <el-table-column prop="vehicleId" label="车辆" width="105" />
        <el-table-column label="阶段" min-width="120"><template #default="{ row }">{{ stageNames[row.stage] || row.stage }}</template></el-table-column>
        <el-table-column prop="step" label="步骤" min-width="100" />
        <el-table-column prop="waitReason" label="任务说明" min-width="220" />
      </el-table>
    </template>
  </section>
</template>

<style scoped>
.dispatch-replay { margin: 16px 0; display: grid; gap: 10px; min-width: 0; }
.replay-controls { display: flex; flex-wrap: wrap; gap: 8px; }
.replay-controls .el-button { margin-left: 0; }
.replay-timeline { display: grid; gap: 5px; }
.replay-timeline input { width: 100%; }
</style>
