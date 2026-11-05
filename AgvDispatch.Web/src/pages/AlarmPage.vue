<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { shortTime, alarmSeverity, alarmStatus } from '../dashboard';
import { IconBell, IconArrowLeft, IconSearch, IconChevronLeft, IconChevronRight } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import type { Alarm, AlarmPage } from '../types';
import { alarmMessageText, alarmSourceLabel, alarmTitleText } from '../operator-labels';
import {
  alarmHistoryError,
  alarmHistoryUrl,
  alarmTimeRange,
  demoAlarmPage,
  emptyAlarmHistoryQuery,
  type AlarmHistoryQuery,
  type AlarmTimePreset
} from '../alarm-history';

const store = useDispatchStore();
const router = useRouter();

const search = ref('');
const statusFilter = ref('all');
const severityFilter = ref('all');
const occurredFrom = ref('');
const occurredTo = ref('');
const pageSize = ref(50);
const appliedQuery = ref<AlarmHistoryQuery>(emptyAlarmHistoryQuery());
const alarmPage = ref(1);
const queriedAlarms = ref<AlarmPage | null>(null);
const queryBusy = ref(false);
const queryError = ref('');
const selectedAlarm = ref<Alarm | null>(null);
let queryStarted = false;
let queryEpoch = 0;

const draftQuery = computed<AlarmHistoryQuery>(() => ({
  search: search.value,
  status: statusFilter.value,
  severity: severityFilter.value,
  from: occurredFrom.value,
  to: occurredTo.value,
  pageSize: pageSize.value
}));
const queryChanged = computed(() => JSON.stringify(draftQuery.value) !== JSON.stringify(appliedQuery.value));
const visibleAlarms = computed(() => queriedAlarms.value?.items ?? []);
const totalCount = computed(() => queriedAlarms.value?.totalCount ?? 0);
const pages = computed(() => Math.max(1, Math.ceil(totalCount.value / (queriedAlarms.value?.pageSize ?? appliedQuery.value.pageSize))));
const summary = computed(() => queriedAlarms.value?.summary ?? { total: 0, open: 0, acknowledged: 0, recovered: 0, critical: 0 });
const hasTimeFilter = computed(() => !!(appliedQuery.value.from || appliedQuery.value.to));
const appliedFilterText = computed(() => {
  const parts = [];
  if (appliedQuery.value.search) parts.push(`关键词“${appliedQuery.value.search}”`);
  if (appliedQuery.value.status !== 'all') parts.push(`状态：${alarmStatus(statusValue(appliedQuery.value.status))}`);
  if (appliedQuery.value.severity !== 'all') parts.push(`级别：${alarmSeverity(severityValue(appliedQuery.value.severity))}`);
  if (hasTimeFilter.value) parts.push(`首次发生：${shortTime(appliedQuery.value.from)} 至 ${shortTime(appliedQuery.value.to)}`);
  return parts.join(' · ');
});

watch(() => store.fresh, value => {
  if (value && !queryStarted) void queryAlarms(1, true);
});
onMounted(() => {
  if (store.fresh) void queryAlarms(1, true);
});
onUnmounted(() => {
  queryEpoch++;
});

function statusValue(value: string) {
  return ({ open: 'Open', acknowledged: 'Acknowledged', recovered: 'Recovered' } as Record<string, string>)[value] ?? value;
}

function severityValue(value: string) {
  return ({ info: 'Info', warning: 'Warning', error: 'Error', critical: 'Critical' } as Record<string, string>)[value] ?? value;
}

function statusTone(status: string) {
  return status === 'Open' ? 'open' : status === 'Acknowledged' ? 'acknowledged' : 'recovered';
}

function queryMessage(error: unknown) {
  return String(error).replace(/^Error:\s*/i, '');
}

