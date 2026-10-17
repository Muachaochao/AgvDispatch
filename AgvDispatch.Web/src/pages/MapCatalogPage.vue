<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { shortTime } from '../dashboard';
import type { MapSummary } from '../types';
import { ElCard, ElButton, ElTag, ElEmpty, ElIcon, ElDialog, ElDescriptions, ElDescriptionsItem, ElSpace, ElResult } from 'element-plus';
import { ArrowLeft, MapLocation, Refresh, View } from '@element-plus/icons-vue';

const store = useDispatchStore();
const router = useRouter();
const catalog = ref<MapSummary[]>([]);
const catalogError = ref('');
const selectedMap = ref<MapSummary | null>(null);

async function loadCatalog() {
  catalogError.value = '';
  try { catalog.value = await store.api<MapSummary[]>('/api/maps'); }
  catch (ex) { catalogError.value = String(ex); }
}
void loadCatalog();

const mapDialogVisible = computed({ get: () => selectedMap.value !== null, set: (v: boolean) => { if (!v) selectedMap.value = null; } });
</script>

<template>
  <div class="page-container">
    <div class="page-header">
      <h2><ElIcon :size="22"><MapLocation /></ElIcon> 地图查看与版本</h2>
      <ElTag type="info" effect="plain" size="small">共 {{ catalog.length }} 张</ElTag>
      <ElButton type="primary" size="small" @click="router.push({ name: 'planning', query: { tab: 'map' } })">地图 / 场景编辑</ElButton>
      <ElButton size="small" @click="router.push({ name: 'planning', query: { tab: 'plans' } })">方案管理</ElButton>
      <ElButton size="small" @click="loadCatalog"><ElIcon><Refresh /></ElIcon>刷新</ElButton>
      <ElButton size="small" @click="router.push('/')"><ElIcon><ArrowLeft /></ElIcon>返回大屏</ElButton>
    </div>

    <p style="color:#8ea7c5;font-size:12px;margin-bottom:8px">复用厂家建好的地图。本页查看运行版本；工程编辑中可加载完整草稿、编辑和发布。</p>

    <ElResult v-if="catalogError" icon="warning" :title="catalogError" />

    <div class="map-grid">
      <ElCard v-for="map in catalog" :key="map.mapId" shadow="hover" class="map-item">
        <div class="map-art">
          <ElIcon :size="48" color="#355c82"><MapLocation /></ElIcon>
          <ElTag :type="map.isActive ? 'success' : 'info'" size="small" effect="dark" class="map-badge">{{ map.isActive ? '当前运行' : '未运行' }}</ElTag>
        </div>
        <h3 class="map-name">{{ map.name }}</h3>
        <p class="map-id mono">{{ map.mapId }}</p>
        <p class="map-desc">{{ map.description || '未填写说明' }}</p>
        <ElDescriptions :column="1" size="small" border>
          <ElDescriptionsItem label="运行版本">{{ map.activeVersionId ?? '—' }}</ElDescriptionsItem>
          <ElDescriptionsItem label="发布版本">{{ map.publishedVersionId ?? '—' }}</ElDescriptionsItem>
          <ElDescriptionsItem label="坐标系">{{ map.coordinateSystem }} / {{ map.unit }}</ElDescriptionsItem>
        </ElDescriptions>
        <ElSpace style="margin-top:14px">
          <ElButton size="small" @click="selectedMap = map"><ElIcon><View /></ElIcon>查看信息</ElButton>
          <ElButton v-if="map.isActive && map.mapId === store.snapshot?.map?.id" type="primary" size="small" @click="router.push('/')">查看运行图</ElButton>
        </ElSpace>
      </ElCard>
      <ElEmpty v-if="!catalog.length && !catalogError" description="没有可查看的地图" />
    </div>
  </div>

  <ElDialog v-model="mapDialogVisible" title="地图版本信息" width="500px">
    <template v-if="selectedMap">
      <ElDescriptions :column="1" border size="default">
        <ElDescriptionsItem label="地图编号">{{ selectedMap.mapId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="地图名称">{{ selectedMap.name }}</ElDescriptionsItem>
        <ElDescriptionsItem label="坐标系">{{ selectedMap.coordinateSystem }}</ElDescriptionsItem>
        <ElDescriptionsItem label="单位">{{ selectedMap.unit }}</ElDescriptionsItem>
        <ElDescriptionsItem label="运行版本">{{ selectedMap.activeVersionId ?? '—' }}</ElDescriptionsItem>
        <ElDescriptionsItem label="发布版本">{{ selectedMap.publishedVersionId ?? '—' }}</ElDescriptionsItem>
        <ElDescriptionsItem label="更新时间">{{ shortTime(selectedMap.updatedAt) }}</ElDescriptionsItem>
      </ElDescriptions>
      <p style="color:#8198b8;font-size:11px;margin-top:14px">不在Web中修改或激活地图。</p>
    </template>
  </ElDialog>
</template>

<style scoped>
.page-container{display:flex;flex-direction:column;gap:12px}
.page-header{display:flex;align-items:center;gap:14px;padding:4px 0;flex-wrap:wrap}
.page-header h2{display:flex;align-items:center;gap:8px;font-size:18px;color:#d9eafb;margin:0}
.page-header .el-tag:first-of-type{margin-right:auto}
.map-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px}
.map-item{background:#0f1b30;border-color:#1e3352}
.map-item :deep(.el-card__body){padding:20px}
.map-item :deep(.el-descriptions){--el-descriptions-table-border:1px solid #1e3352}
.map-item :deep(.el-descriptions__label){background:#0a1525;color:#8da3c6}
.map-item :deep(.el-descriptions__content){background:#0f1b30;color:#c8daf0}
.map-art{position:relative;height:120px;background:#0b142c;border-radius:6px;display:flex;align-items:center;justify-content:center;margin-bottom:14px}
.map-badge{position:absolute;top:10px;left:10px}
.map-name{font-size:16px;color:#c6def3;margin:0 0 4px}
.map-id{font-size:12px;color:#6b91b4;margin:0 0 8px}
.map-desc{font-size:12px;color:#7d96ba;line-height:1.6;margin:0 0 14px;min-height:20px}
.mono{font-family:Consolas,monospace}
</style>
