<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, watch } from "vue";
import { ElMessage } from "element-plus";
import EngineeringPointMap from "./EngineeringPointMap.vue";
import {
  createRoadNet,
  validateRoadNet,
  type ValidationError,
} from "@/api/roadnet";
import {
  convertDispatchMap,
  applyImportRoadDirections,
  sourcePointTraffic,
  dispatchSceneMap,
  RoundTripBlockedError,
  resolveMapSiteId,
  roadnetFingerprint,
  simulationRoadnetName,
  type SourceMap,
  type MapConversion,
} from "@/integration/dispatch-adapter";
import {
  loadWebSimulationMap,
  downloadIntegrationRecord,
} from "@/integration/dispatch-api";
const emit = defineEmits<{ saved: [id: number] }>();
const definition = shallowRef<SourceMap | null>(null),
  identity = ref<Record<string, unknown>>({}),
  initial = ref(""),
  roundTripOnly = ref(true),
  simplifyRoads = ref(true),
  name = ref(""),
  protectedSites = ref<string[]>([]);
const conversion = shallowRef<MapConversion | null>(null),
  errors = ref<ValidationError[]>([]),
  error = ref(""),
  busy = ref(false),
  savedId = ref<number | null>(null);
const directions = shallowRef<Record<string, boolean>>({});
const selectedSite = ref("");
const blockedSites = ref<string[]>([]);
const diagnosticsMap = ref<InstanceType<typeof EngineeringPointMap>>();
const draft = computed(() => definition.value ? applyImportRoadDirections(definition.value, directions.value) : null);
const point = computed(() => draft.value?.nodes.find(node => node.id === selectedSite.value));
const sourceRoads = computed(() => draft.value?.edges.map((road, i) => ({ ...road, id: road.id ?? `source-edge-${i}` })) ?? []);
const nodeIndex = computed(() => new Map(draft.value?.nodes.map(node => [node.id, node])));
const roadsById = computed(() => new Map(sourceRoads.value.map(road => [road.id, road])));
const incident = computed(() => sourceRoads.value.filter(road => road.from === selectedSite.value || road.to === selectedSite.value));
const highlights = computed(() => new Set(incident.value.map(road => road.id)));
const pointTraffic = computed(() => point.value && draft.value ? sourcePointTraffic(draft.value, point.value.id) : null);
const mapPreview = computed(() => {
  const map = draft.value;
  if (!map) return null;
  const edges = sourceRoads.value.filter(road => road.enabled !== false);
  try { return dispatchSceneMap({ ...map, edges }); }
  catch {
    return dispatchSceneMap({ ...map, edges: edges.map(road => ({ ...road, geometryKind: "Line", controlPoints: null })) });
  }
});
const problemMarkers = computed(() => {
  const markers = new Map<string, { label: string; nodeId: string; group: string }>();
  for (const id of conversion.value?.excludedNodes ?? []) markers.set(id, { label: id, nodeId: id, group: "excluded" });
  for (const id of blockedSites.value) markers.set(id, { label: id, nodeId: id, group: "error" });
  for (const issue of errors.value) {
    const id = issueLocation(issue);
    if (id) markers.set(id, { label: id, nodeId: id, group: "error" });
  }
  for (const road of skippedRoads.value) {
    const id = nodeIndex.value.has(road.from) ? road.from : road.to;
    if (nodeIndex.value.has(id) && !markers.has(id)) markers.set(id, { label: id, nodeId: id, group: "error" });
  }
  return [...markers.values()];
});
const skippedRoads = computed(() => sourceRoads.value.filter(road =>
  road.enabled !== false && [road.from, road.to].some(id => !nodeIndex.value.has(id) || nodeIndex.value.get(id)?.enabled === false),
));
function issueLocation(issue: ValidationError) {
  if (issue.nodeId && nodeIndex.value.has(issue.nodeId)) return issue.nodeId;
  if (!issue.edgeId) return "";
  const sourceId = Object.entries(conversion.value?.edgeMapping ?? {}).find(([, ids]) => ids.includes(issue.edgeId!))?.[0] ?? issue.edgeId;
  const edge = roadsById.value.get(sourceId);
  return edge && (nodeIndex.value.has(edge.from) ? edge.from : edge.to) || "";
}
function focusPoint(id: string) {
  selectedSite.value = id;
  diagnosticsMap.value?.focus();
}
function changeDirection(id: string, value: boolean) {
  const index = sourceRoads.value.findIndex(road => road.id === id);
  const source = definition.value?.edges[index];
  if (!source) return;
  const next = { ...directions.value, [id]: value };
  if (!!source.bidirectional === value) delete next[id];
  directions.value = next;
}
const choices = computed(
  () => definition.value?.nodes.filter((n) => n.enabled !== false).map(n => ({ value: n.id, label: n.id })) ?? [],
);
const sourceLabel = computed(() =>
  String(
    identity.value.mapName ??
      identity.value.sourceName ??
      definition.value?.name ??
      "Web 仿真地图",
  ),
);
const sourceTitle = computed(
  () =>
    `版本 ${String(identity.value.mapVersionId ?? "未知")} · 摘要 ${String(identity.value.definitionHash ?? "未知")}`,
);
function defaultInitialSite(def: SourceMap) {
  return (
    def.nodes.find(
      (node) => node.enabled !== false && node.type === "ChargingPoint",
    )?.id ??
    def.nodes.find((node) => node.enabled !== false && node.type === "Station")
      ?.id ??
    def.nodes[0]?.id ??
    ""
  );
}
function defaultName(def: SourceMap) {
  return simulationRoadnetName(def);
}
function reset(
  def: SourceMap,
  source: Record<string, unknown>,
  suggestedInitialSite = "",
  protectedSourceSites: string[] = [],
) {
  roundTripOnly.value = true;
  simplifyRoads.value = true;
  definition.value = def;
  directions.value = {};
  selectedSite.value = "";
  identity.value = source;
  protectedSites.value = protectedSourceSites.map(site => resolveMapSiteId(def, site) || site);
  initial.value =
    resolveMapSiteId(def, suggestedInitialSite) || defaultInitialSite(def);
  name.value = defaultName(def);
  conversion.value = null;
  errors.value = [];
  error.value = "";
  savedId.value = null;
}
async function loadActive() {
  busy.value = true;
  error.value = "";
  try {
    const source = await loadWebSimulationMap();
    reset(source.definition, source.identity, source.suggestedInitialSite, source.protectedSites);
    await nextTick();
    await preview();
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
async function loadFile(event: Event) {
  const input = event.target as HTMLInputElement,
    file = input.files?.[0];
  if (!file) return;
  try {
    if (file.size > 8 * 1024 * 1024) throw new Error("地图文件超过 8 MiB");
    const source = JSON.parse(await file.text()),
      def = source.definition ?? source.conversion?.sourceDefinition ?? source;
    if (!Array.isArray(def.nodes) || !Array.isArray(def.edges))
      throw new Error("地图文件缺少 nodes / edges");
    reset(def, source.identity ?? { sourceName: file.name });
    directions.value = Object.fromEntries(Object.entries(source.roadDirections ?? {}).flatMap(
      ([id, value]) => typeof value === "boolean" ? [[id, value] as const] : [],
    ));
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    input.value = "";
  }
}
async function preview() {
  if (!draft.value) return;
  busy.value = true;
  error.value = "";
  conversion.value = null;
  blockedSites.value = [];
  savedId.value = null;
  try {
    const next = convertDispatchMap(
      draft.value,
      initial.value || undefined,
      {
        roundTripOnly: roundTripOnly.value,
        simplifyRoads: simplifyRoads.value,
        protectedSiteIds: protectedSites.value,
      },
    );
    errors.value = await validateRoadNet(next.roadnet);
    conversion.value = next;
  } catch (e) {
    if (e instanceof RoundTripBlockedError) blockedSites.value = e.nodeIds;
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
async function previewRoundTrip() {
  roundTripOnly.value = true;
  await nextTick();
  await preview();
}
async function save() {
  if (!conversion.value || errors.value.length || savedId.value) return;
  busy.value = true;
  error.value = "";
  try {
    const result = await createRoadNet({
      name: name.value,
      roadnet: {
        ...conversion.value.roadnet,
        integration: {
          identity: identity.value,
          importedGraphFingerprint: roadnetFingerprint(
            conversion.value.roadnet,
          ),
          sourceDefinition: definition.value,
          sourceRoadDirections: directions.value,
          coordinateOffset: conversion.value.coordinateOffset,
          coordinateTransform: {
            xScale: 1,
            yScale: -1,
            xTranslate: -conversion.value.coordinateOffset.x,
            yTranslate: conversion.value.coordinateOffset.y,
          },
          edgeMapping: conversion.value.edgeMapping,
          excludedNodes: conversion.value.excludedNodes,
          excludedEdges: conversion.value.excludedEdges,
        },
      },
      isTemplate: false,
    });
    savedId.value = result.id;
    try {
      localStorage.setItem(
        `dispatch-import-${result.id}`,
        JSON.stringify({
          identity: identity.value,
          roadnet: conversion.value.roadnet,
        }),
      );
    } catch {
      ElMessage.warning("路网已保存，请下载来源记录，浏览器未能保存映射缓存");
    }
    emit("saved", result.id);
    ElMessage.success("已保存仿真路网");
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
watch([initial, roundTripOnly, simplifyRoads, directions], () => {
  conversion.value = null;
  errors.value = [];
  blockedSites.value = [];
  savedId.value = null;
  error.value = "";
});
function download() {
  downloadIntegrationRecord(
    {
      identity: identity.value,
      definition: definition.value,
      roadDirections: directions.value,
      conversion: conversion.value && { ...conversion.value, sourceDefinition: undefined },
      savedRoadnetId: savedId.value,
    },
    "dispatch-map-import.json",
  );
}
</script>
<template>
  <section class="panel map-import">
    <h3>导入 Web 仿真地图</h3>
    <p class="muted">
      优先读取 5199 中已发布的五车全厂仿真图，保存后供本模块算法仿真使用。
    </p>
    <el-space wrap>
      <el-button type="primary" :loading="busy" @click="loadActive"
        >读取 Web 仿真地图</el-button
      >
      <label class="file-button"
        >备用：载入地图文件<input type="file" accept=".json" :disabled="busy" @change="loadFile"
      /></label>
    </el-space>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <div v-if="definition" class="import-form">
      <p class="source-line" :title="sourceTitle">
        Web 仿真地图：<strong>{{ sourceLabel }}</strong>
      </p>
      <div v-if="mapPreview" class="diagnostics-layout">
        <EngineeringPointMap
          ref="diagnosticsMap"
          diagnostic
          :map="mapPreview"
          :markers="problemMarkers"
          :selected-id="selectedSite"
          :highlight-edges="highlights"
          @select="selectedSite = $event"
        />
        <aside class="point-inspection">
          <h4>点位与关联道路</h4>
          <el-select-v2 v-model="selectedSite" :options="choices" filterable placeholder="搜索地图点位" @change="focusPoint" />
          <template v-if="point && pointTraffic">
            <p class="point-coordinates">{{ point.id }} · {{ point.type || "未分类" }} · X {{ point.x.toFixed(3) }} · Y {{ point.y.toFixed(3) }} {{ definition.unit }}</p>
            <p>进入 {{ pointTraffic.incoming }} 条 · 离开 {{ pointTraffic.outgoing }} 条</p>
            <ul class="incident-roads">
              <li v-for="road in incident" :key="road.id">
                <strong>{{ road.id }}</strong>
                <span>{{ road.from }} {{ road.bidirectional ? "↔" : "→" }} {{ road.to }}</span>
                <el-checkbox :model-value="!!road.bidirectional" :disabled="busy || road.enabled === false" @change="changeDirection(road.id, $event === true)">双向</el-checkbox>
              </li>
            </ul>
            <p v-if="!incident.length">没有关联道路。</p>
          </template>
          <p v-else>从地图或校验列表选择点位。</p>
          <el-button v-if="Object.keys(directions).length" link :disabled="busy" @click="directions = {}">撤销本次道路修改</el-button>
        </aside>
      </div>
      <details class="map-rules">
        <summary>地图校验规则</summary>
        <ul>
          <li>点位与道路编号唯一，道路端点存在且不同，实验图中同一端点对只保留一条道路。</li>
          <li>坐标有效，路长和速度大于 0，点位和道路容量至少为 1。</li>
          <li>至少一个充电位，每个充电位可达全部工位，禁止只进不出；缓存位只连接一条道路。</li>
          <li>启用可往返范围时，保留初始点能够到达、也能返回初始点的节点。裁剪涉及业务点时停止转换。</li>
        </ul>
      </details>
      <p>
        {{ definition.name || "Web 仿真图" }} ·
        {{ definition.nodes.length }} 节点 · {{ definition.edges.length }} 道路
        ·
        {{ definition.unit || "单位未提供" }}
      </p>
      <el-form label-width="140px">
        <el-form-item label="仿真路网名称"
          ><el-input v-model="name" maxlength="128"
        /></el-form-item>
        <el-form-item label="实验初始停放点"
          ><el-select-v2
            v-model="initial"
            :options="choices"
            :disabled="busy"
            filterable
            clearable
            placeholder="沿用原图充电位，或显式选择实验停放点"
            />
        ></el-form-item>
        <el-form-item label="实验导入范围"
          ><el-checkbox v-model="roundTripOnly" :disabled="busy"
            >仅导入初始点可往返区域</el-checkbox
          ></el-form-item
        >
        <el-form-item label="道路显示">
          <el-checkbox v-model="simplifyRoads" :disabled="busy"
            >简化道路（推荐，保留真实长度和方向）</el-checkbox
          >
        </el-form-item>
      </el-form>
      <el-button :loading="busy" @click="preview">预览转换与校验</el-button>
      <ul v-if="blockedSites.length" class="diagnostic-items validation-errors">
        <li v-for="id in blockedSites" :key="id">
          <el-button link type="danger" @click="focusPoint(id)">{{ id }} · 无法与初始点往返</el-button>
        </li>
      </ul>
      <p v-if="Object.keys(directions).length" class="muted">已定制 {{ Object.keys(directions).length }} 条实验道路。方向修改后需重新预览校验。</p>
      <details v-if="skippedRoads.length" class="map-rules">
        <summary>端点停用或缺失 · {{ skippedRoads.length }} 条道路</summary>
        <ul class="diagnostic-items">
          <li v-for="road in skippedRoads" :key="road.id">
            <el-button v-if="nodeIndex.has(road.from) || nodeIndex.has(road.to)" link type="danger" @click="focusPoint(nodeIndex.has(road.from) ? road.from : road.to)">{{ road.id }} · {{ road.from }} → {{ road.to }}</el-button>
            <span v-else>{{ road.id }} · {{ road.from }} → {{ road.to }}</span>
          </li>
        </ul>
      </details>
      <template v-if="conversion">
        <p>
          {{ conversion.roadnet.nodes.length }} 实验节点 ·
          {{ conversion.roadnet.edges.length }} 实验路段
        </p>
        <div v-if="conversion.excludedNodes.length" class="excluded-list">
          <strong>实验副本排除 · {{ conversion.excludedNodes.length }} 点 · {{ conversion.excludedEdges.length }} 道路</strong>
          <ul class="diagnostic-items">
            <li v-for="id in conversion.excludedNodes" :key="id">
              <el-button link type="warning" @click="focusPoint(id)">{{ id }} · {{ sourcePointTraffic(draft!, id).reason }}</el-button>
            </li>
          </ul>
        </div>
        <ul class="conversion-warnings">
          <li v-for="warning in conversion.warnings" :key="warning">
            {{ warning }}
          </li>
        </ul>
        <ul v-if="errors.length" class="diagnostic-items validation-errors">
          <li v-for="(item, i) in errors" :key="i">
            <el-button v-if="issueLocation(item)" link type="danger" @click="focusPoint(issueLocation(item))">{{ item.message }}</el-button>
            <span v-else>{{ item.message }}</span>
          </li>
        </ul>
        <el-button v-if="errors.length && !roundTripOnly" :disabled="busy" @click="previewRoundTrip">
          改为仅导入可往返区域
        </el-button>
        <el-space
          ><el-button
            type="primary"
            :disabled="busy || !!savedId || errors.length > 0 || !name.trim()"
            @click="save"
            >{{ savedId ? "已保存仿真路网" : "保存仿真路网" }}</el-button
          ><el-button @click="download">下载来源与转换记录</el-button
          ><el-button v-if="savedId" @click="$router.push('/roadnet')"
            >进入车间地图</el-button
          ></el-space
        >
      </template>
    </div>
  </section>
</template>
<style scoped>
h3 {
  margin-top: 0;
}
.import-form {
  margin-top: 16px;
}
.source-line {
  color: #35516b;
}
.diagnostics-layout { display: grid; grid-template-columns: minmax(0, 1fr) minmax(245px, 300px); gap: 14px; }
.point-inspection { padding: 12px; background: #f5f7fc; border-radius: 8px; overflow: hidden; }
.point-inspection h4 { margin: 0 0 10px; }
.point-inspection p { font-size: 13px; }
.incident-roads, .diagnostic-items { list-style: none; padding: 0; margin: 6px 0; max-height: 200px; overflow: auto; }
.incident-roads li { padding: 6px 0; border-bottom: 1px solid #dce4f0; font-size: 12px; }
.incident-roads span { display: block; }
.map-rules { margin: 12px 0; }
.map-rules summary { cursor: pointer; }
.excluded-list, .validation-errors { padding: 10px 12px; border-radius: 6px; background: #fff6e8; margin: 10px 0; }
.validation-errors { background: #fef0f0; }
@media (max-width: 950px) { .diagnostics-layout { grid-template-columns: minmax(0, 1fr); } }
.file-button {
  display: inline-flex;
  position: relative;
  padding: 8px 15px;
  border: 1px solid #dcdfe6;
  border-radius: 4px;
  color: #606266;
  font-size: 14px;
  cursor: pointer;
}
.file-button input {
  position: absolute;
  inset: 0;
  opacity: 0;
  width: 100%;
  cursor: pointer;
}
.conversion-warnings {
  max-height: 160px;
  overflow: auto;
  padding-left: 20px;
  font-size: 13px;
  color: #7a5c2e;
}
.el-alert {
  margin: 10px 0;
}
.el-select-v2 {
  width: 100%;
}
</style>
