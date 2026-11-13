<script setup lang="ts">
import {
  computed,
  onMounted,
  onBeforeUnmount,
  ref,
  shallowRef,
  watch,
} from "vue";
import { ElMessage } from "element-plus";
import RoadNetScene from "@/components/RoadNetScene.vue";
import DispatchMapImport from "@/components/DispatchMapImport.vue";
import {
  getRoadNet,
  listRoadNets,
  type RoadNetSummary,
  type RoadNetPayload,
} from "@/api/roadnet";
import { createTaskSet } from "@/api/sim";
import {
  dispatchSceneMap,
  dispatchViews,
  transportTasks,
  roadnetFingerprint,
  dispatchIsFresh,
  dispatchRoutePoints,
  type DispatchTaskDetail,
} from "@/integration/dispatch-adapter";
import {
  dispatchRead,
  downloadIntegrationRecord,
  type DispatchStatus,
  type TaskRow,
  type ResourceRow,
} from "@/integration/dispatch-api";

const status = ref<DispatchStatus | null>(null),
  tasks = ref<TaskRow[]>([]),
  resources = ref<ResourceRow[]>([]);
const connected = ref(false),
  error = ref(""),
  detailError = ref(""),
  busy = ref(false),
  observedAt = ref(0);
const detail = ref<DispatchTaskDetail | null>(null),
  selectedTask = ref(""),
  selectedScheme = ref("");
const scenario = ref("normal-cycle"),
  rounds = ref(1),
  targetMap = ref<number | null>(null),
  maps = ref<RoadNetSummary[]>([]);
const selectedTasks = ref<TaskRow[]>([]),
  importDialog = ref(false),
  importPreview = ref<ReturnType<typeof transportTasks> | null>(null);
const importName = ref("工程原命令运输实验"),
  savedTaskSet = ref<number | null>(null);
const liveMap = shallowRef<RoadNetPayload | null>(null),
  liveMapSignature = ref(""),
  scene = ref<InstanceType<typeof RoadNetScene> | null>(null);
const pageAbort = new AbortController();
let timer: ReturnType<typeof setTimeout> | undefined,
  disposed = false,
  actionRevision = 0;
const clock = ref(Date.now());
const clockTimer = setInterval(() => (clock.value = Date.now()), 1000);
const fresh = computed(
  () => connected.value && dispatchIsFresh(status.value, clock.value),
);
const cars = computed(() => status.value?.device?.cars ?? {});
const vehicles = computed(() => dispatchViews(cars.value, fresh.value));
const scenarioActive = computed(() =>
  ["running", "starting", "pending", "stopping", "aborting"].includes(
    status.value?.scenario?.status?.toLowerCase() ?? "",
  ),
);
const actionsEnabled = computed(
  () => connected.value && !!status.value?.enabled && !busy.value,
);
const stateName: Record<string, string> = {
  IDLE: "空载待命",
  MOVING: "行驶",
  WAITING: "作业等待",
  CHARGING: "充电",
  OFFLINE: "离线或状态过期",
  FAULT: "暂停或故障",
  BLOCKED: "受阻",
};
const sceneNames = [
  { value: "normal-cycle", label: "完整业务循环" },
  { value: "simultaneous-calls", label: "同时呼叫" },
  { value: "plc-delay", label: "PLC 延迟" },
  { value: "disconnect-reconnect", label: "断连重连" },
];

