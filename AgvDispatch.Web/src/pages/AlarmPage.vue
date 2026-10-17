<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { shortTime, alarmSeverity, alarmStatus, filterAlarms } from '../dashboard';
import type { Alarm } from '../types';
import { ElCard, ElTable, ElTableColumn, ElTag, ElButton, ElInput, ElSelect, ElOption, ElSpace, ElEmpty, ElIcon, ElDialog, ElDescriptions, ElDescriptionsItem, ElAlert } from 'element-plus';
import { ArrowLeft, Bell } from '@element-plus/icons-vue';

const store = useDispatchStore();
const router = useRouter();
const alarmSearch = ref('');
const alarmStateFilter = ref('all');
const alarmLevelFilter = ref('all');
const selectedAlarm = ref<Alarm | null>(null);
const dialogVisible = computed({ get: () => selectedAlarm.value !== null, set: (v: boolean) => { if (!v) selectedAlarm.value = null; } });

const visibleAlarms = computed(() => filterAlarms(store.alarms, alarmSearch.value, alarmStateFilter.value, alarmLevelFilter.value));

function severityType(s: string): 'success' | 'warning' | 'danger' | 'info' | 'primary' {
  return ({ Error: 'danger', Critical: 'danger', Warning: 'warning', Info: 'info' } as Record<string, 'danger' | 'warning' | 'info'>)[s] ?? 'info';
}
</script>

<template>
  <div class="page-container">
    <div class="page-header">
      <h2><ElIcon :size="22"><Bell /></ElIcon> 异常告警</h2>
      <ElTag type="danger" effect="plain" size="small" v-if="store.openAlarms.length">未确认 {{ store.openAlarms.length }}</ElTag>
      <ElTag type="info" effect="plain" size="small">已加载 {{ store.alarms.length }} 条</ElTag>
      <ElButton size="small" @click="router.push('/')"><ElIcon><ArrowLeft /></ElIcon>返回大屏</ElButton>
    </div>

    <ElCard shadow="never" class="filter-card">
      <ElSpace wrap>
        <ElInput v-model="alarmSearch" placeholder="告警信息、编号、车辆或任务" clearable style="width:260px" size="small" />
        <ElSelect v-model="alarmStateFilter" size="small" style="width:130px"><ElOption label="全部" value="all" /><ElOption label="未确认" value="Open" /><ElOption label="已确认" value="Acknowledged" /><ElOption label="已恢复" value="Recovered" /></ElSelect>
        <ElSelect v-model="alarmLevelFilter" size="small" style="width:130px"><ElOption label="全部" value="all" /><ElOption label="信息" value="Info" /><ElOption label="警告" value="Warning" /><ElOption label="错误" value="Error" /><ElOption label="严重" value="Critical" /></ElSelect>
        <ElButton size="small" @click="alarmSearch='';alarmStateFilter='all';alarmLevelFilter='all'">重置</ElButton>
      </ElSpace>
    </ElCard>

    <ElCard shadow="never" class="table-card">
      <ElTable :data="visibleAlarms" stripe style="width:100%" :header-cell-style="{ background:'#0a1525', color:'#8ea7cc' }" :cell-style="{ color:'#c0d0e8' }">
        <ElTableColumn label="操作" width="80"><template #default="{ row }"><ElButton type="primary" text size="small" @click="selectedAlarm=row as any">详情</ElButton></template></ElTableColumn>
        <ElTableColumn prop="alarmId" label="告警编号" width="140"><template #default="{ row }"><span class="mono text-cyan">{{ row.alarmId }}</span></template></ElTableColumn>
        <ElTableColumn prop="sourceId" label="关联对象" width="120" />
        <ElTableColumn label="级别" width="90"><template #default="{ row }"><ElTag :type="severityType(row.severity)" size="small" effect="dark">{{ alarmSeverity(row.severity) }}</ElTag></template></ElTableColumn>
        <ElTableColumn prop="title" label="告警信息" min-width="200" show-overflow-tooltip />
        <ElTableColumn label="状态" width="90"><template #default="{ row }">{{ alarmStatus(row.status) }}</template></ElTableColumn>
        <ElTableColumn label="首次发生" width="170"><template #default="{ row }">{{ shortTime(row.firstOccurredAt) }}</template></ElTableColumn>
        <ElTableColumn label="最近观察" width="170"><template #default="{ row }">{{ shortTime(row.lastObservedAt) }}</template></ElTableColumn>
        <ElTableColumn label="恢复时间" width="170"><template #default="{ row }">{{ shortTime(row.recoveredAt) }}</template></ElTableColumn>
      </ElTable>
      <ElEmpty v-if="!visibleAlarms.length" description="暂无匹配告警" />
      <div class="table-note">显示后台最近100条；告警确认、现场恢复仍沿用WPF操作。</div>
    </ElCard>
  </div>

  <ElDialog v-model="dialogVisible" title="告警详情" width="600px">
    <template v-if="selectedAlarm">
      <ElDescriptions :column="2" border size="small">
        <ElDescriptionsItem label="告警编号">{{ selectedAlarm.alarmId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="关联对象">{{ selectedAlarm.sourceId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="级别"><ElTag :type="severityType(selectedAlarm.severity)" size="small">{{ alarmSeverity(selectedAlarm.severity) }}</ElTag></ElDescriptionsItem>
        <ElDescriptionsItem label="状态">{{ alarmStatus(selectedAlarm.status) }}</ElDescriptionsItem>
        <ElDescriptionsItem label="代码" :span="2">{{ selectedAlarm.code }}</ElDescriptionsItem>
      </ElDescriptions>
      <h4 style="margin:16px 0 8px;color:#cee2f5">{{ selectedAlarm.title }}</h4>
      <p style="line-height:1.9;color:#afc6e3">{{ selectedAlarm.message }}</p>
      <p style="color:#8198b8;font-size:11px;margin-top:14px">首次：{{ shortTime(selectedAlarm.firstOccurredAt) }}<br>最近：{{ shortTime(selectedAlarm.lastObservedAt) }}<br>恢复：{{ shortTime(selectedAlarm.recoveredAt) }}</p>
      <ElAlert title="此处只查看，确认及恢复按现有WPF流程处理。" type="info" :closable="false" show-icon style="margin-top:14px" />
    </template>
  </ElDialog>
</template>

<style scoped>
.page-container{display:flex;flex-direction:column;gap:12px}
.page-header{display:flex;align-items:center;gap:14px;padding:4px 0}
.page-header h2{display:flex;align-items:center;gap:8px;font-size:18px;color:#d9eafb;margin:0}
.page-header .el-tag:last-of-type{margin-right:auto}
.filter-card{background:#0f1b30;border-color:#1e3352}.filter-card :deep(.el-card__body){padding:12px 16px}
.table-card{background:#0f1b30;border-color:#1e3352}.table-card :deep(.el-card__body){padding:0}
.table-card :deep(.el-table){background:#0f1b30;--el-table-border-color:#1e3352;--el-table-row-hover-bg-color:#142035;--el-table-header-bg-color:#0a1525}
.table-card :deep(.el-table tr){background:#0f1b30}
.table-card :deep(.el-table--striped .el-table__body tr.el-table__row--striped td.el-table__cell){background:#0b1628}
.table-note{padding:12px 16px;color:#718aad;font-size:11px}
.text-cyan{color:#67e8f9}.mono{font-family:Consolas,monospace}
</style>
