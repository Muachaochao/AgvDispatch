<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import TaskRecoveryPanel from '../TaskRecoveryPanel.vue';
import { label } from '../state';
const store = useDispatchStore();
const route = useRoute();
const taskId = ref('');
watch(() => route.query.taskId, value => { taskId.value = typeof value === 'string' ? value : ''; }, { immediate: true });
const selected = computed(() => taskId.value.trim());
const recentTasks = computed(() => [...store.allTasks]
  .sort((a, b) => Number(['Completed', 'Cancelled'].includes(a.stage)) - Number(['Completed', 'Cancelled'].includes(b.stage))
    || Date.parse(b.updatedAt) - Date.parse(a.updatedAt))
  .slice(0, 12));
</script>
<template>
  <section class="management-page">
    <header class="management-title"><h2>异常任务与启动恢复</h2><RouterLink to="/tasks">返回业务任务</RouterLink></header>
    <p>选择异常 → 核对现场必要事项 → 确认继续。系统保留原任务与核验记录。</p>
    <details><summary>查找其他任务／运行中核对</summary>
    <div class="recovery-task-picker">
      <label>从最近任务选择<select :value="taskId" @change="taskId=($event.target as HTMLSelectElement).value"><option value="">请选择任务</option><option v-for="task in recentTasks" :key="task.taskId" :value="task.taskId">{{task.stationId}} · {{task.vehicleId||'未派车'}} · {{label(task.stage)}} · {{task.taskId}}</option></select></label>
      <label>或输入原任务编号<input v-model="taskId" maxlength="128" placeholder="输入任务编号" /></label>
    </div>
    </details>
    <p v-if="store.demoMode" class="inline-warning">演示模式不访问真实恢复接口。</p>
    <TaskRecoveryPanel v-else :task-id="selected" :fresh="store.fresh" :now="store.now" />
  </section>
</template>

<style scoped>
.recovery-task-picker{display:grid;grid-template-columns:minmax(280px,1fr) minmax(280px,1fr);gap:14px;margin:14px 0}
.recovery-task-picker label{display:grid;gap:7px;color:#a9bfd6;font-size:12px}
.recovery-task-picker select,.recovery-task-picker input{min-width:0}
.recovery-flow{padding:11px 13px;border-left:3px solid #4bb7c8;background:#102235;line-height:1.8}
@media(max-width:760px){.recovery-task-picker{grid-template-columns:1fr}}
</style>