async function loadMaps() {
  maps.value = await listRoadNets();
}
async function poll() {
  const revision = actionRevision;
  try {
    const next = await dispatchRead<DispatchStatus>(
      "/api/simulation",
      pageAbort.signal,
    );
    if (disposed || revision !== actionRevision) return;
    status.value = next;
    observedAt.value = Date.now();
    error.value = next.error ?? "";
    const signature = JSON.stringify(next.map ?? null);
    if (signature !== liveMapSignature.value) {
      liveMap.value = next.map ? dispatchSceneMap(next.map) : null;
      liveMapSignature.value = signature;
    }
    const results = await Promise.allSettled([
      dispatchRead<{ items: TaskRow[] }>(
        "/api/simulation/tasks?page=1&pageSize=200",
        pageAbort.signal,
      ),
      dispatchRead<ResourceRow[]>(
        "/api/simulation/resources",
        pageAbort.signal,
      ),
    ]);
    if (disposed || revision !== actionRevision) return;
    if (results[0].status === "fulfilled")
      tasks.value = results[0].value.items ?? [];
    if (results[1].status === "fulfilled")
      resources.value = results[1].value ?? [];
    const failures = results.filter(
      (r) => r.status === "rejected",
    ) as PromiseRejectedResult[];
    connected.value = failures.length === 0;
    if (failures.length)
      error.value = failures.map((r) => String(r.reason)).join(" · ");
    if (selectedTask.value) await selectTask(selectedTask.value, false);
  } catch (e) {
    if (!disposed) {
      connected.value = false;
      error.value = e instanceof Error ? e.message : String(e);
    }
  } finally {
    if (!disposed) timer = setTimeout(poll, 1000);
  }
}
async function selectTask(id: string, clear = true) {
  selectedTask.value = id;
  if (clear) {
    detail.value = null;
    detailError.value = "";
    scene.value?.clearPath();
  }
  try {
    const next = await dispatchRead<DispatchTaskDetail>(
      `/api/simulation/tasks/${encodeURIComponent(id)}`,
      pageAbort.signal,
    );
    if (disposed || selectedTask.value !== id) return;
    detail.value = next;
    detailError.value = "";
    const route = next.commands?.find((c) =>
      ["Sent", "Sending", "Accepted", "Executing"].includes(c.status ?? ""),
    )?.route;
    const points = liveMap.value
      ? dispatchRoutePoints(
          liveMap.value,
          route?.nodeIds ?? [],
          route?.edgeIds ?? [],
        )
      : [];
    if (points.length > 1) scene.value?.drawPath(points);
    else scene.value?.clearPath();
  } catch (e) {
    if (!disposed)
      detailError.value = e instanceof Error ? e.message : String(e);
  }
}
async function action(name: string, body: Record<string, unknown> = {}) {
  if (!actionsEnabled.value) return;
  busy.value = true;
  error.value = "";
  actionRevision++;
  try {
    status.value = await dispatchRead<DispatchStatus>(
      `/api/simulation/${name}`,
      pageAbort.signal,
      body,
    );
    observedAt.value = Date.now();
    ElMessage.success("仿真控制器已受理");
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
async function previewTasks() {
  if (!targetMap.value || !selectedTasks.value.length) return;
  busy.value = true;
  error.value = "";
  savedTaskSet.value = null;
  try {
    const map = await getRoadNet(targetMap.value);
    const cached = map.integration
      ? null
      : JSON.parse(
          localStorage.getItem(`dispatch-import-${targetMap.value}`) ?? "null",
        );
    const identity = map.integration?.identity ?? cached?.identity;
    const hash = identity?.definitionHash;
    const source = status.value?.configuration?.mapIdentity;
    if (!hash || hash !== source?.definitionHash)
      throw new Error(
        "目标路网缺少与当前仿真地图一致的来源摘要，请先从本实例导入地图",
      );
    const expected =
      map.integration?.importedGraphFingerprint ??
      (cached?.roadnet ? roadnetFingerprint(cached.roadnet) : null);
    if (!expected || roadnetFingerprint(map) !== expected)
      throw new Error("目标实验路网已修改，请重新核对并导入当前来源地图");
    const details: DispatchTaskDetail[] = [];
    for (const task of selectedTasks.value)
      details.push(
        await dispatchRead<DispatchTaskDetail>(
          `/api/simulation/tasks/${encodeURIComponent(task.taskId)}`,
          pageAbort.signal,
        ),
      );
    importPreview.value = transportTasks(details, map);
    importDialog.value = true;
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
async function saveTasks() {
  if (
    !targetMap.value ||
    !importPreview.value?.tasks.length ||
    savedTaskSet.value
  )
    return;
  busy.value = true;
  try {
    const saved = await createTaskSet({
      name: importName.value,
      roadnetId: targetMap.value,
      taskSet: { tasks: importPreview.value.tasks },
    });
    savedTaskSet.value = saved.id;
    ElMessage.success("实验任务集已保存");
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e));
  } finally {
    busy.value = false;
  }
}
function mapSaved(id: number) {
  targetMap.value = id;
  void loadMaps();
}
function reportDownload() {
  downloadIntegrationRecord(
    status.value?.report ?? {},
    "dispatch-simulation-report.json",
  );
}
watch(
  () => status.value?.rootName,
  () => {
    selectedTask.value = "";
    detail.value = null;
    selectedTasks.value = [];
    tasks.value = [];
    resources.value = [];
    scene.value?.clearPath();
  },
);
onMounted(() => {
  void poll();
  void loadMaps().catch((e) => (error.value = String(e)));
});
onBeforeUnmount(() => {
  disposed = true;
  pageAbort.abort();
  clearTimeout(timer);
  clearInterval(clockTimer);
});
</script>

<template>
  <div class="dispatch-page">
    <section class="panel source-panel">
      <div>
        <h2>主工程接入</h2>
        <p class="muted">
          5199 · 当前隔离仿真实例 {{ status?.rootName || "尚未连接" }}
        </p>
      </div>
      <el-space wrap
        ><el-tag :type="fresh ? 'success' : 'warning'">{{
          fresh ? "设备反馈有效" : connected ? "设备状态待刷新" : "主工程未连接"
        }}</el-tag
        ><span class="muted">{{
          observedAt
            ? new Date(observedAt).toLocaleTimeString()
            : "暂无成功观察"
        }}</span
        ><el-button @click="$router.push('/simulation')"
          >进入原生算法仿真</el-button
        ></el-space
      >
    </section>
    <el-alert
      v-if="error"
      :title="error"
      type="error"
      :closable="false"
      show-icon
    />
    <section class="panel controls">
      <el-space wrap>
        <el-select
          v-model="selectedScheme"
          placeholder="选择主工程已保存方案"
          style="width: 240px"
          :disabled="busy || !!status?.running"
          ><el-option
            v-for="scheme in status?.schemes ?? []"
            :key="scheme.id"
            :label="`${scheme.name} · ${scheme.vehicleCount ?? '?'}辆`"
            :value="scheme.id"
        /></el-select>
        <el-button
          type="primary"
          :disabled="!actionsEnabled || !selectedScheme || !!status?.running"
          @click="action('scheme-start', { id: selectedScheme })"
          >按方案一键开测</el-button
        >
        <el-button
          :disabled="!actionsEnabled || !!status?.running"
          @click="action('start')"
          >启动隔离环境</el-button
        >
        <el-button
          :disabled="!actionsEnabled || !status?.running || scenarioActive"
          @click="action('stop')"
          >停止隔离环境</el-button
        >
        <el-select
          v-model="scenario"
          style="width: 160px"
          :disabled="busy || scenarioActive"
          ><el-option
            v-for="item in sceneNames"
            :key="item.value"
            :label="item.label"
            :value="item.value"
        /></el-select>
        <el-input-number
          v-model="rounds"
          :min="1"
          :max="100"
          :disabled="busy || scenarioActive"
          aria-label="业务循环轮次"
        />
        <el-button
          type="primary"
          :disabled="
            !actionsEnabled || !status?.running || !fresh || scenarioActive
          "
          @click="
            action('run', {
              name: scenario,
              rounds,
              intervalSeconds: 1,
              timeoutSeconds: 600,
            })
          "
          >运行业务场景</el-button
        >
        <el-button
          :disabled="!actionsEnabled || !scenarioActive"
          @click="action('abort')"
          >中止后续编排</el-button
        >
      </el-space>
      <p v-if="status?.scenario" class="muted">
        {{ status.scenario.name }} · {{ status.scenario.status }} ·
        {{ status.scenario.message }}
      </p>
    </section>
    <div class="dispatch-body">
      <section class="panel dispatch-scene">
        <div class="panel-heading">
          <h3>隔离仿真三维路网</h3>
          <el-button size="small" @click="scene?.fitView()">适应画面</el-button>
        </div>
        <RoadNetScene
          v-if="liveMap"
          ref="scene"
          :roadnet="liveMap"
          :agvs="vehicles"
          observed
        /><el-empty v-else description="连接主工程后显示原始地图与车辆反馈" />
      </section>
      <section class="panel vehicles">
        <h3>车辆与等待原因</h3>
        <el-empty v-if="!vehicles.length" description="暂无车辆反馈" />
        <article v-for="car in vehicles" :key="car.id" class="vehicle-row">
          <div>
            <strong>{{ car.id }}</strong><el-tag
              size="small"
              :type="
                car.status === 'OFFLINE'
                  ? 'info'
                  : car.status === 'MOVING'
                    ? 'success'
                    : 'warning'
              "
              >{{ stateName[car.status] || car.status }}</el-tag
            >
          </div>
          <p>
            {{ cars[car.id]?.station || "站间" }} ·
            {{ cars[car.id]?.loaded ? "载箱" : "空载" }}
          </p>
          <p>
            X {{ car.x.toFixed(3) }} · Y {{ car.y.toFixed(3) }} m ·
            {{ car.heading.toFixed(1) }}°
          </p>
          <p>{{ cars[car.id]?.waitReason || "未报告等待原因" }}</p>
          <el-button
            v-if="tasks.find((t) => t.vehicleId === car.id)"
            size="small"
            @click="
              selectTask(tasks.find((t) => t.vehicleId === car.id)!.taskId)
            "
            >查看原任务</el-button
          >
        </article>
      </section>
    </div>
    <section class="panel tasks-panel">
      <div class="panel-heading">
        <h3>原任务 · 最近 {{ tasks.length }} 条</h3>
        <el-space wrap
          ><el-select
            v-model="targetMap"
            style="width: 220px"
            placeholder="选择导入后的实验路网"
            ><el-option
              v-for="map in maps"
              :key="map.id"
              :label="map.name"
              :value="map.id" /></el-select
          ><el-button
            :disabled="!fresh || busy || !targetMap || !selectedTasks.length"
            @click="previewTasks"
            >预览选中命令为实验任务</el-button
          ></el-space
        >
      </div>
      <el-table
        :data="tasks"
        row-key="taskId"
        @selection-change="selectedTasks = $event"
        @row-click="(row: TaskRow) => selectTask(row.taskId)"
        ><el-table-column type="selection" width="45" /><el-table-column
          prop="taskId"
          label="原任务 ID"
          min-width="240" /><el-table-column
          prop="vehicleId"
          label="车辆"
          width="120" /><el-table-column
          prop="stationId"
          label="工位"
          width="140" /><el-table-column prop="stage" label="阶段" width="160"
      /></el-table>
      <el-alert
        v-if="detailError"
        :title="detailError"
        type="warning"
        :closable="false"
      />
      <div v-if="detail" class="task-detail">
        <h4>
          {{ detail.task.taskId }} ·
          {{ detail.waitingExplanation || "当前无等待说明" }}
        </h4>
        <el-table :data="detail.steps ?? []" size="small"
          ><el-table-column
            prop="sequence"
            label="步骤"
            width="70" /><el-table-column
            prop="stepType"
            label="类型"
            width="190" /><el-table-column
            prop="status"
            label="状态"
            width="130" /><el-table-column
            prop="waitingExplanation"
            label="等待说明"
        /></el-table>
        <p
          v-for="command in detail.commands ?? []"
          :key="command.commandId"
          class="muted"
        >
          {{ command.commandId }} · {{ command.status }} ·
          {{ command.route?.nodeIds?.join(" → ") }}
        </p>
      </div>
    </section>
    <DispatchMapImport :status="status" @saved="mapSaved" />
    <div class="lower-grid">
      <section class="panel">
        <h3>交通资源</h3>
        <el-table :data="resources" size="small"
          ><el-table-column prop="resourceId" label="资源" /><el-table-column
            prop="occupiedCount"
            label="占用"
            width="70"
          /><el-table-column
            prop="capacity"
            label="容量"
            width="70"
          /><el-table-column label="原占用任务"
            ><template #default="{ row }">{{
              row.leases
                ?.map((l: any) => `${l.vehicleId} / ${l.taskId}`)
                .join("，") || "无"
            }}</template></el-table-column
          ></el-table
        >
      </section>
      <section class="panel">
        <div class="panel-heading">
          <h3>当前业务报告</h3>
          <el-button
            :disabled="!status?.report"
            size="small"
            @click="reportDownload"
            >下载原报告</el-button
          >
        </div>
        <p>{{ status?.report?.status || "暂无报告" }}</p>
        <p
          v-for="(value, key) in status?.report?.metrics ?? {}"
          :key="key"
          class="muted"
        >
          {{ key }} · {{ value }}
        </p>
        <p v-for="check in status?.report?.checks ?? []" :key="check.name">
          {{ check.name }} · {{ check.status }}
        </p>
      </section>
    </div>
    <el-dialog v-model="importDialog" title="原运输命令转换预览" width="780px"
      ><p>
        将生成
        {{ importPreview?.tasks.length ?? 0 }}
        条算法运输任务，原任务身份保存在映射记录中。
      </p>
      <el-input
        v-model="importName"
        placeholder="实验任务集名称"
        maxlength="128"
      /><el-table :data="importPreview?.tasks ?? []"
        ><el-table-column prop="id" label="实验 ID" /><el-table-column
          prop="from"
          label="起点" /><el-table-column prop="to" label="终点"
      /></el-table>
      <p
        v-for="reason in importPreview?.skipped ?? []"
        :key="reason"
        class="muted"
      >
        {{ reason }}
      </p>
      <template #footer
        ><el-button
          @click="
            downloadIntegrationRecord(
              importPreview,
              'dispatch-task-mapping.json',
            )
          "
          >下载身份映射</el-button
        ><el-button
          type="primary"
          :disabled="
            busy ||
            !!savedTaskSet ||
            !importPreview?.tasks.length ||
            !importName.trim()
          "
          @click="saveTasks"
          >{{ savedTaskSet ? "已保存任务集" : "确认保存实验任务集" }}</el-button
        ><el-button v-if="savedTaskSet" @click="$router.push('/tasks')"
          >进入任务集</el-button
        ></template
      ></el-dialog
    >
  </div>
