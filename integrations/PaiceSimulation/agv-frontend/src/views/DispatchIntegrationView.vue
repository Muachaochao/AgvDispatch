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
import { dispatchLocation, type DispatchSection } from "@/integration/dispatch-workspace";
import BusinessScenarioSetup from "@/components/BusinessScenarioSetup.vue";
import EngineeringSchemeEditor from "@/components/EngineeringSchemeEditor.vue";
import SimulationWorkshopMap from "@/components/SimulationWorkshopMap.vue";
import DispatchMapImport from "@/components/DispatchMapImport.vue";
import DispatchReplay from "@/components/DispatchReplay.vue";
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
  dispatchWaitBlockers,
  dispatchWaitDuration,
  dispatchWaitSnapshotFresh,
  dispatchWaitState,
  type DispatchTaskDetail,
  type DispatchWaitRequest,
  type SourceMap,
} from "@/integration/dispatch-adapter";
import {
  dispatchRead,
  downloadIntegrationRecord,
  type DispatchMonitor,
  type DispatchStatus,
  type DispatchReport,
  type DispatchTrafficEvent,
  type CallRow,
  type TaskRow,
  type ResourceRow,
} from "@/integration/dispatch-api";
import {
  briefReason, businessStepLabel, businessStepNames as stepNames, callPresentation,
  elapsedLabel, resourceLocations, resourceRows, routeTarget, secondsSince,
  summarizeWait, trafficWaitRows, vehicleTaskKind,
} from "@/integration/simulation-monitor";
import {
  mainDispatchStationId,
  toggleMainDispatchStation,
} from "@/simulation/draft-task-set";

const status = shallowRef<DispatchStatus | null>(null),
  tasks = ref<TaskRow[]>([]),
  resources = shallowRef<ResourceRow[]>([]);
const monitor = shallowRef<DispatchMonitor | null>(null);
const resourcePageSize = 40;
const resourcePage = ref(1);
const resourceQuery = ref("");
const showAllResources = ref(false);
const trafficView = ref<'requests' | 'resources'>('requests');
const selectedResourceId = ref("");
const resourceEvents = shallowRef<DispatchTrafficEvent[]>([]);
const resourceEvidenceStatus = ref("");
const resourceIndex = computed(() => resourceLocations(status.value?.map, resources.value));
const filteredResources = computed(() => resourceRows(
  resources.value, waitFresh.value ? monitor.value?.traffic?.requests ?? [] : [],
  resourceIndex.value, resourceQuery.value, showAllResources.value,
));
const resourcePageCount = computed(() =>
  Math.max(1, Math.ceil(filteredResources.value.length / resourcePageSize)),
);
const visibleResources = computed(() => {
  const offset = (resourcePage.value - 1) * resourcePageSize;
  return filteredResources.value.slice(offset, offset + resourcePageSize);
});
watch([resourceQuery, showAllResources], () => { resourcePage.value = 1; });
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
const selectedCommandId = ref("");
const selectedVehicleId = ref("");
const selectedCallIndex = ref<number | null>(null);
const mapLocation = shallowRef<{ vehicleId?: string; nodeId?: string; edgeIds?: string[]; point?: { x: number; y: number }; label?: string } | null>(null);
const historicalReport = shallowRef<DispatchReport | null>(null);
const historicalReportId = ref("");
const reportError = ref("");
const reportBusy = ref(false);
const displayedReport = computed(() =>
  historicalReportId.value ? historicalReport.value : status.value?.report,
);
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
  liveMapSignature = ref("");
const pageAbort = new AbortController();
let timer: ReturnType<typeof setTimeout> | undefined,
  disposed = false,
  actionRevision = 0,
  taskPollCounter = 0,
  experimentPollCounter = 0;
