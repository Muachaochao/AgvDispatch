<script setup lang="ts">
import {
  computed,
  onMounted,
  onBeforeUnmount,
  ref,
  shallowRef,
  watch,
} from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import BusinessScenarioSetup from "@/components/BusinessScenarioSetup.vue";
import EngineeringSchemeEditor from "@/components/EngineeringSchemeEditor.vue";
import RoadNetScene from "@/components/RoadNetScene.vue";
import DispatchMapImport from "@/components/DispatchMapImport.vue";
import {
  getRoadNet,
  listRoadNets,
  type RoadNetSummary,
  type RoadNetPayload,
} from "@/api/roadnet";
import {
  createTaskSet,
  listTaskSets,
  type TaskSetSummary,
} from "@/api/sim";
import {
  createMainDispatchExperiment,
  getMainDispatchExperiment,
  listMainDispatchExperiments,
  stopMainDispatchExperiment,
  type MainDispatchExperiment,
} from "@/api/dispatch-experiment";
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
import {
  mainDispatchStationId,
  toggleMainDispatchStation,
} from "@/simulation/draft-task-set";

const status = ref<DispatchStatus | null>(null),
  tasks = ref<TaskRow[]>([]),
  resources = ref<ResourceRow[]>([]);
const experimentTaskSets = ref<TaskSetSummary[]>([]);
const selectedExperimentTaskSetId = ref<number | null>(null);
const experimentTaskSource = ref<"taskset" | "map">("taskset");
const selectedCallStations = ref<string[]>([]);
const selectedCallRoadnetId = ref<number | null>(null);
const experimentName = ref("五车20单主工程调度实验");
const experimentTimeout = ref(7200);
const experiments = ref<MainDispatchExperiment[]>([]);
const activeExperiment = ref<MainDispatchExperiment | null>(null);
const experimentBusy = ref(false);
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
  actionRevision = 0,
  taskPollCounter = 0,
  experimentPollCounter = 0;
