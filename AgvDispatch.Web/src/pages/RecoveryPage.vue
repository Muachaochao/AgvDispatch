<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import TaskRecoveryPanel from '../TaskRecoveryPanel.vue';
import { label } from '../state';
import { shortTime } from '../dashboard';
import { IconListDetails, IconRefresh, IconSearch } from '@tabler/icons-vue';
const store = useDispatchStore();
const route = useRoute();
const taskId = ref('');
const recoverySearch = ref('');
watch(() => route.query.taskId, value => { taskId.value = typeof value === 'string' ? value : ''; }, { immediate: true });
const selected = computed(() => taskId.value.trim());
const recentTasks = computed(() => [...store.allTasks]
  .sort((a, b) => Number(['Completed', 'Cancelled'].includes(a.stage)) - Number(['Completed', 'Cancelled'].includes(b.stage))
    || Date.parse(b.updatedAt) - Date.parse(a.updatedAt))
  .slice(0, 12));
const filteredRecentTasks = computed(() => recentTasks.value.filter(task => !recoverySearch.value.trim()
  || `${task.taskId} ${task.stationId} ${task.vehicleId ?? ''} ${label(task.stage)}`.toLowerCase().includes(recoverySearch.value.trim().toLowerCase())));
const taskTagType = (stage:string) => stage === 'Faulted' ? 'danger' : ['Cancelled','CancellationPending','CancellationReview'].includes(stage) ? 'info' : ['Completed'].includes(stage) ? 'success' : 'warning';
function selectRecoveryTask(id:string){taskId.value=id;}
function selectRecoveryRow(row:{taskId:string}){selectRecoveryTask(row.taskId);}
</script>
<template>
  <section class="management-page recovery-page">
    <header class="ui-page-header">
      <div><h2>异常任务与启动恢复</h2><p>选择异常任务，核对现场事项后继续；系统保留原任务、操作依据与核验记录。</p></div>
      <div class="ui-page-actions"><el-button :icon="IconListDetails" @click="$router.push('/tasks')">业务任务</el-button><el-button type="primary" :icon="IconRefresh" @click="store.connectBackend()">刷新状态</el-button></div>
    </header>

    <el-alert v-if="store.demoMode" type="warning" :closable="false" show-icon>演示模式不访问真实恢复接口。</el-alert>
    <el-alert v-else-if="!store.fresh" type="warning" :closable="false" show-icon>后台数据非当前，恢复操作会保持只读，请等待连接恢复。</el-alert>

    <div class="recovery-layout">
      <section class="recovery-main">
        <header><div><h3>恢复工作区</h3><p>未选择任务时不会执行任何恢复动作。</p></div><el-tag :type="selected ? 'warning' : 'success'" effect="plain">{{ selected ? '已选择任务' : '等待选择' }}</el-tag></header>
        <TaskRecoveryPanel v-if="selected" :task-id="selected" :fresh="store.fresh" :now="store.now" />
        <el-empty v-else description="当前没有选中的异常任务。可从右侧最近任务中选择，或输入完整任务编号。" :image-size="96">
          <el-button @click="$router.push('/tasks')">前往业务任务</el-button>
        </el-empty>
      </section>

      <aside class="recovery-side">
        <section class="recovery-picker-card">
          <header><h3>查找任务</h3><span>{{filteredRecentTasks.length}} / {{recentTasks.length}}</span></header>
          <el-input v-model="recoverySearch" clearable :prefix-icon="IconSearch" placeholder="任务编号、工位或车辆"/>
          <el-input v-model="taskId" clearable maxlength="128" placeholder="输入完整任务编号后回车" @keydown.enter="taskId=taskId.trim()"/>
          <div class="recovery-table">
            <el-table :data="filteredRecentTasks" row-key="taskId" height="340" highlight-current-row @row-click="selectRecoveryRow">
              <el-table-column label="任务" min-width="220" show-overflow-tooltip><template #default="{row}"><span class="recovery-task-id">{{row.taskId}}</span><small>{{shortTime(row.updatedAt)}}</small></template></el-table-column>
              <el-table-column label="工位 / 车辆" width="138"><template #default="{row}">{{row.stationId}}<small>{{row.vehicleId??'待分配'}}</small></template></el-table-column>
              <el-table-column label="状态" width="96"><template #default="{row}"><el-tag :type="taskTagType(row.stage)" effect="plain">{{label(row.stage)}}</el-tag></template></el-table-column>
              <template #empty><el-empty description="没有匹配任务" :image-size="54"/></template>
            </el-table>
          </div>
        </section>
      </aside>
    </div>
  </section>
</template>

<style scoped>
.recovery-page{gap:12px}
.recovery-layout{display:grid;grid-template-columns:minmax(0,1.55fr) minmax(320px,.75fr);gap:12px;min-height:0;flex:1}
.recovery-main,.recovery-picker-card{border:1px solid var(--ui-line);border-radius:var(--ui-radius);background:var(--ui-surface);min-width:0;overflow:hidden}
.recovery-main>header,.recovery-picker-card>header{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:14px 16px;border-bottom:1px solid var(--ui-line);background:var(--ui-surface-raised)}
.recovery-main h3,.recovery-picker-card h3{margin:0;color:var(--ui-text);font-size:14px}
.recovery-main header p{margin-top:4px;color:var(--ui-text-3);font-size:11px}
.recovery-picker-card>header>span{color:var(--ui-text-3);font-size:11px}
.recovery-main :deep(.el-empty){min-height:430px}
.recovery-picker-card{display:flex;flex-direction:column;gap:10px;padding-bottom:12px}
.recovery-picker-card>.el-input{margin:0 12px;width:auto}
.recovery-table{margin:0 12px;min-height:0}
.recovery-table :deep(.el-table){--el-table-bg-color:var(--ui-surface);--el-table-tr-bg-color:var(--ui-surface);--el-table-header-bg-color:var(--ui-table-head);--el-table-border-color:var(--ui-table-line);--el-table-text-color:var(--ui-table-text);--el-table-header-text-color:var(--ui-text-2)}
.recovery-table :deep(.el-table__row){cursor:pointer}
.recovery-table small{display:block;margin-top:3px;color:var(--ui-text-3);font-size:10px;overflow-wrap:anywhere}
.recovery-task-id{display:block;color:var(--ui-text);font:11px/1.45 Consolas,"Microsoft YaHei",monospace;overflow-wrap:anywhere}
@media(max-width:980px){.recovery-layout{grid-template-columns:1fr}.recovery-main :deep(.el-empty){min-height:260px}}
</style>
