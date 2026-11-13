<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { ElMessage } from "element-plus";
import {
  createRoadNet,
  validateRoadNet,
  type ValidationError,
} from "@/api/roadnet";
import {
  convertDispatchMap,
  roadnetFingerprint,
  type SourceMap,
  type MapConversion,
} from "@/integration/dispatch-adapter";
import {
  loadPublishedDispatchMap,
  downloadIntegrationRecord,
  type DispatchStatus,
} from "@/integration/dispatch-api";
const props = defineProps<{ status: DispatchStatus | null }>();
const emit = defineEmits<{ saved: [id: number] }>();
const definition = ref<SourceMap | null>(null),
  identity = ref<Record<string, unknown>>({}),
  initial = ref(""),
  roundTripOnly = ref(false),
  name = ref("");
const conversion = ref<MapConversion | null>(null),
  errors = ref<ValidationError[]>([]),
  error = ref(""),
  busy = ref(false),
  savedId = ref<number | null>(null);
const choices = computed(
  () => definition.value?.nodes.filter((n) => n.enabled !== false) ?? [],
);
function reset(def: SourceMap, source: Record<string, unknown>) {
  roundTripOnly.value = false;
  definition.value = def;
  identity.value = source;
  initial.value = "";
  name.value = `${def.name ?? "工程地图"} · 算法实验副本`;
  conversion.value = null;
  errors.value = [];
  error.value = "";
  savedId.value = null;
}
async function loadActive() {
  busy.value = true;
  error.value = "";
  try {
    const source = await loadPublishedDispatchMap();
    reset(source.definition, source.identity);
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
function loadSimulation() {
  if (!props.status?.map) return;
  reset(JSON.parse(JSON.stringify(props.status.map)), {
    ...props.status.configuration?.mapIdentity,
    instance: props.status.rootName,
  });
}
async function loadFile(event: Event) {
  const input = event.target as HTMLInputElement,
    file = input.files?.[0];
  if (!file) return;
  try {
    if (file.size > 8 * 1024 * 1024) throw new Error("地图文件超过 8 MiB");
    const source = JSON.parse(await file.text()),
      def = source.conversion?.sourceDefinition ?? source.definition ?? source;
    if (!Array.isArray(def.nodes) || !Array.isArray(def.edges))
      throw new Error("地图文件缺少 nodes / edges");
    reset(def, source.identity ?? { sourceName: file.name });
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    input.value = "";
  }
}
async function preview() {
  if (!definition.value) return;
  busy.value = true;
  error.value = "";
  conversion.value = null;
  savedId.value = null;
  try {
    conversion.value = convertDispatchMap(
      definition.value,
      initial.value || undefined,
      { roundTripOnly: roundTripOnly.value },
    );
    errors.value = await validateRoadNet(conversion.value.roadnet);
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
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
          sourceDefinition: conversion.value.sourceDefinition,
          coordinateOffset: conversion.value.coordinateOffset,
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
    ElMessage.success(
      "已保存为独立实验路网，可进入车间地图、任务集及对比实验使用",
    );
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = false;
  }
}
watch([initial, roundTripOnly], () => {
  conversion.value = null;
  errors.value = [];
});
function download() {
  downloadIntegrationRecord(
    {
      identity: identity.value,
      conversion: conversion.value,
      savedRoadnetId: savedId.value,
    },
    "dispatch-map-import.json",
  );
}
</script>
<template>
  <section class="panel map-import">
    <h3>导入工程地图</h3>
    <el-space wrap>
      <el-button :loading="busy" @click="loadActive"
        >读取主工程活动地图</el-button
      >
      <el-button :disabled="!status?.map || busy" @click="loadSimulation"
        >读取隔离仿真地图</el-button
      >
      <label class="file-button"
        >上传完整地图<input type="file" accept=".json" @change="loadFile"
      /></label>
    </el-space>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <div v-if="definition" class="import-form">
      <p>
        {{ definition.name || "工程地图" }} · {{ definition.nodes.length }} 节点
        · {{ definition.edges.length }} 道路 ·
        {{ definition.unit || "单位未提供" }}
      </p>
      <el-form label-width="140px">
        <el-form-item label="副本名称"
          ><el-input v-model="name" maxlength="128"
        /></el-form-item>
        <el-form-item label="实验初始停放点"
          ><el-select
            v-model="initial"
            filterable
            clearable
            placeholder="沿用原图充电位，或显式选择实验停放点"
            ><el-option
              v-for="node in choices"
              :key="node.id"
              :value="node.id"
              :label="node.id" /></el-select
        ></el-form-item>
        <el-form-item label="实验导入范围"
          ><el-checkbox v-model="roundTripOnly"
            >仅导入初始点可往返区域</el-checkbox
          ></el-form-item
        >
      </el-form>
      <el-button :loading="busy" @click="preview">预览转换与校验</el-button>
      <template v-if="conversion">
        <p>
          {{ conversion.roadnet.nodes.length }} 实验节点 ·
          {{ conversion.roadnet.edges.length }} 实验路段
        </p>
        <ul class="conversion-warnings">
          <li v-for="warning in conversion.warnings" :key="warning">
            {{ warning }}
          </li>
        </ul>
        <el-alert
          v-for="(item, i) in errors"
          :key="i"
          :title="item.message"
          type="warning"
          :closable="false"
        />
        <el-space
          ><el-button
            type="primary"
            :disabled="busy || !!savedId || errors.length > 0 || !name.trim()"
            @click="save"
            >{{ savedId ? "已保存实验副本" : "确认保存实验副本" }}</el-button
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
.el-select {
  width: 100%;
}
</style>