async function queryAlarms(page = 1, apply = false) {
  if (queryBusy.value) return;
  const query = { ...(apply ? draftQuery.value : appliedQuery.value) };
  const invalid = alarmHistoryError(query);
  if (invalid) {
    queryError.value = invalid;
    return;
  }

  const mine = ++queryEpoch;
  queryStarted = true;
  queryBusy.value = true;
  queryError.value = '';
  try {
    let result = store.demoMode
      ? demoAlarmPage(store.alarms, query, page)
      : await store.api<AlarmPage>(alarmHistoryUrl(query, page));
    const lastPage = Math.max(1, Math.ceil(result.totalCount / result.pageSize));
    if (page > lastPage && mine === queryEpoch) {
      result = store.demoMode
        ? demoAlarmPage(store.alarms, query, lastPage)
        : await store.api<AlarmPage>(alarmHistoryUrl(query, lastPage));
    }
    if (mine === queryEpoch) {
      queriedAlarms.value = result;
      appliedQuery.value = query;
      alarmPage.value = result.page;
    }
  } catch (error) {
    if (mine === queryEpoch) queryError.value = queryMessage(error);
  } finally {
    if (mine === queryEpoch) queryBusy.value = false;
  }
}

function resetQuery() {
  if (queryBusy.value) return;
  search.value = '';
  statusFilter.value = 'all';
  severityFilter.value = 'all';
  occurredFrom.value = '';
  occurredTo.value = '';
  pageSize.value = 50;
  void queryAlarms(1, true);
}

function chooseTimeRange(preset: AlarmTimePreset) {
  if (queryBusy.value) return;
  const range = alarmTimeRange(preset);
  occurredFrom.value = range.from;
  occurredTo.value = range.to;
  void queryAlarms(1, true);
}
</script>

