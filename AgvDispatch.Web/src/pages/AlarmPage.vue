<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, ref } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { shortTime, alarmSeverity, alarmStatus, filterAlarms } from '../dashboard';
import { IconBell, IconArrowLeft } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import type { Alarm } from '../types';

const store = useDispatchStore();
const router = useRouter();
const alarmSearch = ref('');
const alarmStateFilter = ref('all');
const alarmLevelFilter = ref('all');
const selectedAlarm = ref<Alarm | null>(null);

const visibleAlarms = computed(() => filterAlarms(store.alarms, alarmSearch.value, alarmStateFilter.value, alarmLevelFilter.value));
</script>

<template>
  <section class="management-page">
    <header class="management-title">
      <h2><IconBell :size="23" />异常告警</h2>
      <span>未确认 {{ store.openAlarms.length }} 条 / 已加载 {{ store.alarms.length }} 条</span>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <div class="query-bar">
      <label>告警 / 对象<input v-model="alarmSearch" aria-label="搜索告警" placeholder="告警信息、编号、车辆或任务" /></label>
      <label>状态<select v-model="alarmStateFilter" aria-label="筛选告警状态"><option value="all">全部</option><option value="Open">未确认</option><option value="Acknowledged">已确认</option><option value="Recovered">已恢复</option></select></label>
      <label>级别<select v-model="alarmLevelFilter" aria-label="筛选告警级别"><option value="all">全部</option><option value="Info">信息</option><option value="Warning">警告</option><option value="Error">错误</option><option value="Critical">严重</option></select></label>
      <button @click="alarmSearch = ''; alarmStateFilter = 'all'; alarmLevelFilter = 'all'"><ActionIcon name="refresh"/>重置</button>
    </div>
    <div class="management-table">
      <table>
        <thead><tr><th>操作</th><th>告警编号</th><th>关联对象</th><th>级别</th><th>告警信息</th><th>状态</th><th>首次发生</th><th>最近观察</th><th>恢复时间</th></tr></thead>
        <tbody>
          <tr v-for="alarm in visibleAlarms" :key="alarm.alarmId">
            <td><button class="table-button" @click="selectedAlarm = alarm"><ActionIcon name="preview"/>详情</button></td>
            <td class="mono cyan">{{ alarm.alarmId }}</td>
            <td>{{ alarm.sourceId }}</td>
            <td><span class="severity" :class="alarm.severity.toLowerCase()">{{ alarmSeverity(alarm.severity) }}</span></td>
            <td>{{ alarm.title }}</td>
            <td>{{ alarmStatus(alarm.status) }}</td>
            <td>{{ shortTime(alarm.firstOccurredAt) }}</td>
            <td>{{ shortTime(alarm.lastObservedAt) }}</td>
            <td>{{ shortTime(alarm.recoveredAt) }}</td>
          </tr>
        </tbody>
      </table>
      <p v-if="!visibleAlarms.length" class="empty-panel">暂无匹配告警</p>
    </div>
    <footer class="pagination"><span>显示后台最近100条；告警确认、现场恢复仍沿用WPF操作。</span></footer>
  </section>

  <ConsoleDialog v-if="selectedAlarm" title="告警详情" @close="selectedAlarm = null">
    <div class="detail-summary">
      <div><span>告警编号</span><b>{{ selectedAlarm.alarmId }}</b></div>
      <div><span>关联对象</span><b>{{ selectedAlarm.sourceId }}</b></div>
      <div><span>级别 / 状态</span><b>{{ alarmSeverity(selectedAlarm.severity) }} / {{ alarmStatus(selectedAlarm.status) }}</b></div>
      <div><span>代码</span><b>{{ selectedAlarm.code }}</b></div>
    </div>
    <h3 class="subheading">{{ selectedAlarm.title }}</h3>
    <p class="long-message">{{ selectedAlarm.message }}</p>
    <p class="muted-note">首次发生：{{ shortTime(selectedAlarm.firstOccurredAt) }}<br>最近观察：{{ shortTime(selectedAlarm.lastObservedAt) }}<br>恢复时间：{{ shortTime(selectedAlarm.recoveredAt) }}</p>
    <div class="inline-warning">此处只查看，确认及恢复按现有WPF流程处理。</div>
  </ConsoleDialog>
</template>
