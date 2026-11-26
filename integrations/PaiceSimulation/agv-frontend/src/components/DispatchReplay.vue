<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import SimulationWorkshopMap from "@/components/SimulationWorkshopMap.vue";
import { dispatchSceneMap, type DispatchCar, type Point, type SourceMap } from "@/integration/dispatch-adapter";
import { dispatchRead } from "@/integration/dispatch-api";

import { previewNodePositions } from "@/integration/map-navigation";
import ReplayEvidence from "@main-web/components/ReplayEvidence.vue";
import ReplaySpeed from "@main-web/components/ReplaySpeed.vue";
import { fitReplayText, recordingRate, recordReplayWebm, replayClockFrame, type ReplayCoverage, type ReplayEvent } from "@main-web/replay-evidence";

interface ReplayVehicle extends Point {
  vehicleId: string;
  angle?: number;
  site?: string;
  waitReason?: string;
  loaded?: boolean;
  moving?: boolean;
  offline?: boolean;
  blocked?: boolean;
  blockedByVehicle?: string;
  paused?: boolean;
  taskId?: string | null;
}
interface ReplayTask {
  taskId: string;
  stationId?: string;
  vehicleId?: string;
  stage?: string;
  step?: string;
  waitReason?: string;
}
interface ReplayFrame {
  time: number;
  vehicles?: ReplayVehicle[];
  tasks?: ReplayTask[];
}
interface DispatchReplay {
  id: string;
  timeline: ReplayFrame[];
  map?: SourceMap | null;
  mapMessage?: string | null;
  simulationRate?: number;
  sampled?: boolean;
  totalSamples?: number;
  events?: ReplayEvent[];
  evidence?: ReplayCoverage;
}

const props = defineProps<{ reportId: string; simulationRate?: number }>();
const replay = shallowRef<DispatchReplay | null>(null);
const frameIndex = ref(0);
const loading = ref(false);
const playing = ref(false);
const playbackRate = ref(1);
const exportRate = ref(1);
const playbackRateBase = computed(() => recordingRate(replay.value?.simulationRate ?? props.simulationRate));
const videoProgress = ref<number | null>(null);
const error = ref("");
const notice = ref("");
const lifetime = new AbortController();
let exportAbort: AbortController | undefined;
function cancelExport() { exportAbort?.abort(); }
const timeline = computed(() => replay.value?.timeline ?? []);
const events = computed(() => replay.value?.events ?? []);
const frame = computed(() => timeline.value[frameIndex.value]);
const positionEvent = shallowRef<ReplayEvent>();
watch(frameIndex, () => { positionEvent.value = undefined; }, { flush: 'sync' });
const map = computed(() => replay.value?.map ? dispatchSceneMap(replay.value.map) : null);
const cars = computed<Record<string, DispatchCar>>(() => {
  const vehicles: Record<string, DispatchCar> = Object.fromEntries(
  (frame.value?.vehicles ?? []).filter(validPosition).map(car => [car.vehicleId, {
    x: car.x, y: car.y, angle: car.angle, station: car.site, waitReason: car.waitReason,
    loaded: car.loaded, moving: car.moving, offline: car.offline, blocked: car.blocked, paused: car.paused,
    blockedByVehicle: car.blockedByVehicle, current_task: car.taskId,
  }]));
  const event = positionEvent.value;
  if (event?.vehicleId && event.position) {
    const { x, y, angle, site } = event.position;
    vehicles[event.vehicleId] = { ...vehicles[event.vehicleId], x, y, angle, station: site };
  }
  return vehicles;
});
const selectedVehicle = ref("");
const location = shallowRef<{ vehicleId: string } | null>(null);
function locateVehicle(vehicleId: string) {
  selectedVehicle.value = vehicleId;
  location.value = { vehicleId };
}
const stageNames: Record<string, string> = {
  WaitingForVehicle: "待派车", SubmittingToAgv: "下发命令", GoingToPickup: "前往取箱",
  PickingUp: "取箱", GoingToDumper: "运送满箱", WaitingForDumper: "等待倒料",
  Dumping: "倒料／返箱", ReturningEmptyBox: "返空箱", WaitingForPlcConfirmation: "PLC 核验",
  Completed: "已完成", Faulted: "故障", Cancelled: "已取消",
};
const terminalStages = new Set(["Completed", "Faulted", "Cancelled"]);
const vehicleCards = computed(() => (frame.value?.vehicles ?? []).map(car => {
  const task = (frame.value?.tasks ?? []).find(item => item.vehicleId === car.vehicleId && !terminalStages.has(item.stage ?? ""))
    ?? (frame.value?.tasks ?? []).find(item => item.vehicleId === car.vehicleId);
  return { car, task, state: vehicleState(car, task) };
}).sort((left, right) => {
  if (left.car.vehicleId === selectedVehicle.value) return -1;
  if (right.car.vehicleId === selectedVehicle.value) return 1;
  return left.car.vehicleId.localeCompare(right.car.vehicleId, "zh-CN");
}));
const currentTasks = computed(() => [...(frame.value?.tasks ?? [])].sort((left, right) => {
  const leftTerminal = terminalStages.has(left.stage ?? "");
  const rightTerminal = terminalStages.has(right.stage ?? "");
  if (leftTerminal !== rightTerminal) return leftTerminal ? 1 : -1;
  if (left.vehicleId === selectedVehicle.value) return -1;
  if (right.vehicleId === selectedVehicle.value) return 1;
  return (left.stationId ?? "").localeCompare(right.stationId ?? "", "zh-CN");
}));
const activeTaskCount = computed(() => currentTasks.value.filter(task => !terminalStages.has(task.stage ?? "")).length);
const onlineVehicleCount = computed(() => vehicleCards.value.filter(item => !item.car.offline).length);
const waitingVehicleCount = computed(() => vehicleCards.value.filter(item => item.state.tone === "warning" || item.state.tone === "danger").length);
const selectedCard = computed(() => vehicleCards.value.find(item => item.car.vehicleId === selectedVehicle.value));
const reportShortId = computed(() => props.reportId.slice(0, 8));
const mapTitle = computed(() => replay.value?.map?.name || "业务仿真地图");
const recentVehicleEvents = computed(() => {
  const targetTime = frame.value?.time;
  const vehicle = selectedVehicle.value;
  if (targetTime === undefined || !vehicle) return [];
  return events.value
    .filter(event => event.time <= targetTime && (event.vehicleId === vehicle || event.holderVehicleIds?.includes(vehicle)))
    .slice(-4)
    .reverse();
});