<template>
  <section class="management-page alarm-history-page">
    <header class="alarm-page-header">
      <div class="alarm-page-title">
        <IconBell :size="22" />
        <div><h2>异常告警</h2><small>历史记录、筛选与现场核对依据</small></div>
      </div>
      <div class="alarm-page-actions">
        <span>{{ queriedAlarms ? `匹配 ${totalCount} 条` : '等待查询' }}</span>
        <button class="quiet-button" @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
      </div>
    </header>

    <form class="alarm-query-toolbar" @submit.prevent="queryAlarms(1, true)">
      <label class="query-search">
        <IconSearch :size="14" />
        <input v-model="search" maxlength="128" placeholder="告警编号、对象、代码或内容" aria-label="搜索告警" :disabled="queryBusy" />
      </label>
      <select v-model="statusFilter" class="alarm-status-filter" aria-label="筛选告警状态" :disabled="queryBusy">
        <option value="all">全部状态</option>
        <option value="open">未确认</option>
        <option value="acknowledged">已确认</option>
        <option value="recovered">已恢复</option>
      </select>
      <select v-model="severityFilter" class="alarm-severity-filter" aria-label="筛选告警级别" :disabled="queryBusy">
        <option value="all">全部级别</option>
        <option value="info">信息</option>
        <option value="warning">警告</option>
        <option value="error">错误</option>
        <option value="critical">严重</option>
      </select>
      <div class="query-date-range">
        <input v-model="occurredFrom" type="datetime-local" aria-label="首次发生时间从" :disabled="queryBusy" />
        <span>至</span>
        <input v-model="occurredTo" type="datetime-local" aria-label="首次发生时间至" :disabled="queryBusy" />
      </div>
      <div class="history-presets" aria-label="快捷时间范围">
        <button v-for="item in ([['today', '今天'], ['yesterday', '昨天'], ['week', '近7天'], ['all', '全部']] as const)" :key="item[0]" type="button" :disabled="queryBusy || !store.fresh" @click="chooseTimeRange(item[0])">{{ item[1] }}</button>
      </div>
      <button type="submit" class="accent-button query-submit" :class="{ changed: queryChanged }" :disabled="queryBusy || !store.fresh">{{ queryBusy ? '查询中…' : queryChanged ? '应用筛选' : '查询' }}</button>
      <button type="button" class="quiet-button reset-submit" :disabled="queryBusy || !store.fresh" @click="resetQuery">重置</button>
    </form>

    <div class="alarm-summary-strip" aria-label="告警汇总">
      <div><span>匹配总数</span><b>{{ summary.total }}</b></div>
      <div class="open"><span>未确认</span><b>{{ summary.open }}</b></div>
      <div class="acknowledged"><span>已确认</span><b>{{ summary.acknowledged }}</b></div>
      <div class="recovered"><span>已恢复</span><b>{{ summary.recovered }}</b></div>
      <div class="critical"><span>严重</span><b>{{ summary.critical }}</b></div>
      <p>{{ hasTimeFilter ? '汇总按关键词和首次发生时间统计，不受状态、级别筛选影响。' : '汇总按关键词统计，不受状态、级别筛选影响。' }}</p>
    </div>

    <div v-if="appliedFilterText" class="active-filter-line"><b>当前条件</b><span>{{ appliedFilterText }}</span></div>
    <p v-if="queryError" class="inline-warning" role="alert">{{ queryError }}</p>

    <div class="management-table">
      <table class="alarm-table">
        <thead>
          <tr><th>操作</th><th>告警编号</th><th>关联对象</th><th>告警信息</th><th>级别</th><th>状态</th><th>首次发生</th><th>最近观察</th><th>恢复时间</th></tr>
        </thead>
        <tbody>
          <tr v-for="alarm in visibleAlarms" :key="alarm.alarmId">
            <td><button class="table-button" @click="selectedAlarm = alarm"><ActionIcon name="preview"/>详情</button></td>
            <td class="mono cyan alarm-id-cell" :title="alarm.alarmId">{{ alarm.alarmId }}</td>
            <td class="source-cell"><b>{{ alarm.sourceId }}</b><small>{{ alarmSourceLabel(alarm.sourceType) }}</small></td>
            <td class="alarm-message-cell"><b :title="alarmTitleText(alarm.title)">{{ alarmTitleText(alarm.title) }}</b><small :title="alarmMessageText(alarm.message)">{{ alarmMessageText(alarm.message) }}</small></td>
            <td><span class="severity" :class="alarm.severity.toLowerCase()">{{ alarmSeverity(alarm.severity) }}</span></td>
            <td><span class="alarm-state" :class="statusTone(alarm.status)">{{ alarmStatus(alarm.status) }}</span></td>
            <td class="time-cell">{{ shortTime(alarm.firstOccurredAt) }}</td>
            <td class="time-cell">{{ shortTime(alarm.lastObservedAt) }}</td>
            <td class="time-cell">{{ shortTime(alarm.recoveredAt) }}</td>
          </tr>
        </tbody>
      </table>
      <p v-if="!visibleAlarms.length" class="empty-panel">{{ queryBusy ? '正在查询告警记录…' : queryError && !queriedAlarms ? '读取失败，请重新查询。' : '当前条件下没有告警，可调整关键词、状态、级别或首次发生时间。' }}</p>
    </div>

    <footer class="alarm-result-footer">
      <div class="result-total"><span>告警总数</span><b>{{ totalCount }}</b></div>
      <div class="result-controls">
        <label class="page-size">每页<select v-model.number="pageSize" aria-label="每页告警条数" :disabled="queryBusy" @change="queryAlarms(1, true)"><option :value="20">20 条</option><option :value="50">50 条</option><option :value="100">100 条</option><option :value="200">200 条</option></select></label>
        <nav class="pager" aria-label="告警分页">
          <button :disabled="alarmPage <= 1 || queryBusy" aria-label="上一页" title="上一页" @click="queryAlarms(alarmPage - 1)"><IconChevronLeft :size="15" /></button>
          <span><b>{{ alarmPage }}</b><i>/</i>{{ pages }}</span>
          <button :disabled="alarmPage >= pages || queryBusy" aria-label="下一页" title="下一页" @click="queryAlarms(alarmPage + 1)"><IconChevronRight :size="15" /></button>
        </nav>
      </div>
    </footer>
  </section>

  <ConsoleDialog v-if="selectedAlarm" title="告警详情 · 整个生命周期记录" wide @close="selectedAlarm = null">
    <div class="alarm-detail-hero">
      <div>
        <span>{{ alarmSourceLabel(selectedAlarm.sourceType) }} · {{ selectedAlarm.sourceId }}</span>
        <h3>{{ alarmTitleText(selectedAlarm.title) }}</h3>
        <p>{{ alarmMessageText(selectedAlarm.message) }}</p>
      </div>
      <div class="alarm-detail-pills">
        <span class="severity" :class="selectedAlarm.severity.toLowerCase()">{{ alarmSeverity(selectedAlarm.severity) }}</span>
        <span class="alarm-state" :class="statusTone(selectedAlarm.status)">{{ alarmStatus(selectedAlarm.status) }}</span>
      </div>
    </div>

    <div class="detail-summary alarm-detail-summary">
      <div><span>告警编号</span><b>{{ selectedAlarm.alarmId }}</b></div>
      <div><span>规则键</span><b>{{ selectedAlarm.ruleKey || '—' }}</b></div>
      <div><span>告警代码</span><b>{{ selectedAlarm.code || '—' }}</b></div>
      <div><span>首次发生</span><b>{{ shortTime(selectedAlarm.firstOccurredAt) }}</b></div>
      <div><span>最近观察</span><b>{{ shortTime(selectedAlarm.lastObservedAt) }}</b></div>
      <div><span>恢复时间</span><b>{{ shortTime(selectedAlarm.recoveredAt) }}</b></div>
    </div>

    <section class="alarm-lifecycle">
      <div>
        <h4>确认记录</h4>
        <template v-if="selectedAlarm.acknowledgedAt || selectedAlarm.acknowledgedBy || selectedAlarm.acknowledgeComment">
          <dl>
            <dt>确认时间</dt><dd>{{ shortTime(selectedAlarm.acknowledgedAt) }}</dd>
            <dt>确认人</dt><dd>{{ selectedAlarm.acknowledgedBy || '—' }}</dd>
            <dt>确认说明</dt><dd>{{ selectedAlarm.acknowledgeComment || '—' }}</dd>
          </dl>
        </template>
        <p v-else class="empty-lifecycle">{{ selectedAlarm.status === 'Open' ? '尚未确认。' : '该告警当前没有确认记录。' }}</p>
      </div>
      <div>
        <h4>恢复记录</h4>
        <template v-if="selectedAlarm.recoveredAt || selectedAlarm.recoveryMessage">
          <dl>
            <dt>恢复时间</dt><dd>{{ shortTime(selectedAlarm.recoveredAt) }}</dd>
            <dt>恢复说明</dt><dd>{{ selectedAlarm.recoveryMessage || '—' }}</dd>
          </dl>
        </template>
        <p v-else class="empty-lifecycle">报警条件尚未恢复。</p>
      </div>
    </section>

    <details v-if="alarmTitleText(selectedAlarm.title) !== selectedAlarm.title || alarmMessageText(selectedAlarm.message) !== selectedAlarm.message" class="alarm-raw-message">
      <summary>查看厂家原始提示</summary>
      <p>{{ selectedAlarm.title }}</p>
      <p>{{ selectedAlarm.message }}</p>
    </details>

    <div class="inline-warning">此处为只读历史查询。告警确认及现场恢复仍按现有受控流程执行；查看详情不会解除任务、路权或车辆动作限制。</div>
  </ConsoleDialog>
