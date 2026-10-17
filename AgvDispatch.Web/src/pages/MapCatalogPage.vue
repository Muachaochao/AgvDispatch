<script setup lang="ts">
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { shortTime } from '../dashboard';
import type { MapSummary } from '../types';
import { IconMap, IconArrowLeft, IconRefresh, IconEye } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';

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
</script>

<template>
  <section class="management-page">
    <header class="management-title">
      <h2><IconMap :size="23" />地图查看与版本</h2>
      <span>共 {{ catalog.length }} 张</span>
      <button class="accent-button" @click="router.push({ name: 'planning', query: { tab: 'map' } })">地图 / 场景编辑</button>
      <button @click="router.push({ name: 'planning', query: { tab: 'plans' } })">方案管理</button>
      <button @click="loadCatalog"><IconRefresh :size="15" />刷新</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <div class="map-catalog-note">复用厂家建好的地图。本页查看运行版本；工程编辑中可加载完整草稿、编辑和发布，正式运行切换沿用WPF安全流程。</div>
    <p v-if="catalogError" class="inline-warning">{{ catalogError }}</p>
    <div class="map-catalog">
      <article v-for="map in catalog" :key="map.mapId" class="map-card">
        <div class="map-card-art"><IconMap :size="64" stroke="1" /><span class="outline-badge" :class="{ cyan: map.isActive }">{{ map.isActive ? '当前运行' : '未运行' }}</span><b>{{ map.mapId }}</b><small>{{ map.unit }} / {{ map.coordinateSystem }}</small></div>
        <h3>{{ map.name }}</h3>
        <p>{{ map.description || '未填写说明' }}</p>
        <dl><dt>运行版本</dt><dd>{{ map.activeVersionId ?? '—' }}</dd><dt>发布版本</dt><dd>{{ map.publishedVersionId ?? '—' }}</dd></dl>
        <footer>
          <button @click="selectedMap = map"><IconEye :size="14" />查看信息</button>
          <button v-if="map.isActive && map.mapId === store.snapshot?.map?.id" class="accent-button" @click="router.push('/')">查看运行图</button>
        </footer>
      </article>
      <p v-if="!catalog.length && !catalogError" class="empty-panel">没有可查看的地图</p>
    </div>
  </section>

  <ConsoleDialog v-if="selectedMap" title="地图版本信息" @close="selectedMap = null">
    <dl class="property-list map-properties">
      <dt>地图编号</dt><dd>{{ selectedMap.mapId }}</dd>
      <dt>地图名称</dt><dd>{{ selectedMap.name }}</dd>
      <dt>坐标系</dt><dd>{{ selectedMap.coordinateSystem }}</dd>
      <dt>单位</dt><dd>{{ selectedMap.unit }}</dd>
      <dt>运行版本</dt><dd>{{ selectedMap.activeVersionId ?? '—' }}</dd>
      <dt>发布版本</dt><dd>{{ selectedMap.publishedVersionId ?? '—' }}</dd>
      <dt>更新时间</dt><dd>{{ shortTime(selectedMap.updatedAt) }}</dd>
    </dl>
    <p class="muted-note">不在Web中修改或激活地图。</p>
  </ConsoleDialog>
</template>
