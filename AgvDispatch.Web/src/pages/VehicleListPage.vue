<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { vehicleCategory, categoryLabels, shortTime } from '../dashboard';
import { label } from '../state';
import { ElCard, ElTable, ElTableColumn, ElTag, ElButton, ElInput, ElSelect, ElOption, ElSpace, ElEmpty, ElIcon, ElProgress } from 'element-plus';
import { ArrowLeft, Van, Aim } from '@element-plus/icons-vue';

const store = useDispatchStore();
const router = useRouter();
const vehicleFilter = ref('all');
const vehicleSearch = ref('');

const filteredVehicles = computed(() => store.vehicles.filter(v =>
  (vehicleFilter.value === 'all' || vehicleCategory(v, store.allTasks, store.fresh) === vehicleFilter.value) &&
  `${v.vehicleId} ${v.displayName} ${v.currentStation ?? ''}`.toLowerCase().includes(vehicleSearch.value.toLowerCase())
));

function statusType(v: import('../types').Vehicle): 'success' | 'warning' | 'danger' | 'info' | 'primary' {
  const cat = vehicleCategory(v, store.allTasks, store.fresh);
  return ({ running: 'success', waiting: 'warning', fault: 'danger', offline: 'info', idle: 'primary', charging: 'primary' } as Record<string, 'success' | 'warning' | 'danger' | 'info' | 'primary'>)[cat] ?? 'info';
}
</script>

<template>
  <div class="page-container">
    <div class="page-header">
      <h2><ElIcon :size="22"><Van /></ElIcon> 车辆列表</h2>
      <ElTag type="info" effect="plain" size="small">共 {{ store.vehicles.length }} 辆</ElTag>
      <ElButton type="primary" size="small" @click="router.push({ name: 'planning', query: { tab: 'vehicles' } })">新增 / 配置车辆</ElButton>
      <ElButton size="small" @click="router.push('/')"><ElIcon><ArrowLeft /></ElIcon>返回大屏</ElButton>
    </div>

    <ElCard shadow="never" class="filter-card">
      <ElSpace wrap>
        <ElInput v-model="vehicleSearch" placeholder="编号、名称、当前位置" clearable style="width:240px" size="small" />
        <ElSelect v-model="vehicleFilter" placeholder="车辆状态" size="small" style="width:160px">
          <ElOption label="全部" value="all" />
          <ElOption v-for="stat in store.vehicleStats" :key="stat.key" :label="stat.label" :value="stat.key" />
        </ElSelect>
        <ElButton size="small" @click="vehicleSearch = ''; vehicleFilter = 'all'">重置</ElButton>
      </ElSpace>
    </ElCard>

    <ElCard shadow="never" class="table-card">
      <ElTable :data="filteredVehicles" stripe style="width:100%" :header-cell-style="{ background: '#0a1525', color: '#8ea7cc' }" :cell-style="{ color: '#c0d0e8' }">
        <ElTableColumn prop="vehicleId" label="车辆编号" width="120">
          <template #default="{ row }"><span class="mono text-cyan">{{ row.vehicleId }}</span></template>
        </ElTableColumn>
        <ElTableColumn prop="displayName" label="名称" width="120" />
        <ElTableColumn label="状态" width="110">
          <template #default="{ row }"><ElTag :type="statusType(row as any)" size="small" effect="dark">{{ categoryLabels[vehicleCategory(row as any, store.allTasks, store.fresh)] }}</ElTag></template>
        </ElTableColumn>
        <ElTableColumn label="当前站点" width="120"><template #default="{ row }">{{ row.currentStation ?? '—' }}</template></ElTableColumn>
        <ElTableColumn label="坐标 X / Y" width="150"><template #default="{ row }"><span class="mono">{{ row.x?.toFixed(2) ?? '—' }} / {{ row.y?.toFixed(2) ?? '—' }}</span></template></ElTableColumn>
        <ElTableColumn label="电量" width="140">
          <template #default="{ row }"><ElProgress :percentage="row.batteryPercent ?? 0" :stroke-width="6" :color="(row.batteryPercent ?? 0) > 20 ? '#67e8f9' : '#f56c6c'" :show-text="true" :format="() => `${row.batteryPercent ?? 0}%`" style="width:100px" /></template>
        </ElTableColumn>
        <ElTableColumn label="定位置信度" width="100"><template #default="{ row }">{{ row.localizationConfidence ?? '—' }}</template></ElTableColumn>
        <ElTableColumn label="数据质量" width="90"><template #default="{ row }">{{ label(row.quality) }}</template></ElTableColumn>
        <ElTableColumn label="最近采集" width="170"><template #default="{ row }">{{ shortTime(row.lastSuccessfulPollAt) }}</template></ElTableColumn>
        <ElTableColumn label="操作" width="100" fixed="right">
          <template #default="{ row }"><ElButton type="primary" text size="small" @click="router.push('/')"><ElIcon><Aim /></ElIcon>定位</ElButton></template>
        </ElTableColumn>
      </ElTable>
      <ElEmpty v-if="!filteredVehicles.length" description="没有匹配车辆" />
    </ElCard>
  </div>
</template>

<style scoped>
.page-container{display:flex;flex-direction:column;gap:12px}
.page-header{display:flex;align-items:center;gap:14px;padding:4px 0}
.page-header h2{display:flex;align-items:center;gap:8px;font-size:18px;color:#d9eafb;margin:0}
.page-header .el-tag{margin-right:auto}
.filter-card{background:#0f1b30;border-color:#1e3352}
.filter-card :deep(.el-card__body){padding:12px 16px}
.table-card{background:#0f1b30;border-color:#1e3352}
.table-card :deep(.el-card__body){padding:0}
.table-card :deep(.el-table){background:#0f1b30;--el-table-border-color:#1e3352;--el-table-row-hover-bg-color:#142035;--el-table-header-bg-color:#0a1525;color:#c0d0e8}
.table-card :deep(.el-table tr){background:#0f1b30}
.table-card :deep(.el-table--striped .el-table__body tr.el-table__row--striped td.el-table__cell){background:#0b1628}
.text-cyan{color:#67e8f9}.mono{font-family:Consolas,monospace}
</style>