</template>

<style scoped>
.alarm-history-page{gap:8px}
.alarm-page-header{display:flex;align-items:center;justify-content:space-between;gap:18px;min-height:52px;padding:2px 2px 7px;border-bottom:1px solid #243858}
.alarm-page-title{display:flex;align-items:center;gap:10px;color:#54cadf}
.alarm-page-title h2{color:#d9eafb;font-size:19px;letter-spacing:.8px}
.alarm-page-title small{display:block;margin-top:4px;color:#7089aa;font-size:10px}
.alarm-page-actions{display:flex;align-items:center;gap:8px}
.alarm-page-actions>span{margin-right:6px;color:#6f9fba;font-size:11px}
.quiet-button{border-color:transparent;background:transparent}
.alarm-query-toolbar{display:grid;grid-template-columns:minmax(220px,1.35fr) 112px 112px minmax(290px,1.25fr) auto auto auto;align-items:center;gap:7px;padding:8px 10px;border:1px solid #233a5d;border-radius:5px;background:#0e1932}
.alarm-query-toolbar input,.alarm-query-toolbar select,.alarm-query-toolbar button{min-height:31px;height:31px}
.query-search{display:flex;align-items:center;gap:7px;min-width:0;padding-left:9px;border:1px solid #2b4168;border-radius:3px;background:#0a1530;color:#667f9f}
.query-search input{width:100%;padding:4px 7px;border:0;background:transparent}
.query-date-range{display:flex;align-items:center;gap:6px;min-width:0}
.query-date-range input{width:100%;min-width:126px;padding:4px 7px;color-scheme:dark}
.query-date-range>span{color:#7088a8;font-size:10px}
.history-presets{display:flex;gap:2px}
.history-presets button{height:31px;min-height:31px;padding:4px 7px;border-color:transparent;background:transparent;color:#7f99ba;font-size:10px}
.history-presets button:hover{border-color:#2f6081;background:#132947}
.query-submit,.reset-submit{padding:4px 12px}
.query-submit.changed{color:#f0c46a;border-color:#7f6535;background:#342b20}
.alarm-summary-strip{display:grid;grid-template-columns:repeat(5,minmax(92px,120px)) minmax(180px,1fr);align-items:stretch;gap:7px;padding:0 1px}
.alarm-summary-strip>div{display:flex;align-items:baseline;justify-content:space-between;gap:8px;padding:8px 11px;border:1px solid #263d5e;border-radius:4px;background:#101d38}
.alarm-summary-strip span{color:#8299b9;font-size:10px}
.alarm-summary-strip b{color:#dcecff;font:600 17px Consolas,monospace}
.alarm-summary-strip .open b{color:#f0bd59}
.alarm-summary-strip .acknowledged b{color:#66cae4}
.alarm-summary-strip .recovered b{color:#4bd8a7}
.alarm-summary-strip .critical b{color:#f07998}
.alarm-summary-strip p{align-self:center;padding-left:4px;color:#637d9f;font-size:10px;line-height:1.6}
.active-filter-line{display:flex;align-items:center;gap:9px;min-width:0;padding:0 3px;color:#718baa;font-size:10px}
.active-filter-line b{flex-shrink:0;color:#76cce0;font-weight:500}
.active-filter-line span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.alarm-history-page>.inline-warning{margin:0}
.management-table{min-height:270px}
.alarm-table th:first-child,.alarm-table td:first-child{width:70px}
.alarm-id-cell{max-width:178px;overflow:hidden;text-overflow:ellipsis}
.source-cell{max-width:145px}
.source-cell b,.source-cell small{display:block;overflow:hidden;text-overflow:ellipsis}
.source-cell small{max-width:145px;margin-top:4px;color:#6f88aa;font-size:9px}
.alarm-message-cell{max-width:330px;white-space:normal}
.alarm-message-cell b,.alarm-message-cell small{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.alarm-message-cell b{font-weight:500;color:#c8d9ed}
.alarm-message-cell small{margin-top:5px;color:#7891b2;font-size:10px}
.time-cell{max-width:140px;font:10px Consolas,monospace;color:#8fa6c5}
.alarm-state{display:inline-flex;align-items:center;gap:6px;font-size:10px;white-space:nowrap}
.alarm-state:before{content:"";width:6px;height:6px;border-radius:50%;background:currentColor}
.alarm-state.open{color:#f0bd59}
.alarm-state.acknowledged{color:#61cbe3}
.alarm-state.recovered{color:#4bd8a7}
.alarm-result-footer{display:flex;align-items:center;justify-content:space-between;gap:9px;min-height:42px;padding:5px 2px 3px;color:#7890ae;font-size:10px}
.result-total{display:flex;align-items:baseline;gap:7px;color:#899fbd;font-size:11px}
.result-total b{color:#eaf6ff;font:600 15px Consolas,monospace}
.result-controls{display:flex;align-items:center;gap:10px}
.page-size{display:flex;align-items:center;gap:7px;color:#8ca4c2;font-size:11px}
.page-size select{min-height:30px;padding:3px 26px 3px 9px;border-color:#294665;background:#101e38}
.pager{display:flex;align-items:center;overflow:hidden;min-height:31px;border:1px solid #294562;border-radius:6px;background:#0c1930}
.pager button{width:32px;min-height:30px;padding:0;border:0;border-radius:0;background:transparent;color:#91b5d1}
.pager button:hover:not(:disabled){color:#e8f9ff;background:#173451}
.pager>span{display:flex;align-items:center;justify-content:center;gap:5px;min-width:58px;min-height:23px;padding:0 8px;border-right:1px solid #223a55;border-left:1px solid #223a55;color:#819ab8;font:11px Consolas,monospace}
.pager>span b{color:#58d4ea;font-size:12px}
.pager>span i{color:#526b89;font-style:normal}
.alarm-detail-hero{display:flex;align-items:flex-start;justify-content:space-between;gap:24px;padding:18px 20px;border:1px solid #2b4f6c;border-radius:5px;background:#112741}
.alarm-detail-hero>div:first-child{min-width:0}
.alarm-detail-hero span{color:#77a8c6;font-size:10px}
.alarm-detail-hero h3{margin:8px 0 10px;color:#dcecff;font-size:17px;overflow-wrap:anywhere}
.alarm-detail-hero p{color:#a9c1dc;font-size:12px;line-height:1.8;overflow-wrap:anywhere}
.alarm-detail-pills{display:flex;align-items:center;gap:8px;flex-shrink:0}
.alarm-detail-pills .severity,.alarm-detail-pills .alarm-state{padding:5px 9px;border:1px solid #31506f;border-radius:12px;background:#0d1a31}
.alarm-detail-summary{margin-top:22px}
.alarm-detail-summary b{overflow-wrap:anywhere}
.alarm-lifecycle{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin-bottom:17px}
.alarm-lifecycle>div{padding:16px;border:1px solid #293f5e;border-radius:4px;background:#0f1c34}
.alarm-lifecycle h4{margin:0 0 12px;color:#78cfe1;font-size:12px}
.alarm-lifecycle dl{display:grid;grid-template-columns:72px minmax(0,1fr);gap:9px 12px;margin:0;font-size:11px}
.alarm-lifecycle dt{color:#758dac}
.alarm-lifecycle dd{margin:0;color:#b7cbe1;overflow-wrap:anywhere}
.empty-lifecycle{color:#738aa9;font-size:11px}
.alarm-raw-message{margin:0 0 17px;padding:12px 14px;border:1px solid #2b405e;border-radius:4px;background:#0d192d;color:#8fa8c5;font-size:11px;line-height:1.7}
.alarm-raw-message summary{cursor:pointer;color:#78cfe1}
.alarm-raw-message p{margin:7px 0 0;overflow-wrap:anywhere}
@media(max-width:1450px){
  .alarm-query-toolbar{grid-template-columns:minmax(200px,1fr) 110px 110px minmax(270px,1.2fr) auto auto}
  .history-presets{grid-column:1/-1;justify-self:end}
  .reset-submit{display:none}
  .alarm-summary-strip{grid-template-columns:repeat(5,minmax(85px,1fr))}
  .alarm-summary-strip p{display:none}
}
@media(max-width:900px){
  .alarm-page-header{align-items:flex-start;flex-wrap:wrap}
  .alarm-page-actions{width:100%;justify-content:flex-end}
  .alarm-query-toolbar{grid-template-columns:1fr 1fr}
  .query-search,.query-date-range,.history-presets{grid-column:1/-1}
  .history-presets{justify-self:start}
  .query-submit{grid-column:2}
  .alarm-summary-strip{grid-template-columns:repeat(3,1fr)}
  .alarm-summary-strip>div{padding:7px 9px}
}
@media(max-width:600px){
  .alarm-summary-strip{grid-template-columns:repeat(2,1fr)}
  .alarm-result-footer{align-items:flex-start;flex-direction:column}
  .result-controls{justify-content:space-between;width:100%}
  .alarm-lifecycle{grid-template-columns:1fr}
  .alarm-detail-hero{flex-direction:column}
}
</style>