let detailPolling = false;
let metadataRoot: string | undefined, metadataScenario: string | undefined;
const clock = ref(Date.now());
const clockTimer = setInterval(() => (clock.value = Date.now()), 1000);
const fresh = computed(
  () => connected.value && dispatchIsFresh(status.value, clock.value),
);
const cars = computed(() => status.value?.device?.cars ?? {});
const section = ref('engineering');
const callStatuses: Record<string, string> = {
  pending: '待触发', emitted: '已触发 · 待建单', created: '待派车', active: '执行中', completed: '已完成',
  Faulted: '任务故障', Cancelled: '任务取消',
};
function schemeSaved(value: DispatchStatus) {
  if (status.value) status.value = { ...status.value, schemes: value.schemes };
}
async function openCallTask(taskId: string) {
  await selectTask(taskId);
  document.getElementById('business-task-detail')?.scrollIntoView({ block: 'start' });
}
function blockingVehicles(key?: string) {
  try {
    const rows: unknown = JSON.parse(key || '[]');
    if (Array.isArray(rows)) return [...new Set(rows.filter(Array.isArray).map(row => row[2]).filter(Boolean))].join('、');
  } catch { /* The backend explanation remains available. */ }
  return '';
}
const selectedCar = computed(() => cars.value[detail.value?.task.vehicleId ?? '']);
const mechanism = computed(() => selectedCar.value?.mechanism);
const mechanismStates: Record<string, string> = {
  Idle: '待命', Executing: '执行中', Completed: '完成', Interrupted: '中断',
};
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
  if (selectedCallRoadnetId.value === null) {
    selectedCallRoadnetId.value =
      maps.value.find((item) => item.name.includes("全厂五车"))?.id ??
      maps.value[0]?.id ??
      null;
  }
}
async function loadExperimentOptions() {
  experimentTaskSets.value = await listTaskSets();
  const preferred =
    experimentTaskSets.value.find((item) =>
      item.name.includes("五车20工位"),
    ) ?? experimentTaskSets.value[0];
  selectedExperimentTaskSetId.value = preferred?.id ?? null;
  experiments.value = await listMainDispatchExperiments();
  activeExperiment.value =
    experiments.value.find((item) =>
      ["PREPARING", "SUBMITTED", "RUNNING"].includes(item.status),
    ) ?? null;
}
async function refreshExperiments() {
  if (!activeExperiment.value) return;
  const id = activeExperiment.value.id;
  try {
    const updated = await getMainDispatchExperiment(id);
    const index = experiments.value.findIndex((item) => item.id === id);
    if (index >= 0) experiments.value[index] = updated;
    else experiments.value.unshift(updated);
    activeExperiment.value = updated;
  } catch (e) {
    if (!disposed) error.value = e instanceof Error ? e.message : String(e);
  }
}
async function startMainDispatchExperiment() {
  if (!selectedScheme.value) {
    ElMessage.warning("请选择主工程方案");
    return;
  }
  let taskSet: TaskSetSummary | null = null;
  let roadnetId: number | null = null;
  let taskCount = 0;
  if (experimentTaskSource.value === "map") {
    if (!selectedCallStations.value.length) {
      ElMessage.warning("请先在地图上选择 CNC 工位");
      return;
    }
    if (selectedCallRoadnetId.value === null) {
      ElMessage.error("请选择实验路网");
      return;
    }
    roadnetId = selectedCallRoadnetId.value;
    taskCount = selectedCallStations.value.length;
  } else {
    if (!selectedExperimentTaskSetId.value) {
      ElMessage.warning("请选择实验任务集");
      return;
    }
    taskSet =
      experimentTaskSets.value.find(
        (item) => item.id === selectedExperimentTaskSetId.value,
      ) ?? null;
    if (!taskSet) return;
    if (taskSet.roadnetId === null) {
      ElMessage.error("所选任务集没有绑定路网");
      return;
    }
    roadnetId = taskSet.roadnetId;
    taskCount = taskSet.taskCount;
  }
  try {
    await ElMessageBox.confirm(
      experimentTaskSource.value === "map"
        ? `将为主工程创建 ${taskCount} 个 PLC 工位呼叫。车辆、路线、等待位、倒料位和九步流程由当前方案决定。`
        : `将由主工程正式调度执行「${taskSet!.name}」的 ${taskCount} 条任务。新实验会停止当前主工程场景并创建干净隔离实例。`,
      "启动主工程调度实验",
      {
        confirmButtonText: "启动实验",
        cancelButtonText: "取消",
        type: "warning",
      },
    );
  } catch {
    return;
  }
  experimentBusy.value = true;
  error.value = "";
  try {
    let taskSetId = taskSet?.id;
    if (experimentTaskSource.value === "map") {
      const createdTaskSet = await createTaskSet({
        name: `主工程地图点选-${taskCount}工位-${new Date().toLocaleString(
          "zh-CN",
          { hour12: false },
        )}`,
        roadnetId,
        taskSet: {
          tasks: selectedCallStations.value.map((stationId, index) => ({
            id: index + 1,
            from: stationId,
            to: "DUMPER-001",
          })),
        },
      });
      taskSetId = createdTaskSet.id;
      experimentTaskSets.value = await listTaskSets();
    }
    if (!taskSetId || roadnetId === null) {
      throw new Error("实验任务集或路网尚未准备完成");
    }
    const created = await createMainDispatchExperiment({
      name: experimentName.value.trim(),
      schemeId: selectedScheme.value,
      roadnetId,
      taskSetId,
      scenarioName: "dispatch-backlog",
      timeoutSeconds: experimentTimeout.value,
    });
    experiments.value = [
      created,
      ...experiments.value.filter((item) => item.id !== created.id),
    ];
    activeExperiment.value = created;
    ElMessage.success("主工程调度实验已受理，正在创建隔离实例");
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    experimentBusy.value = false;
  }
}
function onMainMapNodeSelect(node: { id: string; type: string }) {
  if (experimentTaskSource.value !== "map") return;
  const stationId = mainDispatchStationId(node);
  if (!stationId) {
    ElMessage.warning("主工程 PLC 呼叫只能选择 CNC 工位");
    return;
  }
  selectedCallStations.value = toggleMainDispatchStation(
    selectedCallStations.value,
    node.id,
  );
}
async function stopExperiment(item: MainDispatchExperiment) {
  try {
    await ElMessageBox.confirm(
      `停止「${item.name}」后，当前主工程隔离环境也会停止。`,
      "停止主工程实验",
      {
        confirmButtonText: "停止实验",
        cancelButtonText: "取消",
        type: "warning",
      },
    );
  } catch {
    return;
  }
  experimentBusy.value = true;
  try {
    const stopped = await stopMainDispatchExperiment(item.id);
    const index = experiments.value.findIndex((row) => row.id === item.id);
    if (index >= 0) experiments.value[index] = stopped;
    activeExperiment.value = stopped;
    ElMessage.success("主工程调度实验已停止");
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    experimentBusy.value = false;
  }
}
function experimentStatusType(
  status: MainDispatchExperiment["status"],
): "success" | "danger" | "info" | "primary" | "warning" {
  if (status === "FINISHED") return "success";
  if (status === "FAILED") return "danger";
  if (status === "ABORTED") return "info";
  if (status === "RUNNING") return "primary";
  return "warning";
}
async function poll() {
  const revision = actionRevision;
  try {
    const live = await dispatchRead<DispatchStatus>(
      "/api/simulation/live",
      pageAbort.signal,
    );
    if (disposed || revision !== actionRevision) return;
    status.value = { ...status.value!, ...live };
    observedAt.value = Date.now();
    connected.value = true;
    if (!detailPolling) void pollDetails(live, revision);
  } catch (e) {
    if (!disposed) {
      connected.value = false;
      error.value = e instanceof Error ? e.message : String(e);
    }
  } finally {
    if (!disposed) timer = setTimeout(poll, 1000);
  }
}
async function pollDetails(live: DispatchStatus, revision: number) {
  detailPolling = true;
  try {
    const needsMetadata =
      !status.value?.map || live.rootName !== metadataRoot || live.scenario?.status !== metadataScenario;
    const full = needsMetadata
      ? await dispatchRead<DispatchStatus>("/api/simulation", pageAbort.signal)
      : null;
    if (disposed || revision !== actionRevision || status.value?.rootName !== live.rootName) return;
    if (full) { metadataRoot = live.rootName; metadataScenario = live.scenario?.status; }
    const next: DispatchStatus = full
      ? {
          ...full,
          ...live,
          map: full.map,
          configuration: full.configuration,
          schemes: full.schemes,
          report: full.report,
        }
      : { ...status.value!, ...live };
    status.value = { ...next, ...status.value, map: next.map, configuration: next.configuration, schemes: next.schemes, report: next.report, startupPlan: full?.startupPlan ?? status.value?.startupPlan };
    error.value = next.error ?? "";
    const signature = JSON.stringify(next.map ?? null);
    if (signature !== liveMapSignature.value) {
      liveMap.value = next.map ? dispatchSceneMap(next.map) : null;
      liveMapSignature.value = signature;
    }
    const shouldReadDetails = next.running === true && taskPollCounter++ % 3 === 0;
    const results = shouldReadDetails
      ? await Promise.allSettled([
          dispatchRead<{ items: TaskRow[] }>(
            "/api/simulation/tasks?page=1&pageSize=200",
            pageAbort.signal,
          ),
          dispatchRead<ResourceRow[]>(
            "/api/simulation/resources",
            pageAbort.signal,
          ),
        ])
      : [];
    if (disposed || revision !== actionRevision) return;
    if (results[0]?.status === "fulfilled")
      tasks.value = results[0].value.items ?? [];
    if (results[1]?.status === "fulfilled")
      resources.value = results[1].value ?? [];
    const failures = results.filter(
      (r) => r.status === "rejected",
    ) as PromiseRejectedResult[];
    if (failures.length)
      error.value = failures.map((r) => String(r.reason)).join(" · ");
    if (!selectedTask.value && tasks.value.length === 1) selectedTask.value = tasks.value[0]!.taskId;
    if (next.running && selectedTask.value) await selectTask(selectedTask.value, false);
    if (experimentPollCounter++ % 2 === 0) await refreshExperiments();
  } catch (e) {
    if (!disposed) {
      error.value = e instanceof Error ? e.message : String(e);
    }
  } finally {
    detailPolling = false;
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
    if (!identity?.sourceMapId || !identity?.definitionHash)
      throw new Error(
        "目标路网缺少 Web 仿真地图来源记录，请先重新导入 Web 仿真地图",
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
    if (!importPreview.value.tasks.length)
      throw new Error(
        "所选原任务命令与目标 Web 仿真路网不匹配，没有可转换的运输任务",
      );
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
  void loadExperimentOptions().catch((e) => (error.value = String(e)));
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
        <h2>主工程调度</h2>
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
        ><el-button @click="$router.push('/simulation?mode=native')"
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
    <nav class="panel workflow-nav" aria-label="业务仿真流程">
      <el-radio-group v-model="section">
        <el-radio-button value="engineering">工程配置</el-radio-button>
        <el-radio-button value="plan">呼叫计划</el-radio-button>
        <el-radio-button value="monitor">运行监控</el-radio-button>
        <el-radio-button value="report">报告</el-radio-button>
      </el-radio-group>
    </nav>
    <EngineeringSchemeEditor v-if="section === 'engineering'" :status="status" @saved="schemeSaved" />
    <BusinessScenarioSetup v-show="section === 'plan'" :status="status" @started="section = 'monitor'" @changed="status = $event; selectedTask = ''; detail = null; tasks = []; liveMapSignature = '';" />
    <details v-show="section === 'monitor'" class="panel legacy-experiments"><summary>批量任务与历史实验（高级）</summary>
    <section class="panel experiment-launcher">
      <div class="panel-heading">
        <div>
          <h3>运行正式工程仿真</h3>
          <p class="muted">
            PLC 呼叫、车辆调度、机构动作和九步流程由 5199 正式工程执行
          </p>
        </div>
        <el-tag type="success">主工程控制车辆</el-tag>
      </div>
      <el-space wrap>
        <el-radio-group v-model="experimentTaskSource" :disabled="experimentBusy">
          <el-radio-button value="taskset">任务集</el-radio-button>
          <el-radio-button value="map">地图点选 PLC 呼叫</el-radio-button>
        </el-radio-group>
        <el-select
          v-model="selectedScheme"
          style="width: 290px"
          placeholder="选择主工程方案"
          :disabled="experimentBusy"
        >
          <el-option
            v-for="scheme in status?.schemes ?? []"
            :key="scheme.id"
            :label="`${scheme.name} · ${scheme.vehicleCount ?? '?'}辆`"
            :value="scheme.id"
          />
        </el-select>
        <el-select
          v-if="experimentTaskSource === 'taskset'"
          v-model="selectedExperimentTaskSetId"
          style="width: 250px"
          placeholder="选择实验任务集"
          :disabled="experimentBusy"
        >
          <el-option
            v-for="item in experimentTaskSets"
            :key="item.id"
            :label="`${item.name} · ${item.taskCount}单`"
            :value="item.id"
          />
        </el-select>
        <el-select
          v-else
          v-model="selectedCallRoadnetId"
          style="width: 190px"
          placeholder="选择实验路网"
          :disabled="experimentBusy"
        >
          <el-option
            v-for="item in maps"
            :key="item.id"
            :label="item.name"
            :value="item.id"
          />
        </el-select>
        <el-tag v-if="experimentTaskSource === 'map'" type="info">
          已选 {{ selectedCallStations.length }} 个 PLC 工位
        </el-tag>
        <el-input
          v-model="experimentName"
          maxlength="128"
          style="width: 250px"
          placeholder="实验名称"
          :disabled="experimentBusy"
        />
        <el-input-number
          v-model="experimentTimeout"
          :min="30"
          :max="604800"
          :step="300"
          style="width: 150px"
          aria-label="实验超时秒数"
          :disabled="experimentBusy"
        />
        <span class="muted">超时秒</span>
        <el-button
          type="primary"
          :disabled="
            experimentBusy ||
            !selectedScheme ||
            (experimentTaskSource === 'taskset'
              ? !selectedExperimentTaskSetId
              : !selectedCallStations.length || !selectedCallRoadnetId) ||
            !experimentName.trim()
          "
          @click="startMainDispatchExperiment"
        >
          {{
            experimentTaskSource === "map"
              ? "按选中工位启动主工程实验"
              : "按任务集启动主工程实验"
          }}
        </el-button>
        <el-button
          v-if="experimentTaskSource === 'map' && selectedCallStations.length"
          :disabled="experimentBusy"
          @click="selectedCallStations = []"
        >
          清空工位
        </el-button>
      </el-space>
      <el-alert
        v-if="experimentTaskSource === 'taskset' && selectedExperimentTaskSetId"
        class="mapping-tip"
        type="info"
        :closable="false"
        title="当前任务集需使用 BUS-CNC-001 至 BUS-CNC-305 或 CNC-001 至 CNC-305 作为起点，终点统一为 DUMPER-001"
      />
      <el-alert
        v-else-if="experimentTaskSource === 'map'"
        class="mapping-tip"
        type="info"
        :closable="false"
        title="在下方三维地图点击 CNC 工位即可加入或取消 PLC 呼叫。系统自动决定车辆、路线、等待位、倒料位、返程和九步机构动作。"
      />
    </section>
    <section v-if="experiments.length" class="panel experiment-history">
      <div class="panel-heading">
        <h3>主工程实验记录</h3>
        <span v-if="activeExperiment" class="muted">
          当前 {{ activeExperiment.message || activeExperiment.status }}
        </span>
      </div>
      <el-table :data="experiments" size="small" row-key="id">
        <el-table-column prop="name" label="实验" min-width="190" />
        <el-table-column prop="taskCount" label="任务" width="70" />
        <el-table-column label="状态" width="105">
          <template #default="{ row }">
            <el-tag size="small" :type="experimentStatusType(row.status)">
              {{ row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column
          prop="runtimeInstance"
          label="隔离实例"
          min-width="220"
          show-overflow-tooltip
        />
        <el-table-column prop="scenarioId" label="场景编号" min-width="150" />
        <el-table-column prop="message" label="说明" min-width="240" />
        <el-table-column label="操作" width="150" fixed="right">
          <template #default="{ row }">
            <el-button size="small" @click="activeExperiment = row">
              查看
            </el-button>
            <el-button
              v-if="row.status !== 'ABORTED'"
              size="small"
              type="danger"
              link
              :disabled="experimentBusy"
              @click="stopExperiment(row)"
            >
              停止环境
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </section>
    </details>
    <section v-show="section === 'monitor' || section === 'plan'" class="panel controls">
      <div class="panel-heading">
        <h3>高级隔离控制</h3>
        <span class="muted">用于故障演练和底层联调</span>
      </div>
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
    <section v-show="section === 'monitor'" class="panel calls-panel">
      <h3>多单呼叫</h3>
      <el-table :data="status?.scenario?.callRows ?? []" size="small" empty-text="开始业务呼叫计划后显示逐单进度">
        <el-table-column label="序号" width="72"><template #default="{ row }">{{ row.planIndex + 1 }}</template></el-table-column>
        <el-table-column prop="stationId" label="工位" width="110" />
        <el-table-column prop="atSeconds" label="计划秒" width="90" />
        <el-table-column label="触发秒" width="90"><template #default="{ row }">{{ row.actualAtSeconds?.toFixed(1) ?? '—' }}</template></el-table-column>
        <el-table-column label="状态" width="145"><template #default="{ row }">{{ callStatuses[row.taskStage] || callStatuses[row.status] || row.status }}{{ row.stepNumber ? ` · ${row.stepNumber}/9` : '' }}</template></el-table-column>
        <el-table-column prop="vehicleId" label="车辆" width="100" />
        <el-table-column prop="waitingExplanation" label="当前说明" show-overflow-tooltip />
        <el-table-column label="正式任务" width="115"><template #default="{ row }">
          <el-button v-if="row.taskId" link type="primary" :title="row.taskId" @click="openCallTask(row.taskId)">查看九步</el-button>
        </template></el-table-column>
      </el-table>
    </section>
    <div v-show="section === 'monitor'" class="dispatch-body">
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
          :selectable="experimentTaskSource === 'map' && !experimentBusy"
          :selected-node-ids="selectedCallStations"
          @node-select="onMainMapNodeSelect"
          observed
        /><el-empty v-else description="连接主工程后显示原始地图与车辆反馈" />
        <div v-if="experimentTaskSource === 'map'" class="selected-call-stations">
          <el-tag
            v-for="stationId in selectedCallStations"
            :key="stationId"
            closable
            type="success"
            @close="
              selectedCallStations = selectedCallStations.filter(
                (item) => item !== stationId,
              )
            "
          >
            {{ stationId }}
          </el-tag>
        </div>
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
    <section v-show="section === 'monitor'" id="business-task-detail" class="panel tasks-panel">
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
        /><el-table-column prop="completionRequirement" label="完成条件" min-width="250" /></el-table>
        <template v-if="detail.trafficWaiting?.length">
          <h4>当前路权阻塞</h4>
          <p v-for="(wait, index) in detail.trafficWaiting" :key="index">
            {{ wait.reason }} · {{ blockingVehicles(wait.blockingKey) }}
            <span v-if="wait.waitingSinceUtc"> · 等待起始 {{ new Date(wait.waitingSinceUtc.replace(' ', 'T') + 'Z').toLocaleString() }}</span>
          </p>
        </template>
        <h4>导航与机构动作命令</h4>
        <p class="muted">车辆当前载箱：{{ selectedCar?.loaded == null ? '未提供' : selectedCar.loaded ? '有箱' : '空载' }}</p>
        <p v-if="mechanism" class="muted">
          车辆最近机构反馈（合成模型）：
          {{ mechanism.operation === 'JackLoad' ? '顶升取箱' : mechanism.operation === 'JackUnload' ? '落下放箱' : '无动作' }} ·
          {{ mechanismStates[mechanism.state] || '未知' }} ·
          上位 {{ mechanism.upperLimit == null ? '未知' : mechanism.upperLimit ? '1' : '0' }} ·
          下位 {{ mechanism.lowerLimit == null ? '未知' : mechanism.lowerLimit ? '1' : '0' }}
          <span v-if="mechanism.taskId"> · 机构指令 {{ mechanism.taskId }}</span>
        </p>
        <p v-else class="muted">车辆最近机构反馈：未提供</p>
        <el-alert v-if="!fresh" title="当前设备数据已过期，以下记录不能作为实时完成证据" type="warning" :closable="false" />
        <h4>PLC 执行条件 · {{ detail.handshakeContractVersion || '未提供契约版本' }}</h4>
        <el-table :data="detail.plcConditions ?? []" size="small" empty-text="当前步骤没有返回 PLC 条件">
          <el-table-column prop="stationId" label="工位" />
          <el-table-column prop="variableName" label="信号" min-width="180" />
          <el-table-column prop="address" label="地址" />
          <el-table-column label="期望"><template #default="{ row }">{{ row.expectedValue ? '1' : '0' }}</template></el-table-column>
          <el-table-column label="反馈"><template #default="{ row }">{{ row.currentValue == null ? '未知' : row.currentValue ? '1' : '0' }}</template></el-table-column>
          <el-table-column prop="quality" label="质量" />
          <el-table-column prop="reason" label="判定原因" min-width="200" />
        </el-table>
        <p v-for="handshake in detail.handshakes ?? []" :key="handshake.stepId" class="muted">{{ handshake.phase }} · {{ handshake.reason }}</p>
        <p
          v-for="command in detail.commands ?? []"
          :key="command.commandId"
          class="muted"
        >
          {{ command.route?.action || "导航" }} · {{ command.commandId }} · {{ command.status }} ·
          {{ command.route?.nodeIds?.join(" → ") }}
        </p>
      </div>
    </section>
    <DispatchMapImport v-show="section === 'engineering'" @saved="mapSaved" />
    <div v-show="section === 'monitor' || section === 'report'" class="lower-grid" :class="{ 'report-grid': section === 'report' }">
      <section v-show="section === 'monitor'" class="panel">
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
      <section v-show="section === 'report'" class="panel">
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
.dispatch-page > * {
  order: 10;
}
.dispatch-page > .source-panel {
  order: 0;
}
.dispatch-page > .el-alert {
  order: 1;
}
.dispatch-page > .experiment-launcher {
  order: 2;
}
.dispatch-page > .dispatch-body {
  order: 3;
}
.dispatch-page > .business-setup { order: 2; }
.dispatch-page > .workflow-nav { order: 1; }
.dispatch-page > .calls-panel { order: 2; }
.dispatch-page > .tasks-panel { order: 4; }
.dispatch-page > .legacy-experiments { order: 12; }
.dispatch-page > .experiment-history {
  order: 4;
}
.dispatch-page > .controls {
  order: 5;
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
.lower-grid.report-grid {
  grid-template-columns: minmax(0, 1fr);
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
.experiment-launcher {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.experiment-launcher .panel-heading,
.experiment-history .panel-heading {
  margin-bottom: 4px;
}
.experiment-launcher h3,
.experiment-history h3,
.controls h3 {
  margin: 0 0 4px;
}
.experiment-launcher p {
  margin: 0;
}
.mapping-tip {
  margin-top: 2px;
}
.experiment-history :deep(.el-table) {
  margin-top: 10px;
}
.selected-call-stations {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  padding: 10px 12px 0;
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