function validPosition(point: Point) {
  return Number.isFinite(point.x) && Number.isFinite(point.y);
}
function displayTime(time: number) {
  return new Date(time * 1000).toLocaleString();
}
function displayClock(time: number) {
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false,
  }).format(new Date(time * 1000));
}
function displayDate(time: number) {
  return new Intl.DateTimeFormat("zh-CN", {
    year: "numeric", month: "2-digit", day: "2-digit",
  }).format(new Date(time * 1000));
}
function stageLabel(stage?: string) {
  return stage ? stageNames[stage] || stage : "未进入业务步骤";
}
function shortTaskId(taskId?: string) {
  if (!taskId) return "未返回任务号";
  return taskId.length > 18 ? "…" + taskId.slice(-16) : taskId;
}
function taskSummary(task?: ReplayTask) {
  const sources = [task?.waitReason, task?.step].filter((value): value is string => Boolean(value?.trim()));
  if (!sources.length) return "当前步骤没有等待说明";
  const patterns = [
    /等待可用车辆[^。；]*/,
    /等待倒料机[^。；]*/,
    /等待位置握手[^。；]*/,
    /等待路权[^。；]*/,
    /等待[^。；]{2,80}/,
  ];
  for (const source of sources) {
    for (const pattern of patterns) {
      const match = source.match(pattern);
      if (match) return match[0];
    }
  }
  for (const source of sources) {
    const step = source.match(/第\s*\d+\s*\/\s*\d+\s*步[，,：:][^；。]*/);
    if (step) return step[0];
  }
  return sources[0]!.trim().slice(0, 90);
}
function vehicleState(car: ReplayVehicle, task?: ReplayTask) {
  const waitReason = task?.waitReason || car.waitReason;
  if (car.offline) return { label: "离线", tone: "muted" };
  if (car.paused) return { label: "暂停", tone: "danger" };
  if (car.blocked || waitReason) return { label: "等待", tone: "warning" };
  if (car.moving) return { label: "行驶中", tone: "moving" };
  if (task && !terminalStages.has(task.stage ?? "")) return { label: "执行任务", tone: "moving" };
  if (car.loaded) return { label: "载箱待命", tone: "waiting" };
  return { label: "空载待命", tone: "idle" };
}
function vehicleSummary(task?: ReplayTask, waitReason?: string) {
  const summary = taskSummary(task);
  return summary === "当前步骤没有等待说明" ? waitReason || summary : summary;
}
function selectVehicleCard(vehicleId: string) {
  locateVehicle(vehicleId);
}
function selectTask(task: ReplayTask) {
  if (task.vehicleId) locateVehicle(task.vehicleId);
  playing.value = false;
}
function focusInitialVehicle(sample?: ReplayFrame) {
  if (!sample) {
    selectedVehicle.value = "";
    location.value = null;
    return;
  }
  const activeTask = (sample.tasks ?? []).find(task => task.vehicleId && !terminalStages.has(task.stage ?? ""));
  const vehicle = sample.vehicles?.find(car => car.vehicleId === activeTask?.vehicleId)
    ?? sample.vehicles?.[0];
  if (vehicle) locateVehicle(vehicle.vehicleId);
  else {
    selectedVehicle.value = "";
    location.value = null;
  }
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
      let initialIndex = 0;
      let initialAssigned = -1;
      let initialTasks = -1;
      result.timeline.forEach((sample, index) => {
        const tasks = sample.tasks ?? [];
        const assigned = new Set(tasks
          .filter(task => task.vehicleId && !terminalStages.has(task.stage ?? ""))
          .map(task => task.vehicleId)).size;
        if (assigned > initialAssigned || (assigned === initialAssigned && tasks.length > initialTasks)) {
          initialIndex = index;
          initialAssigned = assigned;
          initialTasks = tasks.length;
        }
      });
      frameIndex.value = initialIndex;
      positionEvent.value = undefined;
      focusInitialVehicle(result.timeline[frameIndex.value]);
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
function seekEvent(index: number, event: ReplayEvent) {
  playing.value = false;
  frameIndex.value = index;
  positionEvent.value = event;
  if (event.vehicleId) locateVehicle(event.vehicleId);
}
watch([playing, playbackRate], ([active, speed], _old, onCleanup) => {
  const frames = timeline.value;
  const first = frames[frameIndex.value];
  if (!active || !first) return;
  positionEvent.value = undefined;
  const started = performance.now();
  let animation = 0;
  const tick = (now: number) => {
    frameIndex.value = replayClockFrame(frames, first.time, now - started, playbackRateBase.value, speed);
    if (frameIndex.value < frames.length - 1) animation = requestAnimationFrame(tick);
    else playing.value = false;
  };
  animation = requestAnimationFrame(tick);
  onCleanup(() => cancelAnimationFrame(animation));
});

onBeforeUnmount(() => { lifetime.abort(); cancelExport(); });

function videoRenderer() {
  const canvas = document.createElement("canvas");
  canvas.width = 1280;
  canvas.height = 814;
  const context = canvas.getContext("2d");
  if (!context) throw new Error("浏览器无法创建视频画布");
  const geometry = map.value;
  const displayNodes = geometry ? previewNodePositions(geometry.nodes, geometry.edges) : undefined;
  const positions = displayNodes?.size ? [...displayNodes.values()] : timeline.value.flatMap(
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
  base.lineWidth = 1.15;
  for (const edge of geometry?.edges ?? []) {
    const from = displayNodes?.get(edge.from), to = displayNodes?.get(edge.to);
    if (!from || !to) continue;
    const stationBranch = from.type === 'WORKSTATION' || to.type === 'WORKSTATION';
    base.strokeStyle = stationBranch ? '#377f82' : '#445a76';
    base.beginPath();
    base.moveTo(x(from.x), y(from.y));
    for (const point of edge.points?.slice(1, -1) ?? []) base.lineTo(x(point.x), y(point.y));
    base.lineTo(x(to.x), y(to.y));
    base.stroke();
  }
  base.fillStyle = '#6aa4cb';
  for (const station of displayNodes?.values() ?? []) {
    if (station.type !== 'WORKSTATION') continue;
    base.beginPath();
    base.arc(x(station.x), y(station.y), 1.8, 0, Math.PI * 2);
    base.fill();
  }
  function draw(sample: ReplayFrame) {
    context!.drawImage(background, 0, 0);
    context!.font = "22px sans-serif";
    context!.fillStyle = "#edf4ff";
    context!.fillText(`${replay.value?.map?.name || "业务仿真"} · ${displayTime(sample.time)}`, 30, 46);
    const activeTasks = (sample.tasks ?? []).filter(task => !terminalStages.has(task.stage ?? ""));
    const assignedVehicles = new Set(activeTasks.flatMap(task => task.vehicleId ? [task.vehicleId] : []));
    context!.fillStyle = "#79cfd8";
    context!.font = "700 13px sans-serif";
    context!.fillText(`${sample.vehicles?.length ?? 0} 台车辆 · ${assignedVehicles.size} 台有任务 · ${activeTasks.length} 条活动任务`, 30, 76);
    const palette = ["#53e2d1", "#ffd36a", "#86b8ff", "#fe94ae", "#b5a3ff"];
    const vehicles = sample.vehicles ?? [];
    context!.fillStyle = "rgb(8 22 35 / 94%)";
    context!.fillRect(0, 526, canvas.width, 194);
    context!.fillStyle = "#75cbd4";
    context!.font = "800 11px sans-serif";
    context!.fillText("车辆 / 当前点位", 30, 553);
    context!.fillText("工位 / 业务阶段", 315, 553);
    context!.fillText("当前步骤 / 等待说明", 615, 553);
    vehicles.forEach((car, index) => {
      const color = palette[index % palette.length]!;
      context!.fillStyle = color;
      if (validPosition(car)) {
        const cx = x(car.x), cy = y(-car.y);
        context!.beginPath();
        context!.arc(cx, cy, 6, 0, Math.PI * 2);
        context!.fill();
        context!.fillText(car.vehicleId, cx + 8, cy - 8);
      }
      const task = sample.tasks?.find(item => item.vehicleId === car.vehicleId && item.stage !== "Completed");
      const stage = task ? stageNames[task.stage ?? ""] || task.stage : "";
      const state = vehicleState(car, task);
      const rowY = 584 + index * 26;
      context!.strokeStyle = "rgb(255 255 255 / 7%)";
      context!.beginPath();
      context!.moveTo(24, rowY + 9);
      context!.lineTo(canvas.width - 24, rowY + 9);
      context!.stroke();
      context!.fillStyle = color;
      context!.beginPath();
      context!.arc(31, rowY - 4, 4, 0, Math.PI * 2);
      context!.fill();
      context!.font = "700 14px sans-serif";
      context!.fillText(fitReplayText(context!, [car.vehicleId, car.site || "位置未返回", state.label].filter(Boolean).join(" · "), 270), 43, rowY);
      context!.fillStyle = "#d7e7ef";
      context!.font = "700 13px sans-serif";
      context!.fillText(fitReplayText(context!, [task?.stationId || "无工位", stage || "无业务阶段"].join(" · "), 286), 315, rowY);
      context!.fillStyle = "#a9c3d1";
      context!.font = "12px sans-serif";
      const columns = [
        { text: vehicleSummary(task, car.waitReason), x: 615, width: 620 },
      ];
      for (const column of columns) {
        context!.fillText(fitReplayText(context!, column.text, column.width), column.x, rowY);
      }
    });
  }
  return { canvas, draw };
}

async function exportVideo() {
  if (videoProgress.value !== null || !timeline.value.length) return;
  error.value = "";
  notice.value = "";
  videoProgress.value = 0;
  const speed = exportRate.value;
  const cancellation = new AbortController();
  exportAbort = cancellation;
  try {
    const { canvas, draw } = videoRenderer();
    const blob = await recordReplayWebm({ canvas, draw, frames: timeline.value, events: events.value,
      base: playbackRateBase.value, speed, signal: cancellation.signal,
      progress: percent => { videoProgress.value = percent; } });
    if (cancellation.signal.aborted) return;
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `simulation-replay-${props.reportId}.webm`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    notice.value = `回放视频已导出 · ${speed}×`;
  } catch (reason) {
    if (cancellation.signal.aborted) notice.value = "已取消视频导出";
    else error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    exportAbort = undefined;
    videoProgress.value = null;
  }
}
</script>

<template>
  <section class="dispatch-replay replay-console" aria-label="仿真回放">
    <header class="replay-masthead">
      <div class="replay-identity">
        <p class="replay-eyebrow">SIMULATION EVIDENCE</p>
        <div class="replay-title-row">
          <h2>仿真证据回放</h2>
          <span class="read-only-badge">只读现场</span>
        </div>
        <p>{{ mapTitle }} · 报告 {{ reportShortId }}<span v-if="replay?.totalSamples"> · {{ replay.totalSamples }} 条原始采样</span></p>
      </div>
      <div class="replay-command-actions">
        <el-button class="load-action" :loading="loading" :disabled="playing || videoProgress !== null" @click="load">
          {{ replay ? "重新载入" : "载入时间轴" }}
        </el-button>
        <el-button class="export-action" aria-label="导出回放视频" :disabled="!timeline.length" :loading="videoProgress !== null" @click="exportVideo">
          {{ videoProgress === null ? "导出视频" : `录制 ${videoProgress}%` }}
        </el-button>
      </div>
    </header>

    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <p v-if="notice" class="replay-notice" role="status">{{ notice }}</p>
    <p v-if="replay && !frame" class="replay-empty">本轮没有可回放的观察采样。</p>

    <template v-if="frame">
      <section class="transport-deck" aria-label="回放控制">
        <button type="button" class="play-control" :aria-label="playing ? '暂停回放' : '播放回放'"
          :disabled="timeline.length < 2" @click="togglePlayback">
          <span class="play-symbol">{{ playing ? "Ⅱ" : "▶" }}</span>
          <span><b>{{ playing ? "暂停" : "播放" }}</b><small>{{ playing ? "正在按记录时间推进" : "从当前采样继续" }}</small></span>
        </button>
        <div class="transport-clock">
          <span>{{ displayDate(frame.time) }}</span>
          <strong>{{ displayClock(frame.time) }}</strong>
          <small>采样 {{ frameIndex + 1 }} / {{ timeline.length }}</small>
        </div>
        <label class="replay-scrubber">
          <span class="sr-only">回放时间点</span>
          <input v-model.number="frameIndex" type="range" min="0" :max="timeline.length - 1" aria-label="回放时间点"
            @input="playing = false" />
          <span class="scrubber-labels">
            <span>{{ timeline[0] ? displayClock(timeline[0].time) : "" }}</span>
            <span>{{ timeline.at(-1) ? displayClock(timeline.at(-1)!.time) : "" }}</span>
          </span>
        </label>
        <div class="speed-controls">
          <ReplaySpeed v-if="timeline.length > 1" v-model="playbackRate" label="播放倍速" />
          <ReplaySpeed v-model="exportRate" label="导出倍速" :disabled="videoProgress !== null" />
        </div>
        <div v-if="videoProgress !== null" class="export-progress">
          <span>视频写入 {{ videoProgress }}%</span>
          <button type="button" @click="cancelExport">取消导出</button>
        </div>
      </section>

      <section class="replay-kpis" aria-label="当前回放摘要">
        <article>
          <span>在线车辆</span>
          <strong>{{ onlineVehicleCount }}<small>/ {{ vehicleCards.length }}</small></strong>
          <p>{{ waitingVehicleCount ? `${waitingVehicleCount} 台正在等待` : "当前无车辆等待" }}</p>
        </article>
        <article>
          <span>活动任务</span>
          <strong>{{ activeTaskCount }}</strong>
          <p>当前帧共记录 {{ currentTasks.length }} 条任务</p>
        </article>
        <article class="focus-kpi">
          <span>当前焦点</span>
          <strong>{{ selectedVehicle || "未选择车辆" }}</strong>
          <p v-if="selectedCard">{{ selectedCard.state.label }} · {{ selectedCard.car.site || "位置未返回" }}</p>
          <p v-else>点击车辆卡快速定位</p>
        </article>
        <article :class="{ caution: replay?.evidence?.warnings?.length }">
          <span>证据完整度</span>
          <strong>{{ replay?.evidence?.warnings?.length ? "有缺口" : "已对齐" }}</strong>
          <p>{{ replay?.evidence?.warnings?.[0] || `记录倍率 ${playbackRateBase}× · 事件 ${events.length} 条` }}</p>
        </article>
      </section>

      <div class="replay-workspace">
        <section class="replay-stage">
          <header class="section-heading">
            <div>
              <p class="section-kicker">LIVE REPLAY MAP</p>
              <h3>{{ selectedCard ? `${selectedCard.car.vehicleId} · ${selectedCard.state.label}` : "全场态势" }}</h3>
              <p v-if="selectedCard">
                {{ selectedCard.car.site || "位置未返回" }}
                <template v-if="selectedCard.task"> · {{ selectedCard.task.stationId }} · {{ stageLabel(selectedCard.task.stage) }}</template>
              </p>
              <p v-else>选择一台车查看它在地图中的位置和任务上下文。</p>
            </div>
            <div class="stage-focus" :class="selectedCard?.state.tone">
              <span v-if="positionEvent">已定位事件</span>
              <span v-else>当前车辆</span>
              <strong>{{ positionEvent?.title || vehicleSummary(selectedCard?.task, selectedCard?.car.waitReason) }}</strong>
              <small v-if="positionEvent">{{ displayTime(positionEvent.time) }}</small>
            </div>
          </header>
          <div class="replay-map-shell">
            <SimulationWorkshopMap v-if="replay?.map" :definition="replay.map" :cars="cars" :fresh="true"
              :instant-vehicles="true" compact-header :selected-vehicle="selectedVehicle" :location="location"
              @select-vehicle="locateVehicle" />
            <div v-else class="map-missing">{{ replay?.mapMessage || "该轮报告未提供地图快照" }}</div>
          </div>
        </section>

        <aside class="vehicle-board" aria-label="当时车辆">
          <header class="board-heading">
            <div>
              <p class="section-kicker">VEHICLES AT THIS MOMENT</p>
              <h3>当时车辆</h3>
            </div>
            <span>{{ vehicleCards.length }} 台</span>
          </header>
          <div class="vehicle-stack">
            <button v-for="item in vehicleCards" :key="item.car.vehicleId" type="button"
              class="vehicle-card" :class="[item.state.tone, { selected: item.car.vehicleId === selectedVehicle }]"
              @click="selectVehicleCard(item.car.vehicleId)">
              <span class="vehicle-topline">
                <i />
                <b>{{ item.car.vehicleId }}</b>
                <em>{{ item.state.label }}</em>
                <small>{{ item.car.site || "位置未返回" }}</small>
              </span>
              <span class="vehicle-taskline">
                {{ item.task ? `${item.task.stationId} · ${stageLabel(item.task.stage)}` : "当前没有业务任务" }}
              </span>
              <span class="vehicle-reason">{{ vehicleSummary(item.task, item.car.waitReason) }}</span>
            </button>
            <p v-if="!vehicleCards.length" class="board-empty">当前采样没有车辆反馈。</p>
          </div>
          <section v-if="selectedCard" class="vehicle-focus">
            <header><span>{{ selectedCard.car.vehicleId }}</span><em>{{ selectedCard.state.label }}</em></header>
            <dl>
              <div><dt>当前位置</dt><dd>{{ selectedCard.car.site || "未返回" }}</dd></div>
              <div><dt>原始坐标</dt><dd>{{ validPosition(selectedCard.car) ? `${selectedCard.car.x.toFixed(2)}, ${selectedCard.car.y.toFixed(2)}` : "未返回" }}</dd></div>
              <div><dt>当前任务</dt><dd>{{ shortTaskId(selectedCard.task?.taskId) }}</dd></div>
              <div><dt>载荷状态</dt><dd>{{ selectedCard.car.loaded ? "载箱" : "空载" }}</dd></div>
            </dl>
            <ol v-if="recentVehicleEvents.length" class="focus-events">
              <li v-for="event in recentVehicleEvents" :key="event.time + event.kind + event.title">
                <time>{{ displayClock(event.time) }}</time><span>{{ event.title }}</span>
              </li>
            </ol>
          </section>
        </aside>
      </div>

      <section class="task-board" aria-label="当时任务">
        <header class="board-heading">
          <div>
            <p class="section-kicker">TASKS AT THIS MOMENT</p>
            <h3>当时任务</h3>
            <p>按车辆、工位和业务步骤对齐，点击任务卡会定位对应车辆。</p>
          </div>
          <span>{{ activeTaskCount }} 个活动任务</span>
        </header>
        <div v-if="currentTasks.length" class="task-grid">
          <button v-for="task in currentTasks" :key="task.taskId" type="button" class="task-card"
            :class="{ selected: task.vehicleId === selectedVehicle, terminal: terminalStages.has(task.stage ?? '') }"
            @click="selectTask(task)">
            <span class="task-card-top">
              <b>{{ task.stationId }}</b>
              <em>{{ stageLabel(task.stage) }}</em>
            </span>
            <strong>{{ task.vehicleId || "等待派车" }}<small>{{ shortTaskId(task.taskId) }}</small></strong>
            <p>{{ taskSummary(task) }}</p>
            <span class="task-card-foot">
              <span>来源 · 任务状态采样</span>
              <span>{{ task.vehicleId ? "点击定位车辆" : "尚未分配车辆" }}</span>
            </span>
          </button>
        </div>
        <p v-else class="board-empty">该采样没有业务任务。</p>
      </section>

      <ReplayEvidence v-if="replay" :events="events" :frames="timeline" :coverage="replay.evidence" @seek="seekEvent" />
    </template>
  </section>
</template>

<style scoped>
.replay-console {
  --console-ink:#17283a;
  --console-muted:#687b8f;
  --console-line:#dbe4ed;
  --console-navy:#102334;
  display:grid;
  gap:14px;
  min-width:0;
  margin:16px 0;
  padding:12px;
  color:var(--console-ink);
  background:#eef3f7;
  border:1px solid #d7e1ea;
  border-radius:18px;
  box-shadow:0 18px 46px rgb(31 55 78 / 10%);
}
.replay-masthead {
  position:relative;
  display:flex;
  align-items:center;
  justify-content:space-between;
  gap:24px;
  overflow:hidden;
  padding:22px 24px;
  color:#f4fbff;
  background:
    radial-gradient(circle at 78% -35%,rgb(82 184 205 / 28%),transparent 42%),
    linear-gradient(125deg,#102334,#17394d 58%,#124b58);
  border-radius:14px;
}
.replay-masthead::after {
  content:"";
  position:absolute;
  inset:0;
  pointer-events:none;
  opacity:.16;
  background-image:linear-gradient(rgb(255 255 255 / 10%) 1px,transparent 1px),linear-gradient(90deg,rgb(255 255 255 / 10%) 1px,transparent 1px);
  background-size:28px 28px;
  mask-image:linear-gradient(90deg,transparent,#000);
}
.replay-identity,.replay-command-actions { position:relative; z-index:1; }
.replay-identity { min-width:0; }
.replay-eyebrow,.section-kicker {
  margin:0;
  font-size:9px;
  font-weight:850;
  letter-spacing:.18em;
}
.replay-eyebrow { color:#79d9e8; }
.replay-title-row { display:flex; align-items:center; gap:10px; margin-top:5px; }
.replay-title-row h2 { margin:0; font-size:26px; line-height:1.15; letter-spacing:.02em; }
.replay-identity>p:last-child { margin:7px 0 0; color:rgb(228 247 252 / 66%); font-size:12px; }
.read-only-badge { padding:4px 8px; color:#bdeff6; background:rgb(111 219 232 / 12%); border:1px solid rgb(139 228 239 / 24%); border-radius:999px; font-size:10px; font-weight:750; }
.replay-command-actions { display:flex; flex:none; gap:8px; }
.replay-command-actions :deep(.el-button) { min-width:98px; margin:0; border-radius:9px; }
.load-action { color:#dceff5 !important; background:rgb(255 255 255 / 8%) !important; border-color:rgb(255 255 255 / 24%) !important; }
.export-action { color:#0f2e3a !important; background:#8ee1e7 !important; border-color:#8ee1e7 !important; font-weight:800 !important; }
.replay-notice { margin:0; padding:9px 12px; color:#266b54; background:#e8f7f0; border:1px solid #c8eadc; border-radius:9px; font-size:12px; }
.replay-empty { margin:0; padding:32px; text-align:center; color:var(--console-muted); background:#fff; border-radius:14px; }
.transport-deck {
  display:grid;
  grid-template-columns:auto 152px minmax(180px,1fr) auto;
  align-items:center;
  gap:14px;
  padding:12px 14px;
  color:#effaff;
  background:#12293a;
  border:1px solid #1d4055;
  border-radius:13px;
  box-shadow:inset 0 1px rgb(255 255 255 / 5%);
}
.play-control {
  display:flex;
  align-items:center;
  gap:10px;
  min-width:116px;
  padding:8px 11px;
  color:#effaff;
  background:rgb(255 255 255 / 6%);
  border:1px solid rgb(255 255 255 / 14%);
  border-radius:9px;
  cursor:pointer;
}
.play-control:hover { background:rgb(255 255 255 / 10%); }
.play-control:disabled { opacity:.45; cursor:default; }
.play-symbol {
  display:grid;
  width:29px;
  height:29px;
  place-items:center;
  color:#102938;
  background:#8de2e8;
  border-radius:50%;
  font-size:12px;
  font-weight:900;
}
.play-control>b { display:block; font-size:12px; }
.play-control small { display:block; margin-top:2px; color:rgb(226 246 251 / 52%); font-size:9px; }
.transport-clock { display:grid; grid-template-columns:auto 1fr; align-items:end; column-gap:8px; }
.transport-clock>span { grid-column:1 / -1; color:#7ea2b5; font-size:9px; letter-spacing:.05em; }
.transport-clock strong { color:#fff; font:800 20px/1.1 ui-monospace,SFMono-Regular,Consolas,monospace; font-variant-numeric:tabular-nums; }
.transport-clock small { color:#8aa9ba; font-size:9px; white-space:nowrap; }
.replay-scrubber { display:grid; gap:3px; min-width:0; }
.replay-scrubber input { width:100%; accent-color:#75d9e2; cursor:pointer; }
.scrubber-labels { display:flex; justify-content:space-between; color:#7799ab; font:9px ui-monospace,SFMono-Regular,Consolas,monospace; }
.speed-controls { display:flex; gap:7px; --replay-speed-bg:rgb(255 255 255 / 5%); --replay-speed-border:rgb(255 255 255 / 14%); --replay-speed-label:rgb(226 246 251 / 54%); --replay-speed-color:#effaff; --replay-speed-focus:#75d9e2; }
.export-progress { grid-column:1 / -1; display:flex; align-items:center; justify-content:flex-end; gap:10px; color:#a8cad8; font-size:11px; }
.export-progress button { color:#c9f4f7; background:transparent; border:1px solid #477288; border-radius:7px; padding:4px 8px; cursor:pointer; }
.replay-kpis { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:8px; }
.replay-kpis article {
  position:relative;
  overflow:hidden;
  min-height:94px;
  padding:13px 15px;
  background:#fff;
  border:1px solid var(--console-line);
  border-radius:12px;
}
.replay-kpis article::before { content:""; position:absolute; inset:0 auto 0 0; width:3px; background:#80a6bb; }
.replay-kpis .focus-kpi::before { background:#2f94b2; }
.replay-kpis .caution::before { background:#d19a3c; }
.replay-kpis article>span { color:var(--console-muted); font-size:10px; font-weight:700; letter-spacing:.05em; }
.replay-kpis article>strong { display:block; margin-top:7px; overflow:hidden; color:var(--console-ink); font-size:21px; line-height:1.1; text-overflow:ellipsis; white-space:nowrap; }
.replay-kpis article>strong small { color:#92a3b3; font-size:11px; }
.replay-kpis article>p { margin:6px 0 0; overflow:hidden; color:#6c7f91; font-size:10px; line-height:1.35; text-overflow:ellipsis; white-space:nowrap; }
.replay-workspace { display:grid; grid-template-columns:minmax(0,1fr) 340px; align-items:stretch; gap:12px; }
.replay-stage,.vehicle-board,.task-board { min-width:0; background:#fff; border:1px solid var(--console-line); border-radius:14px; }
.replay-stage { overflow:hidden; }
.section-heading,.board-heading { display:flex; align-items:flex-start; justify-content:space-between; gap:16px; }
.section-heading { padding:15px 17px 12px; border-bottom:1px solid var(--console-line); }
.section-kicker { color:#4d8197; }
.section-heading h3,.board-heading h3 { margin:4px 0 0; font-size:17px; line-height:1.3; }
.section-heading>div>p:last-child,.board-heading>div>p:last-child { margin:5px 0 0; color:var(--console-muted); font-size:10px; }
.stage-focus { display:grid; justify-items:end; max-width:330px; text-align:right; }
.stage-focus>span { color:#4d8197; font-size:9px; font-weight:800; letter-spacing:.06em; }
.stage-focus>strong { display:-webkit-box; overflow:hidden; margin-top:4px; color:var(--console-ink); font-size:11px; line-height:1.4; -webkit-box-orient:vertical; -webkit-line-clamp:2; }
.stage-focus>small { margin-top:3px; color:var(--console-muted); font-size:9px; }
.stage-focus.warning>span,.stage-focus.danger>span { color:#a6671c; }
.replay-map-shell { position:relative; padding:8px 10px 2px; background:linear-gradient(180deg,#f8fbfd,#fff); }
.replay-map-shell :deep(.workshop-preview) { min-height:500px; }
.replay-map-shell :deep(.preview-canvas) { height:clamp(440px,55dvh,620px); }
.replay-map-shell :deep(.vehicle-legend) { display:none; }
.replay-map-shell :deep(.preview-vehicle text) { font-size:13px; font-weight:800; }
.map-missing { display:grid; min-height:480px; place-items:center; color:var(--console-muted); background:#f5f8fb; border-radius:10px; }
.vehicle-board { display:flex; flex-direction:column; overflow:hidden; }
.board-heading { padding:15px 16px 12px; }
.board-heading>span { flex:none; padding:5px 8px; color:#316f88; background:#eaf5fa; border-radius:999px; font-size:10px; font-weight:800; }
.vehicle-stack { display:grid; gap:6px; padding:0 10px 10px; }
.vehicle-card {
  --status:#62829a;
  display:grid;
  gap:5px;
  width:100%;
  min-height:88px;
  padding:10px 11px;
  text-align:left;
  color:var(--console-ink);
  background:#f7fafc;
  border:1px solid #dce5ec;
  border-left:3px solid var(--status);
  border-radius:9px;
  cursor:pointer;
  transition:border-color .16s,background .16s,transform .16s;
}
.vehicle-card:hover { background:#fff; border-color:#b8cad8; transform:translateX(2px); }
.vehicle-card.selected { background:#fff; border-color:var(--status); box-shadow:0 5px 16px rgb(41 74 99 / 10%); }
.vehicle-card.moving { --status:#1aa38b; }
.vehicle-card.waiting,.vehicle-card.warning { --status:#d39737; }
.vehicle-card.danger { --status:#cc4f69; }
.vehicle-card.idle { --status:#4b8ec3; }
.vehicle-card.muted { --status:#8493a1; }
.vehicle-topline { display:grid; grid-template-columns:7px auto auto minmax(0,1fr); align-items:center; gap:7px; }
.vehicle-topline i { width:7px; height:7px; background:var(--status); border-radius:50%; box-shadow:0 0 0 3px color-mix(in srgb,var(--status) 15%,transparent); }
.vehicle-topline b { font-size:13px; }
.vehicle-topline em { padding:2px 6px; color:var(--status); background:color-mix(in srgb,var(--status) 10%,#fff); border-radius:999px; font-size:9px; font-style:normal; font-weight:800; }
.vehicle-topline small { overflow:hidden; color:#6f8292; font-size:9px; text-align:right; text-overflow:ellipsis; white-space:nowrap; }
.vehicle-taskline { overflow:hidden; color:#30485c; font-size:11px; font-weight:700; text-overflow:ellipsis; white-space:nowrap; }
.vehicle-reason { display:-webkit-box; overflow:hidden; color:#6d7f8f; font-size:10px; line-height:1.4; -webkit-box-orient:vertical; -webkit-line-clamp:2; }
.vehicle-focus { margin:0 10px 10px; padding:12px; background:#10293a; color:#e7f6fb; border-radius:10px; }
.vehicle-focus>header { display:flex; align-items:center; justify-content:space-between; gap:8px; }
.vehicle-focus>header>span { font-size:13px; font-weight:850; }
.vehicle-focus>header>em { color:#8ce0e5; font-size:10px; font-style:normal; }
.vehicle-focus dl { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:8px; margin:10px 0 0; }
.vehicle-focus dl div { min-width:0; }
.vehicle-focus dt { color:#7896a7; font-size:8px; }
.vehicle-focus dd { overflow:hidden; margin:2px 0 0; color:#e4f5fa; font-size:10px; font-weight:700; text-overflow:ellipsis; white-space:nowrap; }
.focus-events { display:grid; gap:4px; margin:10px 0 0; padding:8px 0 0; list-style:none; border-top:1px solid rgb(255 255 255 / 10%); }
.focus-events li { display:grid; grid-template-columns:44px minmax(0,1fr); gap:6px; color:#a8c3d0; font-size:9px; }
.focus-events time { color:#68cbd4; font-family:ui-monospace,SFMono-Regular,Consolas,monospace; }
.focus-events span { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.task-board { overflow:hidden; }
.task-board>.board-heading { border-bottom:1px solid var(--console-line); }
.task-grid { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:8px; padding:12px; }
.task-card {
  display:grid;
  gap:8px;
  min-width:0;
  min-height:150px;
  padding:13px;
  text-align:left;
  color:var(--console-ink);
  background:#f9fbfd;
  border:1px solid #dce5ec;
  border-radius:10px;
  cursor:pointer;
  transition:border-color .16s,background .16s,transform .16s;
}
.task-card:hover { background:#fff; border-color:#aac1d1; transform:translateY(-1px); }
.task-card.selected { background:#fff; border-color:#2d8cab; box-shadow:inset 3px 0 #2d8cab; }
.task-card.terminal { opacity:.66; }
.task-card-top { display:flex; align-items:center; justify-content:space-between; gap:10px; }
.task-card-top b { color:#1d6581; font-size:12px; }
.task-card-top em { padding:3px 7px; color:#536f80; background:#eaf0f4; border-radius:999px; font-size:9px; font-style:normal; font-weight:750; }
.task-card>strong { display:flex; align-items:center; justify-content:space-between; gap:10px; min-width:0; font-size:13px; }
.task-card>strong small { overflow:hidden; max-width:58%; color:#8393a1; font:9px ui-monospace,SFMono-Regular,Consolas,monospace; text-overflow:ellipsis; white-space:nowrap; }
.task-card>p { display:-webkit-box; overflow:hidden; margin:0; color:#627687; font-size:10px; line-height:1.5; -webkit-box-orient:vertical; -webkit-line-clamp:2; }
.task-card-foot { display:flex; align-items:center; justify-content:space-between; gap:8px; margin-top:auto; padding-top:8px; color:#80919f; border-top:1px solid #e5ebf0; font-size:9px; }
.board-empty { margin:0; padding:26px 16px; text-align:center; color:var(--console-muted); }
.sr-only { position:absolute; width:1px; height:1px; padding:0; overflow:hidden; clip:rect(0,0,0,0); white-space:nowrap; border:0; }
@media (max-width:1180px) {
  .transport-deck { grid-template-columns:auto 150px minmax(150px,1fr); }
  .speed-controls { grid-column:1 / -1; justify-content:flex-end; }
  .replay-workspace { grid-template-columns:1fr; }
  .vehicle-stack { grid-template-columns:repeat(2,minmax(0,1fr)); }
  .vehicle-focus { display:none; }
  .task-grid { grid-template-columns:repeat(2,minmax(0,1fr)); }
}
@media (max-width:760px) {
  .replay-console { padding:8px; border-radius:14px; }
  .replay-masthead { align-items:flex-start; flex-direction:column; padding:18px; }
  .replay-command-actions { width:100%; }
  .replay-command-actions :deep(.el-button) { flex:1; }
  .transport-deck { grid-template-columns:1fr 1fr; }
  .replay-scrubber { grid-column:1 / -1; order:2; }
  .speed-controls { justify-content:flex-start; flex-wrap:wrap; }
  .replay-kpis { grid-template-columns:repeat(2,minmax(0,1fr)); }
  .replay-map-shell :deep(.preview-canvas) { height:420px; }
  .vehicle-stack,.task-grid { grid-template-columns:1fr; }
  .section-heading { align-items:flex-start; flex-direction:column; }
  .stage-focus { align-items:flex-start; justify-items:start; text-align:left; }
}
</style>
