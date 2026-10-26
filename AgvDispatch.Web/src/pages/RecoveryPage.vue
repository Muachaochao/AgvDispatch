<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import TaskRecoveryPanel from '../TaskRecoveryPanel.vue';
const store = useDispatchStore();
const route = useRoute();
const taskId = ref('');
watch(() => route.query.taskId, value => { taskId.value = typeof value === 'string' ? value : ''; }, { immediate: true });
const selected = computed(() => taskId.value.trim());
</script>
<template>
  <section class="management-page">
    <header class="management-title"><h2>异常任务与启动恢复</h2><RouterLink to="/tasks">返回业务任务</RouterLink></header>
    <p>先读取当前批次，核对原任务、车端动作、载荷和 PLC 周期，再提交处置并请求放行。放行恢复批次不等于结束业务运行 Hold；业务运行还需核对收尾。</p>
    <p>取消原任务请进入任务详情申请。停止新增呼叫、取消车辆任务、现场恢复核对是三个独立操作。</p>
    <label>原任务编号（已有恢复批次可直接选择条目）<input v-model="taskId" maxlength="128" placeholder="TASK-…" /></label>
    <p v-if="store.demoMode" class="inline-warning">演示模式不访问真实恢复接口。</p>
    <TaskRecoveryPanel v-else :task-id="selected" :fresh="store.fresh" :now="store.now" />
  </section>
</template>