</template>
<style scoped>
.dispatch-page {
  display: grid;
  gap: 16px;
}
.source-panel,
.panel-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}
.source-panel h2 {
  margin: 0;
  font-size: 20px;
}
.source-panel p {
  margin-bottom: 0;
}
.dispatch-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 310px;
  gap: 16px;
}
.dispatch-scene {
  height: 560px;
  display: flex;
  flex-direction: column;
}
.dispatch-scene :deep(.scene) {
  flex: 1;
  min-height: 0;
}
.panel-heading h3 {
  margin: 0 0 12px;
}
.vehicles {
  max-height: 560px;
  overflow: auto;
}
.vehicle-row {
  padding: 12px 0;
  border-bottom: 1px solid #ebeef5;
}
.vehicle-row > div {
  display: flex;
  justify-content: space-between;
}
.vehicle-row p {
  font-size: 13px;
  color: #606266;
  margin: 8px 0;
  overflow-wrap: anywhere;
}
.lower-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}
.task-detail {
  margin-top: 18px;
}
.task-detail p {
  overflow-wrap: anywhere;
}
h3 {
  font-size: 16px;
}
.controls {
  padding-bottom: 12px;
}
@media (max-width: 1100px) {
  .dispatch-body,
  .lower-grid {
    grid-template-columns: 1fr;
  }
  .source-panel,
  .panel-heading {
    align-items: flex-start;
    flex-wrap: wrap;
  }
  .dispatch-scene {
    height: 440px;
  }
}
</style>