let detailPolling = false;
let taskRequest = 0;
let metadataRoot: string | undefined, metadataScenario: string | undefined;
let lastRenderSource: SourceMap | null = null;
const clock = ref(Date.now());
const clockTimer = setInterval(() => (clock.value = Date.now()), 1000);
const fresh = computed(
  () => connected.value && dispatchIsFresh(status.value, clock.value),
);
const cars = computed(() => status.value?.device?.cars ?? {});
const section = defineModel<DispatchSection>({ required: true });
const props = defineProps<{ taskId?: string }>();
const waitFresh = computed(() => connected.value && dispatchWaitSnapshotFresh(monitor.value, clock.value));
const waitingApplications = computed(() => trafficWaitRows(
  waitFresh.value ? monitor.value?.traffic?.requests ?? [] : [], clock.value,
));
watch(resourcePageCount, count => { resourcePage.value = Math.min(resourcePage.value, count); });
const waitsByTask = computed(() => {
  const index = new Map<string, DispatchWaitRequest[]>();
  if (!waitFresh.value) return index;
  for (const request of monitor.value?.traffic?.requests ?? []) {
    const requests = index.get(request.taskId) ?? [];
    requests.push(request);
    index.set(request.taskId, requests);
  }
  return index;
});
const calls = computed(() => {
  const rows = status.value?.scenario?.callRows ?? status.value?.report?.callRows ?? [];
  const start = status.value?.scenario?.startedAt ?? status.value?.report?.startedAt;
  const until = scenarioActive.value ? clock.value / 1000 : status.value?.report?.finishedAt ?? status.value?.device?.time;
  const elapsed = start && until ? Math.max(0, until - start) : 0;
  return rows.map(row => {
    const task = tasks.value.find(task => task.taskId === row.taskId);
    const wait = dispatchWaitState(row, waitsByTask.value.get(row.taskId ?? "") ?? []);
    const presentation = callPresentation(row, rows, elapsed);
    const waitingSeconds = row.status === "pending" ? (elapsed >= row.atSeconds ? elapsed - row.atSeconds : undefined)
      : !row.taskId ? (row.actualAtSeconds != null ? elapsed - row.actualAtSeconds : undefined)
      : !row.vehicleId ? secondsSince(row.createdAt ?? task?.createdAt, until ? until * 1000 : undefined)
      : wait.type === "warning" ? secondsSince(row.stepStartedAt, until ? until * 1000 : undefined) : undefined;
    const label = wait.label === "已触发 · 待建单" ? "等 PLC 接收" : wait.label;
    return { ...row, ...presentation, wait, label,
      duration: wait.request ? dispatchWaitDuration(wait.request).replace(/^已等 /, "") : elapsedLabel(waitingSeconds),
      reason: wait.request ? waitSummary(wait.request, !row.vehicleId, true)
        : presentation.reason || (wait.type === "warning" ? "等待本步反馈" : "—"),
    };
  });
});
const selectedCall = computed(() => calls.value.find(row => row.planIndex === selectedCallIndex.value));
const activeCallsByVehicle = computed(() => new Map(
  (status.value?.running ? calls.value : []).filter(row => row.vehicleId && row.status === "active"
    && !["Completed", "Cancelled", "Faulted"].includes(row.taskStage ?? ""))
    .map(row => [row.vehicleId!, row]),
));
function waitSummary(request: DispatchWaitRequest, candidate = false, omitDuration = false) {
  return summarizeWait(request, resourceIndex.value, candidate, omitDuration);
}
function schemeSaved(value: DispatchStatus) {
  if (status.value) status.value = { ...status.value, schemes: value.schemes };
}
async function openCallTask(taskId: string) {
  section.value = "monitor";
  const call = calls.value.find(row => row.taskId === taskId);
  const task = tasks.value.find(row => row.taskId === taskId);
  selectedCallIndex.value = call?.planIndex ?? null;
  selectedVehicleId.value = call?.vehicleId || task?.vehicleId || "";
  locateTask(call?.stationId ?? task?.stationId, selectedVehicleId.value);
  await selectTask(taskId);
  scrollTaskDetail();
}
function scrollTaskDetail() {
  document.getElementById('business-task-detail')?.scrollIntoView({ block: 'start' });
}
function locateTask(stationId?: string, vehicleId?: string) {
  const nodeId = status.value?.configuration?.stations?.find(station => station.stationId === stationId)?.pickupSite
    ?? status.value?.map?.nodes.find(node => node.id === `BUS-${stationId}`)?.id ?? stationId;
  mapLocation.value = vehicleId && cars.value[vehicleId] ? { vehicleId } : nodeId ? { nodeId } : null;
}
function clearTask() {
  taskRequest++;
  selectedTask.value = "";
  detail.value = null;
  detailError.value = "";
  selectedCommandId.value = "";
}
function openCall(row: CallRow) {
  if (row.taskId) return openCallTask(row.taskId);
  clearTask();
  selectedVehicleId.value = "";
  selectedCallIndex.value = row.planIndex;
  locateTask(row.stationId);
  scrollTaskDetail();
}
function pickMapNode(node: { id: string; type: string }) {
  if (experimentTaskSource.value === "map") return onMainMapNodeSelect(node);
  const stationId = status.value?.configuration?.stations?.find(station => station.pickupSite === node.id)?.stationId
    ?? mainDispatchStationId(node);
  const stationCalls = calls.value.filter(row => row.stationId === stationId);
  const call = stationCalls.find(row => row.status === "active") ?? stationCalls.at(-1);
  if (call) void openCall(call);
}
function activeTaskForVehicle(vehicleId: string) {
  if (!status.value?.running) return undefined;
  return activeCallsByVehicle.value.get(vehicleId)?.taskId
    ?? tasks.value.find(row => row.vehicleId === vehicleId && !["Completed", "Cancelled", "Faulted"].includes(row.stage)
      && !calls.value.some(call => call.taskId === row.taskId && call.status === "completed"))?.taskId;
}
function waitForVehicle(vehicleId: string) {
  const call = activeCallsByVehicle.value.get(vehicleId);
  if (call) return call.wait;
  const task = tasks.value.find(row => row.taskId === activeTaskForVehicle(vehicleId));
  return task && dispatchWaitState({ ...task, taskStage: task.stage }, waitsByTask.value.get(task.taskId) ?? []);
}
function inspectVehicle(vehicleId: string) {
  selectedVehicleId.value = vehicleId;
  const call = activeCallsByVehicle.value.get(vehicleId);
  selectedCallIndex.value = call?.planIndex ?? null;
  locateTask(call?.stationId ?? status.value?.configuration?.vehicles?.find(car => car.id === vehicleId)?.initialSite, vehicleId);
  const id = activeTaskForVehicle(vehicleId);
  if (id) void selectTask(id);
  else clearTask();
}
const selectedCar = computed(() => cars.value[detail.value?.task.vehicleId ?? '']);
const historicalTask = computed(() => !!detail.value && (
  detail.value.evidenceSource?.startsWith("saved-report") === true
    || ["Completed", "Cancelled", "Faulted"].includes(detail.value.task.stage ?? "")
));
const currentStep = computed(() => detail.value?.steps?.find(step => step.isCurrent));
const detailWait = computed(() => {
  const task = detail.value?.task;
  if (!task) return undefined;
  return dispatchWaitState({
    ...task,
    taskStage: task.stage,
    stepId: currentStep.value?.stepId,
    stepNumber: currentStep.value?.sequence,
  }, waitsByTask.value.get(task.taskId) ?? []);
});
const detailBlockers = computed(() => dispatchWaitBlockers(detailWait.value?.request?.blockers));
const highlightedResources = computed(() => Object.fromEntries(
  (resourceIndex.value.get(selectedResourceId.value)?.edgeIds ?? []).map(id => [id, "#be5b20"]),
));
const highlightedBoundary = computed(() => {
  const resource = resourceIndex.value.get(selectedResourceId.value);
  return resource?.edgeIds.length ? undefined : resource?.boundary;
});
const selectedResource = computed(() => resources.value.find(row => row.resourceId === selectedResourceId.value));
const selectedResourceWaits = computed(() => waitFresh.value ? (monitor.value?.traffic?.requests ?? []).filter(request =>
  request.resourceIds?.includes(selectedResourceId.value)
  || request.blockers?.some(blocker => blocker.resourceId === selectedResourceId.value)) : []);
function waitVehicleLabel(request: DispatchWaitRequest) {
  const assigned = calls.value.find(row => row.taskId === request.taskId)?.vehicleId
    ?? tasks.value.find(row => row.taskId === request.taskId)?.vehicleId;
  return request.vehicleId ? `${assigned === request.vehicleId ? "" : "候选 "}${request.vehicleId}` : "待匹配";
}
function displayTime(value?: string | null) {
  const time = Date.parse(value ?? "");
  return Number.isFinite(time) ? new Date(time).toLocaleString("zh-CN", { hour12: false }) : "—";
}
async function inspectResource(id: string, focus = false) {
  selectedResourceId.value = id;
  resourceEvents.value = [];
  resourceEvidenceStatus.value = "正在读取释放记录";
  const location = resourceIndex.value.get(id);
  if (focus && location) {
    mapLocation.value = { ...location };
    document.getElementById('fleet-map')?.scrollIntoView({ block: 'start' });
  }
  const instance = status.value?.rootName;
  try {
    const snapshot = await dispatchRead<{ traffic?: { events?: DispatchTrafficEvent[] } }>(
      "/api/simulation/snapshot", pageAbort.signal,
    );
    if (selectedResourceId.value !== id || instance !== status.value?.rootName) return;
    resourceEvents.value = (snapshot.traffic?.events ?? []).filter(event =>
      event.resourceIds?.includes(id) && ["SegmentReleased", "ResourceReleased", "Released", "ResourceAcquired"].includes(event.kind));
    resourceEvidenceStatus.value = resourceEvents.value.length ? "" : "近期快照无此资源的释放或占用记录";
  } catch {
    if (selectedResourceId.value === id && instance === status.value?.rootName) {
      resourceEvidenceStatus.value = "暂时无法读取释放记录";
    }
  }
}
async function copyId(id: string) {
  try {
    await navigator.clipboard.writeText(id);
    ElMessage.success("已复制编号");
  } catch {
    ElMessage.warning("请选中完整编号手动复制");
  }
}
const routeCommands = computed(() =>
  detail.value?.commands?.filter(command => command.route?.edgeIds?.length) ?? [],
);
const currentCommand = computed(() => {
  if (historicalTask.value || !fresh.value || !currentStep.value || !selectedCar.value) return undefined;
  const vendorId = selectedCar.value.current_task;
  const stepId = currentStep.value.stepId;
  const commands = routeCommands.value.filter(command => stepId && command.stepId === stepId);
  if (vendorId) return commands.find(command => command.vendorCommandId === vendorId);
  return commands.reverse().find(command => ["Pending", "Sending", "RetryScheduled"].includes(command.status ?? ""));
});
const shownCommand = computed(() => selectedCommandId.value
  ? routeCommands.value.find(command => command.commandId === selectedCommandId.value)
  : currentCommand.value);
const routeKey = computed(() => JSON.stringify([
  shownCommand.value?.route?.nodeIds ?? [],
  shownCommand.value?.route?.edgeIds ?? [],
  shownCommand.value?.route?.mapVersionId,
]));
const routePoints = computed(() => {
  const [nodes, edges, version] = JSON.parse(routeKey.value) as [string[], string[], string?];
  const currentVersion = status.value?.configuration?.mapIdentity?.mapVersionId;
  if (!liveMap.value || (version && currentVersion && version !== currentVersion)) return [];
  return dispatchRoutePoints(liveMap.value, nodes, edges);
});
const historicalRoute = computed(() => !!shownCommand.value && (
  historicalTask.value || shownCommand.value.commandId !== currentCommand.value?.commandId
));
const stepStates: Record<string, string> = {
  Pending: "未开始", Ready: "待执行", CommandPending: "待发命令",
  Sending: "发送中", Sent: "已发送", Executing: "执行中", Completed: "已完成",
  RetryScheduled: "待重试", Failed: "失败", Cancelled: "已取消", ManualIntervention: "待人工处理",
};
function commandLabel(command: (typeof routeCommands.value)[number]) {
  if (!command.route?.edgeIds?.length) return "机构／设备命令";
  const step = detail.value?.steps?.find(item => item.stepId === command.stepId);
  const nodes = command.route?.nodeIds ?? [];
  const endpoints = nodes.length ? `${nodes[0]} → ${nodes[nodes.length - 1]} · ` : "";
  const position = routeCommands.value.findIndex(item => item.commandId === command.commandId);
  return `${step ? `第 ${step.sequence} 步 · ` : ""}${endpoints}命令 ${position + 1}`;
}
function showCommand(commandId: string) {
  selectedCommandId.value = commandId;
  const command = routeCommands.value.find(item => item.commandId === commandId);
  if (command?.route?.nodeIds?.[0] && routePoints.value.length > 1) mapLocation.value = { nodeId: command.route.nodeIds[0] };
  document.getElementById('fleet-map')?.scrollIntoView({ block: 'start' });
}
function showStepRoute(stepId: string) {
  const command = currentCommand.value?.stepId === stepId ? currentCommand.value
    : routeCommands.value.filter(item => item.stepId === stepId).at(-1);
  if (command) showCommand(command.commandId);
}
const mechanism = computed(() => fresh.value && !historicalTask.value ? selectedCar.value?.mechanism : undefined);
const mechanismStates: Record<string, string> = {
  Idle: '待命', Executing: '执行中', Completed: '完成', Interrupted: '中断',
};
const vehicles = computed(() => {
  const views = new Map(dispatchViews(cars.value, fresh.value).map(car => [car.id, car]));
  const ids = [...new Set([...(status.value?.configuration?.vehicles ?? []).map(car => car.id), ...views.keys()])];
  return ids.sort((a, b) => a.localeCompare(b, "zh-CN", { numeric: true })).map(id => {
    const observed = views.get(id), car = cars.value[id], call = activeCallsByVehicle.value.get(id);
    const step = activeTaskForVehicle(id) === detail.value?.task.taskId && !historicalTask.value ? currentStep.value : undefined;
    const stepNumber = step?.sequence ?? call?.stepNumber;
    const label = businessStepLabel({ stepNumber, stepType: step?.stepType ?? call?.stepType, stepName: call?.stepName });
    const since = secondsSince(step?.startedAt ?? call?.stepStartedAt, fresh.value ? clock.value : (status.value?.device?.time ?? 0) * 1000);
    return { id, car, status: observed?.status ?? "UNKNOWN", coordinates: observed,
      wait: fresh.value ? waitForVehicle(id) : undefined,
      kind: vehicleTaskKind(car, !!activeTaskForVehicle(id)),
      step: label ? `${stepNumber}/9 ${label}` : "",
      target: fresh.value && car && !car.offline
        ? (car.recovery?.state !== "Recovered" ? car.recovery?.safetyPointTarget : undefined)
          || (detail.value?.task.vehicleId === id ? routeTarget(currentCommand.value?.route) : undefined)
          || step?.destinationSite || call?.destinationSite : undefined,
      duration: elapsedLabel(car?.recovery?.durationSeconds ?? since),
    };
  });
});
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
  UNKNOWN: "尚无反馈",
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
      monitor.value = null;
      error.value = e instanceof Error ? e.message : String(e);
    }
  } finally {
    if (!disposed) timer = setTimeout(poll, 1000);
  }
}
async function pollDetails(live: DispatchStatus, revision: number) {
  detailPolling = true;
  try {
    const scenarioKey = `${live.scenario?.id ?? ""}:${live.scenario?.status ?? ""}`;
    const sameInstance = () => !disposed && connected.value && revision === actionRevision
      && status.value?.rootName === live.rootName && status.value?.scenario?.id === live.scenario?.id;
    const needsMetadata =
      !status.value?.map || live.rootName !== metadataRoot || scenarioKey !== metadataScenario;
    const full = needsMetadata
      ? await dispatchRead<DispatchStatus>("/api/simulation", pageAbort.signal)
      : null;
    if (!sameInstance()) return;
    if (full) { metadataRoot = live.rootName; metadataScenario = scenarioKey; }
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
    status.value = { ...next, ...status.value, map: next.map, configuration: next.configuration, schemes: next.schemes, history: next.history, report: next.report, startupPlan: full?.startupPlan ?? status.value?.startupPlan };
    error.value = next.error ?? "";
    if (section.value === 'monitor') {
      const map = next.map ?? null;
      if (map !== lastRenderSource) {
        lastRenderSource = map;
        const signature = JSON.stringify(map);
        if (signature !== liveMapSignature.value) {
          liveMap.value = map ? dispatchSceneMap(map) : null;
          liveMapSignature.value = signature;
        }
      }
    }
    if (section.value !== 'monitor') return;
    const shouldReadDetails = taskPollCounter++ % 3 === 0 && (next.running === true || tasks.value.length === 0);
    const results = shouldReadDetails
      ? await Promise.allSettled([
          dispatchRead<{ items: TaskRow[] }>(
            "/api/simulation/tasks?page=1&pageSize=200",
            pageAbort.signal,
          ),
          next.running ? dispatchRead<DispatchMonitor>(
            "/api/simulation/monitor",
            pageAbort.signal,
          ) : Promise.resolve(null),
        ])
      : [];
    if (!sameInstance()) return;
    if (results[0]?.status === "fulfilled")
      tasks.value = results[0].value.items ?? [];
    if (results[1]?.status === "fulfilled") {
      const snapshot = results[1].value;
      monitor.value = snapshot?.connected ? snapshot : null;
      resources.value = snapshot?.connected ? snapshot.resources ?? [] : [];
    } else if (results[1]?.status === "rejected" || !next.running) {
      monitor.value = null;
      resources.value = [];
    }
    const failures = results.filter(
      (r) => r.status === "rejected",
    ) as PromiseRejectedResult[];
    if (failures.length)
      error.value = failures.map((r) => String(r.reason)).join(" · ");
    const automaticTask = props.taskId ?? (next.running ? status.value?.scenario?.callRows?.find(row => row.status === "active" && row.vehicleId)?.taskId : undefined)
      ?? tasks.value.find(task => task.vehicleId && !["Completed", "Cancelled", "Faulted"].includes(task.stage))?.taskId
      ?? (tasks.value.length === 1 ? tasks.value[0]?.taskId : undefined);
    if (!selectedTask.value && selectedCall.value?.taskId) await selectTask(selectedCall.value.taskId);
    else if (selectedVehicleId.value && selectedCallIndex.value == null && !selectedTask.value) {
      const newTask = activeTaskForVehicle(selectedVehicleId.value);
      if (newTask) await selectTask(newTask);
    } else if (!selectedTask.value && selectedCallIndex.value == null && automaticTask) await selectTask(automaticTask);
    else if (selectedTask.value && shouldReadDetails) await selectTask(selectedTask.value, false);
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
  const request = ++taskRequest;
  selectedTask.value = id;
  if (clear) {
    detail.value = null;
    detailError.value = "";
    selectedCommandId.value = "";
  }
  try {
    const next = await dispatchRead<DispatchTaskDetail>(
      `/api/simulation/tasks/${encodeURIComponent(id)}`,
      pageAbort.signal,
    );
    if (disposed || selectedTask.value !== id || request !== taskRequest) return;
    detail.value = next;
    if (clear) {
      if (historicalTask.value) selectedCommandId.value = routeCommands.value.at(-1)?.commandId ?? "";
    }
    selectedVehicleId.value = next.task.vehicleId || selectedVehicleId.value;
    detailError.value = "";
  } catch (e) {
    if (!disposed && request === taskRequest && selectedTask.value === id)
      detailError.value = e instanceof Error ? e.message : String(e);
  }
}
async function action(name: string, body: Record<string, unknown> = {}) {
  if (!actionsEnabled.value) return;
  busy.value = true;
  error.value = "";
  actionRevision++;
  monitor.value = null;
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
    displayedReport.value,
    `dispatch-simulation-report-${displayedReport.value?.id ?? "current"}.json`,
  );
}
async function selectReport(id: string) {
  historicalReportId.value = id;
  historicalReport.value = null;
  reportError.value = "";
  reportBusy.value = !!id;
  if (!id) return;
  try {
    const result = await dispatchRead<DispatchReport>(`/api/simulation/report?id=${encodeURIComponent(id)}`, pageAbort.signal);
    if (!disposed && historicalReportId.value === id) historicalReport.value = result;
  } catch (e) {
    if (!disposed && historicalReportId.value === id) reportError.value = e instanceof Error ? e.message : String(e);
  } finally {
    if (historicalReportId.value === id) reportBusy.value = false;
  }
}
watch(
  [() => status.value?.rootName, () => status.value?.scenario?.id],
  () => {
    clearTask();
    selectedVehicleId.value = "";
    selectedCallIndex.value = null;
    selectedResourceId.value = "";
    mapLocation.value = null;
    selectedTasks.value = [];
    tasks.value = [];
    resources.value = [];
    monitor.value = null;
    taskPollCounter = 0;
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
        <strong>隔离仿真实例</strong>
        <p class="muted">
          {{ status?.rootName || "尚未连接" }}
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
    <nav class="workspace-actions workflow-nav" aria-label="相关仿真工具">
      <RouterLink v-if="section === 'engineering'" :to="dispatchLocation('environment')">环境工具、车辆与设备模型</RouterLink>
      <RouterLink v-if="section === 'engineering'" :to="dispatchLocation('plan')">配置呼叫并检查启动</RouterLink>
      <RouterLink v-if="section === 'plan'" :to="dispatchLocation('scenarios')">全部场景</RouterLink>
      <RouterLink v-if="section === 'monitor'" :to="dispatchLocation('task-evidence', selectedTask)">任务证据与路权交接</RouterLink>
      <RouterLink v-if="section === 'monitor'" :to="dispatchLocation('diagnosis')">六层调度诊断</RouterLink>
      <RouterLink v-if="section === 'report'" :to="dispatchLocation('report-evidence')">历史对比与完整证据包</RouterLink>
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
    <section v-if="section === 'monitor'" class="panel calls-panel">
      <div class="panel-heading">
        <h3>呼叫进度</h3>
        <span v-if="calls.length" class="muted">{{ !scenarioActive ? '本轮记录 · ' : '' }}已完成 {{ calls.filter(row => row.wait.type === 'success').length }}/{{ calls.length }} 单</span>
      </div>
      <p v-if="status?.running && !waitFresh" class="muted">路权快照暂不可用，等待原因可能不完整。</p>
      <p v-else-if="waitFresh && monitor?.traffic?.requestsTruncated" class="muted">后台快照仅包含部分路权请求。</p>
      <el-table :data="calls" row-key="planIndex" size="small" max-height="460" empty-text="开始业务呼叫计划后显示逐单进度">
        <el-table-column label="工位" width="142"><template #default="{ row }">
          <el-button link type="primary" @click="openCall(row)">{{ row.stationId }} <span class="muted call-number">#{{ row.planIndex + 1 }}</span></el-button>
        </template></el-table-column>
        <el-table-column label="状态" width="155"><template #default="{ row }"><el-tag :type="row.wait.type" size="small">{{ row.label }}</el-tag></template></el-table-column>
        <el-table-column label="执行车辆" width="105"><template #default="{ row }">{{ row.vehicleId || '—' }}</template></el-table-column>
        <el-table-column prop="step" label="当前步骤" min-width="185" />
        <el-table-column prop="duration" label="已等待" width="125" />
        <el-table-column prop="reason" label="等待原因" min-width="225" show-overflow-tooltip />
        <el-table-column label="详情" width="82"><template #default="{ row }"><el-button link type="primary" @click="openCall(row)">查看</el-button></template></el-table-column>
      </el-table>
    </section>
    <div v-if="section === 'monitor'" class="dispatch-body">
      <section id="fleet-map" class="panel dispatch-scene">
        <SimulationWorkshopMap :definition="status?.map" :cars="cars" :fresh="fresh"
          :fleet-count="vehicles.length" :station-count="status?.configuration?.stations?.length"
          :vehicle-profiles="status?.configuration?.vehicles" :selected-vehicle="selectedVehicleId"
          :road-highlights="highlightedResources" :resource-boundary="highlightedBoundary" :route="routePoints" :route-color="historicalRoute ? '#8154ac' : '#14796f'"
          :location="mapLocation" :selected-node-ids="selectedCallStations" select-nodes
          @select-node="pickMapNode" @select-vehicle="inspectVehicle" />
        <div v-if="selectedVehicleId || routeCommands.length" class="route-toolbar">
          <el-tag v-if="shownCommand" :type="historicalRoute ? 'info' : 'success'" size="small">{{ historicalRoute ? '历史路线' : '当前导航' }}</el-tag>
          <span v-else class="muted">{{ selectedVehicleId || '选中任务' }} · 暂无已确认的当前路线</span>
          <el-select v-if="routeCommands.length" :model-value="selectedCommandId" size="small" aria-label="导航命令路线" @change="showCommand">
            <el-option label="跟随当前导航" value="" />
            <el-option v-for="command in routeCommands" :key="command.commandId" :value="command.commandId" :label="commandLabel(command)" />
          </el-select>
          <span v-if="shownCommand && routePoints.length < 2" class="muted">路线与当前地图无法匹配</span>
          <el-button v-if="selectedTask" link type="primary" size="small" @click="scrollTaskDetail">查看九步</el-button>
        </div>
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
        <div class="panel-heading"><h3>车队状态</h3><span class="muted">{{ vehicles.length }} 台</span></div>
        <el-empty v-if="!vehicles.length" description="暂无车辆配置或反馈" />
        <article v-for="vehicle in vehicles" :key="vehicle.id" class="vehicle-row" :class="{ 'vehicle-selected': selectedVehicleId === vehicle.id }">
          <div class="vehicle-title">
            <el-button link type="primary" :aria-pressed="selectedVehicleId === vehicle.id" @click="inspectVehicle(vehicle.id)"><strong>{{ vehicle.id }}</strong></el-button>
            <span class="vehicle-battery" :class="{ 'low-battery': vehicle.car?.batteryPercent != null && vehicle.car.batteryPercent < 25 }">{{ vehicle.car?.batteryPercent != null ? `${vehicle.car.batteryPercent.toFixed(0)}%` : '电量未知' }}</span>
            <el-tag v-if="vehicle.wait && vehicle.wait.type !== 'primary' && vehicle.status !== 'OFFLINE'" :type="vehicle.wait.type" size="small">{{ vehicle.wait.label }}</el-tag>
            <el-tag v-else size="small" :type="vehicle.status === 'MOVING' || vehicle.status === 'CHARGING' ? 'success' : vehicle.status === 'OFFLINE' || vehicle.status === 'UNKNOWN' ? 'info' : 'warning'">{{ stateName[vehicle.status] || vehicle.status }}</el-tag>
          </div>
          <dl class="vehicle-facts">
            <div><dt>位置</dt><dd>{{ vehicle.car?.station || (vehicle.coordinates ? '站间' : '—') }} · {{ vehicle.car?.loaded == null ? '载荷未知' : vehicle.car.loaded ? '载箱' : '空载' }}</dd></div>
            <div><dt>任务</dt><dd>{{ vehicle.kind }}<template v-if="vehicle.step"> · {{ vehicle.step }}</template></dd></div>
            <div><dt>下一目标</dt><dd>{{ vehicle.target || '—' }}</dd></div>
            <div v-if="vehicle.duration !== '—'"><dt>已持续</dt><dd>{{ vehicle.duration }}</dd></div>
          </dl>
          <p v-if="vehicle.status === 'OFFLINE' && status?.device?.time" class="muted">最后反馈 {{ new Date(status.device.time * 1000).toLocaleString('zh-CN', { hour12: false }) }}</p>
          <p v-if="vehicle.wait?.request" class="wait-summary">{{ waitSummary(vehicle.wait.request) }}</p>
          <p v-else-if="vehicle.car?.waitReason && vehicle.status !== 'OFFLINE'">{{ briefReason(vehicle.car.waitReason) }}</p>
          <p v-if="vehicle.car?.recovery?.message && vehicle.status !== 'OFFLINE'">{{ vehicle.car.recovery.message }}</p>
        </article>
      </section>
    </div>
    <section v-if="section === 'monitor'" id="business-task-detail" class="panel tasks-panel">
      <div class="panel-heading">
        <h3>九步任务</h3>
        <el-space v-if="detail" wrap>
          <el-tag v-if="historicalTask" type="info">历史任务</el-tag>
          <el-tag v-if="detailWait" :type="detailWait.type">{{ detailWait.label }}</el-tag>
          <el-tag>{{ detail.task.stationId }} · {{ detail.task.vehicleId || '待派车' }}</el-tag>
        </el-space>
      </div>
      <div v-if="selectedCall" class="call-detail">
        <strong>{{ selectedCall.stationId }} · 第 {{ selectedCall.planIndex + 1 }} 次呼叫</strong>
        <span>计划 {{ selectedCall.atSeconds }} 秒 · 实际 {{ selectedCall.actualAtSeconds == null ? '尚未触发' : `${selectedCall.actualAtSeconds.toFixed(1)} 秒` }}</span>
        <span>{{ selectedCall.label }}<template v-if="selectedCall.reason !== '—'"> · {{ selectedCall.reason }}</template></span>
        <el-button v-if="selectedCall.previous" link type="primary" @click="openCall(selectedCall.previous)">查看该工位上一单</el-button>
      </div>
      <el-alert
        v-if="detailError"
        :title="detailError"
        type="warning"
        :closable="false"
      />
      <div v-if="detail" class="task-detail">
        <p v-if="detail.evidenceMessage" class="muted">{{ detail.evidenceMessage }}</p>
        <el-table :data="detail.steps ?? []" size="small" :row-class-name="({ row }: { row: { isCurrent?: boolean } }) => row.isCurrent ? 'current-business-step' : ''"
          ><el-table-column
            prop="sequence"
            label="步骤"
            width="70" /><el-table-column
            label="类型"
            min-width="150"><template #default="{ row }">{{ stepNames[row.stepType] || row.stepType }}</template></el-table-column><el-table-column
            label="状态"
            width="112"><template #default="{ row }">
              <el-tag v-if="row.isCurrent" size="small">{{ stepStates[row.status] || row.status }}</el-tag>
              <span v-else>{{ stepStates[row.status] || row.status }}</span>
            </template></el-table-column>
          <el-table-column label="起止点" min-width="155"><template #default="{ row }">
            {{ [row.sourceSite, row.destinationSite].filter(Boolean).join(' → ') || '—' }}
          </template></el-table-column>
          <el-table-column prop="elapsedText" label="耗时" width="95" />
          <el-table-column label="到位／动作反馈" min-width="190"><template #default="{ row }">
            {{ briefReason(row.observedPhase) || briefReason(row.waitingExplanation) || '—' }}
            <div v-if="row.completedAt" class="muted">{{ displayTime(row.completedAt) }}</div>
          </template></el-table-column>
          <el-table-column label="证据" width="95"><template #default="{ row }">
            <el-button v-if="routeCommands.some(command => command.stepId === row.stepId)" link type="primary"
              @click="showStepRoute(row.stepId)">看路线</el-button>
          </template></el-table-column>
        </el-table>
        <div v-if="detailWait?.request" class="wait-summary">
          <h4>{{ !detail.task.vehicleId ? '候选路线等待' : detailWait.request.waitingCategory === 'Equipment' ? '设备资源等待' : '当前路权等待' }}</h4>
          <p>{{ detailWait.request.vehicleId }} · {{ dispatchWaitDuration(detailWait.request) }}
            <span v-if="detailWait.request.source && detailWait.request.destination">
              · {{ detailWait.request.source }} → {{ detailWait.request.destination }}
            </span>
          </p>
          <ul v-if="detailBlockers.length">
            <li v-for="blocker in detailBlockers" :key="blocker.resourceId">
              <el-button link type="primary" @click="inspectResource(blocker.resourceId, true)">{{ resourceIndex.get(blocker.resourceId)?.label || '阻塞资源' }}</el-button>
              · {{ blocker.vehicles.length ? `${blocker.vehicles.join('、')} 占用` : '持有车辆未知' }}
            </li>
          </ul>
        </div>
        <h4 v-if="!historicalTask">机构反馈</h4>
        <p v-if="mechanism" class="muted">
          隔离模型最近反馈 ·
          {{ mechanism.operation === 'JackLoad' ? '顶升取箱' : mechanism.operation === 'JackUnload' ? '落下放箱' : '无动作' }} ·
          {{ mechanismStates[mechanism.state] || '未知' }} ·
          上位 {{ mechanism.upperLimit == null ? '未知' : mechanism.upperLimit ? '1' : '0' }} ·
          下位 {{ mechanism.lowerLimit == null ? '未知' : mechanism.lowerLimit ? '1' : '0' }}
        </p>
        <p v-else-if="!historicalTask" class="muted">暂无新鲜机构反馈</p>
        <h4>{{ historicalTask ? '历史 PLC 证据' : 'PLC 信号与反馈' }}</h4>
        <el-table :data="detail.plcConditions ?? []" size="small" empty-text="当前步骤没有返回 PLC 条件">
          <el-table-column prop="stationId" label="工位" />
          <el-table-column prop="variableName" label="信号" min-width="180" />
          <el-table-column prop="address" label="地址" />
          <el-table-column label="期望"><template #default="{ row }">{{ row.expectedValue ? '1' : '0' }}</template></el-table-column>
          <el-table-column label="反馈"><template #default="{ row }">{{ row.currentValue == null ? '未知' : row.currentValue ? '1' : '0' }}</template></el-table-column>
          <el-table-column prop="quality" label="质量" />
          <el-table-column label="观测时间" min-width="175"><template #default="{ row }">{{ displayTime(row.observedAt) }}</template></el-table-column>
          <el-table-column prop="reason" label="判定原因" min-width="170" />
        </el-table>
        <p v-for="handshake in detail.handshakes ?? []" :key="handshake.stepId" class="muted">
          {{ displayTime(handshake.updatedAt) }} · {{ handshake.reason || handshake.phase }}
          <template v-if="handshake.pendingSignal"> · 待写 {{ handshake.pendingSignal }} = {{ handshake.pendingValue == null ? '未知' : Number(handshake.pendingValue) }}</template>
        </p>
        <details v-if="detail.executionTimeline?.length">
          <summary>执行时间线 · {{ detail.executionTimeline.length }} 条</summary>
          <ul class="evidence-list">
            <li v-for="event in detail.executionTimeline" :key="event.id">{{ displayTime(event.occurredAt) }} · {{ event.title }}<span v-if="event.summary"> · {{ event.summary }}</span></li>
          </ul>
        </details>
        <details v-if="detail.commands?.length">
          <summary>导航与机构命令 · {{ detail.commands.length }} 条</summary>
          <ul class="evidence-list">
            <li v-for="command in detail.commands" :key="command.commandId">
              {{ commandLabel(command) }} · {{ stepStates[command.status ?? ''] || command.status }} · {{ displayTime(command.sentAt) }}
              <el-button v-if="command.route?.edgeIds?.length" link type="primary" @click="showCommand(command.commandId)">查看{{ command.commandId === currentCommand?.commandId ? '当前' : '历史' }}路线</el-button>
              <div class="muted">{{ command.vendorCommandId || command.commandId }}</div>
              <div v-if="command.lastError">{{ command.lastError }}</div>
            </li>
          </ul>
        </details>
        <details>
          <summary>原始编号与诊断</summary>
          <p><code>{{ detail.task.taskId }}</code> <el-button link type="primary" @click="copyId(detail.task.taskId)">复制任务号</el-button></p>
          <p>{{ detail.waitingExplanation || selectedCall?.waitingExplanation || '暂无诊断说明' }}</p>
        </details>
      </div>
      <el-empty v-else-if="!detailError && !selectedCall" :description="selectedTask ? '正在读取九步任务' : selectedVehicleId ? `${selectedVehicleId} 暂无执行中的业务任务` : '从呼叫、车辆或地图打开任务'" />
    </section>
    <section v-if="section === 'monitor'" class="panel tasks-panel">
      <div class="panel-heading">
        <h3>原任务 · 最近 {{ tasks.length }} 条</h3>
        <el-space wrap>
          <el-select v-model="targetMap" style="width: 220px" placeholder="选择导入后的实验路网">
            <el-option v-for="map in maps" :key="map.id" :label="map.name" :value="map.id" />
          </el-select>
          <el-button :disabled="!fresh || busy || !targetMap || !selectedTasks.length" @click="previewTasks">
            预览选中命令为实验任务
          </el-button>
        </el-space>
      </div>
      <el-table :data="tasks" row-key="taskId" @selection-change="selectedTasks = $event" @row-click="(row: TaskRow) => openCallTask(row.taskId)">
        <el-table-column type="selection" width="45" />
        <el-table-column prop="taskId" label="原任务 ID" min-width="240" />
        <el-table-column prop="vehicleId" label="车辆" width="120" />
        <el-table-column prop="stationId" label="工位" width="140" />
        <el-table-column prop="stage" label="阶段" width="160" />
      </el-table>
    </section>
    <DispatchMapImport v-show="section === 'engineering'" @saved="mapSaved" />
    <div v-show="section === 'monitor' || section === 'report'" class="lower-grid" :class="{ 'report-grid': section === 'report' }">
      <section v-if="section === 'monitor'" class="panel resource-panel">
        <div class="panel-heading">
          <h3>交通资源</h3>
          <el-radio-group v-model="trafficView" size="small" aria-label="交通资源查看方式">
            <el-radio-button value="requests">按等待申请</el-radio-button>
            <el-radio-button value="resources">按资源查找</el-radio-button>
          </el-radio-group>
          <div v-if="trafficView === 'resources'" class="resource-filters">
            <el-input v-model="resourceQuery" size="small" clearable placeholder="搜索名称或完整编号" aria-label="搜索交通资源" />
            <el-checkbox v-model="showAllResources">显示空闲资源</el-checkbox>
          </div>
        </div>
        <p v-if="!waitFresh" class="muted">占用与等待快照暂不可用。</p>
        <el-table v-if="trafficView === 'requests'" :data="waitingApplications" row-key="key" size="small" empty-text="当前没有路权等待申请">
          <el-table-column type="expand">
            <template #default="{ row }">
              <div class="resource-detail">
                <p>{{ row.request.source || '起点未知' }} → {{ row.request.destination || '目标未知' }}</p>
                <p v-for="blocker in row.blockers" :key="blocker.resourceId">
                  <el-button link type="primary" @click="inspectResource(blocker.resourceId, true)">
                    {{ resourceIndex.get(blocker.resourceId)?.label || blocker.resourceId }}
                  </el-button>
                  · {{ blocker.vehicles.join('、') || '占用车待确认' }}
                </p>
                <p v-if="!row.blockers.length" class="muted">{{ briefReason(row.request.reason) || '等待调度复核' }}</p>
              </div>
            </template>
          </el-table-column>
          <el-table-column label="等待车" min-width="116"><template #default="{ row }">
            <el-button link type="primary" @click="openCallTask(row.request.taskId)">{{ waitVehicleLabel(row.request) }}</el-button>
          </template></el-table-column>
          <el-table-column label="占用车" min-width="108"><template #default="{ row }">{{ row.holders.join('、') || '—' }}</template></el-table-column>
          <el-table-column label="当前阻塞" min-width="158"><template #default="{ row }">
            <template v-if="row.blockers[0]">
              <el-button link type="primary" :title="row.blockers[0].resourceId" @click="inspectResource(row.blockers[0].resourceId, true)">
                {{ resourceIndex.get(row.blockers[0].resourceId)?.label || row.blockers[0].resourceId }}
              </el-button>
              <span v-if="row.blockers.length > 1"> 等 {{ row.blockers.length }} 项</span>
            </template>
            <span v-else>待调度复核</span>
          </template></el-table-column>
          <el-table-column label="申请已等" min-width="108"><template #default="{ row }">{{ dispatchWaitDuration(row.request) }}</template></el-table-column>
          <el-table-column label="连续阻塞" min-width="112"><template #default="{ row }">
            {{ row.blockingSeconds !== null ? elapsedLabel(row.blockingSeconds) : '—' }}
          </template></el-table-column>
        </el-table>
        <el-table v-else :data="visibleResources" row-key="resourceId" size="small" empty-text="当前筛选下没有交通资源">
          <el-table-column label="资源" min-width="195"><template #default="{ row }">
            <el-button link type="primary" @click="inspectResource(row.resourceId)">{{ row.label }}</el-button>
          </template></el-table-column>
          <el-table-column label="状态" width="105"><template #default="{ row }">
            <el-tag size="small" :type="row.blocking ? 'danger' : row.waits.length ? 'warning' : row.occupiedCount ? 'primary' : 'info'">{{ row.blocking ? '正在阻塞' : row.waits.length ? '有人等待' : row.occupiedCount ? '占用中' : '空闲' }}</el-tag>
          </template></el-table-column>
          <el-table-column label="占用 / 容量" width="112"><template #default="{ row }">{{ row.occupiedCount }}/{{ row.capacity }}</template></el-table-column>
          <el-table-column label="占用车辆" min-width="140"><template #default="{ row }">{{ [...new Set(row.leases?.map((lease: { vehicleId: string }) => lease.vehicleId))].filter(Boolean).join('、') || '—' }}</template></el-table-column>
          <el-table-column label="等候车辆" min-width="190"><template #default="{ row }">
            <div v-for="(request, index) in row.waits.slice(0, 3)" :key="`${request.taskId}-${request.stepId}-${index}`">{{ waitVehicleLabel(request) }} · {{ dispatchWaitDuration(request) }}</div>
            <span v-if="row.waits.length > 3">另有 {{ row.waits.length - 3 }} 项</span><span v-if="!row.waits.length">—</span>
          </template></el-table-column>
          <el-table-column label="地图" width="78"><template #default="{ row }"><el-button v-if="row.location?.point || row.location?.nodeId || row.location?.edgeIds.length" link type="primary" @click="inspectResource(row.resourceId, true)">定位</el-button></template></el-table-column>
        </el-table>
        <el-pagination v-if="trafficView === 'resources' && filteredResources.length > resourcePageSize" v-model:current-page="resourcePage" :page-size="resourcePageSize" :total="filteredResources.length" layout="total, prev, pager, next" size="small" />
        <div v-if="selectedResourceId" class="resource-detail">
          <div class="panel-heading">
            <h4>{{ resourceIndex.get(selectedResourceId)?.label || '资源详情' }}</h4>
            <el-button link @click="selectedResourceId = ''">关闭详情</el-button>
          </div>
          <div class="resource-id"><code>{{ selectedResourceId }}</code><el-button link type="primary" @click="copyId(selectedResourceId)">复制编号</el-button></div>
          <p v-for="lease in selectedResource?.leases ?? []" :key="`${lease.vehicleId}-${lease.taskId}`">
            {{ lease.vehicleId }} · 持有中
            <el-button v-if="lease.taskId" link type="primary" @click="openCallTask(lease.taskId)">占用任务</el-button>
          </p>
          <p v-for="(request, index) in selectedResourceWaits" :key="`${request.taskId}-${request.stepId}-${index}`">
            {{ waitVehicleLabel(request) }} · {{ dispatchWaitDuration(request) }}
            <el-button v-if="request.taskId" link type="primary" @click="openCallTask(request.taskId)">等待任务</el-button>
          </p>
          <p v-if="resourceEvidenceStatus" class="muted">{{ resourceEvidenceStatus }}</p>
          <ul v-else class="evidence-list">
            <li v-for="event in resourceEvents" :key="event.eventId">
              {{ displayTime(event.occurredAt) }} · {{ event.vehicleId || '车辆未知' }} · {{ event.kind === 'SegmentReleased' ? '通过校验后逐段释放' : event.kind === 'ResourceAcquired' ? '取得路权' : '已释放' }}
              <span v-if="event.reason"> · {{ event.reason }}</span>
            </li>
          </ul>
        </div>
      </section>
      <section v-if="section === 'report'" class="panel">
        <div class="panel-heading">
          <h3>业务报告</h3>
          <el-select :model-value="historicalReportId" class="report-select" aria-label="选择业务报告" @change="selectReport">
            <el-option label="当前报告" value="" />
            <el-option v-for="entry in status?.history ?? []" :key="entry.id" :value="entry.id || ''"
              :label="`${entry.finishedAt ? new Date(entry.finishedAt * 1000).toLocaleString() : entry.id} · ${entry.name || '业务仿真'}`" />
          </el-select>
          <el-button
            :disabled="!displayedReport"
            size="small"
            @click="reportDownload"
            >下载原报告</el-button
          >
        </div>
        <p>{{ reportBusy ? '正在读取报告' : ({ passed: "通过", failed: "失败", aborted: "已中止" } as Record<string, string>)[displayedReport?.status ?? ""] ?? displayedReport?.status ?? "暂无报告" }}</p>
        <el-alert v-if="reportError" :title="reportError" type="error" :closable="false" />
        <el-alert
          v-if="displayedReport?.firstFailure || displayedReport?.message"
          :title="displayedReport.firstFailure || displayedReport.message"
          :type="displayedReport.status === 'failed' ? 'error' : 'info'"
          :closable="false"
        />
        <DispatchReplay v-if="displayedReport?.id" :key="displayedReport.id" :report-id="displayedReport.id" />
        <el-table v-if="displayedReport?.waitingChain?.length" :data="displayedReport.waitingChain" size="small">
          <el-table-column prop="stationId" label="工位" width="100" />
          <el-table-column prop="vehicleId" label="车辆" width="100" />
          <el-table-column label="当前步骤" min-width="140"><template #default="{ row }">第 {{ row.stepNumber }} 步 · {{ row.stepName }}</template></el-table-column>
          <el-table-column prop="waitReason" label="等待说明" min-width="250" />
          <el-table-column label="正式任务" width="112"><template #default="{ row }"><el-button v-if="row.taskId" link type="primary" @click="openCallTask(row.taskId)">查看九步</el-button></template></el-table-column>
        </el-table>
        <p
          v-for="(value, key) in displayedReport?.metrics ?? {}"
          :key="key"
          class="muted"
        >
          {{ key }} · {{ value }}
        </p>
        <p v-for="check in displayedReport?.checks ?? []" :key="check.name">
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
.dispatch-page > .dispatch-body { order: 2; }
.dispatch-page > .business-setup { order: 2; }
.dispatch-page > .workflow-nav { order: 1; }
.dispatch-page > .calls-panel { order: 3; }
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
.source-panel p {
  margin: 4px 0 0;
  overflow-wrap: anywhere;
}
.dispatch-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(275px, 315px);
  gap: 16px;
  align-items: start;
}
.dispatch-scene {
  min-width: 0;
}
.panel-heading h3 {
  margin: 0 0 12px;
}
.vehicles {
  display: grid;
  gap: 8px;
}
.vehicles .panel-heading h3, .resource-panel .panel-heading h3 { margin: 0; }
.vehicle-row {
  padding: 10px;
  border: 1px solid var(--ui-border, #e0e7f0);
  border-radius: 8px;
}
.vehicle-selected { background: var(--el-color-primary-light-9); border-color: var(--el-color-primary-light-5); }
.vehicle-title {
  display: flex;
  align-items: center;
  gap: 6px 10px;
  flex-wrap: wrap;
}
.vehicle-title .el-tag { margin-left: auto; }
.vehicle-battery { font-size: 13px; font-weight: 600; font-variant-numeric: tabular-nums; }
.low-battery { color: #a34d17; }
.vehicle-facts { display: grid; gap: 4px; margin: 8px 0 0; font-size: 12px; }
.vehicle-facts div { display: grid; grid-template-columns: 4em minmax(0, 1fr); gap: 6px; }
.vehicle-facts dt { color: var(--ui-text-3); }
.vehicle-facts dd { margin: 0; overflow-wrap: anywhere; }
.vehicle-row p {
  font-size: 13px;
  color: #606266;
  margin: 8px 0;
  overflow-wrap: anywhere;
}
.lower-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 16px;
}
.lower-grid.report-grid {
  grid-template-columns: minmax(0, 1fr);
}
.resource-panel .el-input {
  width: 200px;
  max-width: 100%;
}
.resource-panel .el-pagination {
  margin-top: 12px;
}
.resource-filters, .resource-id, .call-detail {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 14px;
}
.resource-detail, .call-detail {
  margin: 12px 0;
  padding: 12px;
  border: 1px solid var(--ui-border, #e0e7f0);
  border-radius: 8px;
}
.resource-detail h4 { margin: 0; }
.resource-id code, .task-detail code { overflow-wrap: anywhere; }
.call-number { margin-left: 4px; }
.evidence-list { padding-left: 18px; }
.evidence-list li { margin: 6px 0; overflow-wrap: anywhere; }
.task-detail {
  margin-top: 18px;
}
.route-toolbar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin: 10px 0;
}
.route-toolbar .el-button {
  margin-left: 0;
}
.route-toolbar .el-select, .report-select {
  width: min(100%, 350px);
}
.task-detail :deep(.current-business-step) {
  --el-table-tr-bg-color: var(--el-color-primary-light-9);
}
.task-detail details {
  overflow-wrap: anywhere;
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
}
</style>
