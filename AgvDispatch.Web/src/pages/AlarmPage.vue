<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { shortTime, alarmSeverity, alarmStatus } from '../dashboard';
import { IconBell, IconArrowLeft, IconSearch } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import type { Alarm, AlarmPage } from '../types';
import { alarmMessageText, alarmSourceLabel, alarmTitleText } from '../operator-labels';
import { alarmAcknowledgeError, alarmAcknowledgeRequest } from '../alarm-actions';
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
const acknowledgeTarget = ref<Alarm | null>(null);
const acknowledgeComment = ref('');
const acknowledgeConfirmed = ref(false);
const acknowledgeBusy = ref(false);
const acknowledgeError = ref('');
const acknowledgeNotice = ref('');
const acknowledgeUncertain = ref(false);
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
const summary = computed(() => queriedAlarms.value?.summary ?? { total: 0, open: 0, acknowledged: 0, recovered: 0, critical: 0 });
const acknowledgeValidation = computed(() => alarmAcknowledgeError(acknowledgeComment.value, acknowledgeConfirmed.value));
const acknowledgeCanSubmit = computed(() => !!acknowledgeTarget.value && !acknowledgeBusy.value &&
  !acknowledgeUncertain.value && !acknowledgeValidation.value);
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
function changeAlarmPageSize(size: number) {
  pageSize.value = size;
  void queryAlarms(1, true);
}

function beginAcknowledge() {
  const alarm = selectedAlarm.value;
  if (!alarm || alarm.status !== 'Open' || !store.alarmActionsEnabled || acknowledgeBusy.value) return;
  acknowledgeTarget.value = alarm;
  acknowledgeComment.value = '';
  acknowledgeConfirmed.value = false;
  acknowledgeError.value = '';
  acknowledgeNotice.value = '';
  acknowledgeUncertain.value = false;
  selectedAlarm.value = null;
}

function closeAcknowledge() {
  if (acknowledgeBusy.value) return;
  acknowledgeTarget.value = null;
  acknowledgeComment.value = '';
  acknowledgeConfirmed.value = false;
  acknowledgeError.value = '';
  acknowledgeUncertain.value = false;
}

async function acknowledgeAlarm() {
  const target = acknowledgeTarget.value;
  const invalid = alarmAcknowledgeError(acknowledgeComment.value, acknowledgeConfirmed.value);
  if (!target || acknowledgeBusy.value || acknowledgeUncertain.value || invalid) {
    acknowledgeError.value = invalid;
    return;
  }

  acknowledgeBusy.value = true;
  acknowledgeError.value = '';
  acknowledgeNotice.value = '';
  try {
    const result = await store.api<{ accepted: boolean; message: string }>(
      `/api/alarms/${encodeURIComponent(target.alarmId)}/acknowledge`,
      alarmAcknowledgeRequest(target.alarmId, acknowledgeComment.value)
    );
    acknowledgeNotice.value = result.message;
    if (!result.accepted) acknowledgeError.value = result.message;
    await queryAlarms(alarmPage.value, false);
    if (result.accepted) {
      acknowledgeTarget.value = null;
      acknowledgeComment.value = '';
      acknowledgeConfirmed.value = false;
    }
  } catch (error) {
    acknowledgeUncertain.value = true;
    acknowledgeError.value = `确认结果未确定：${queryMessage(error)}。正在回读告警状态；不会自动重发。`;
    await queryAlarms(alarmPage.value, false);
  } finally {
    acknowledgeBusy.value = false;
  }
}

async function closeAcknowledgeAndReload() {
  acknowledgeUncertain.value = false;
  closeAcknowledge();
  await queryAlarms(alarmPage.value, false);
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
      <el-input v-model="search" class="query-search" maxlength="128" clearable placeholder="告警编号、对象、代码或内容" aria-label="搜索告警" :disabled="queryBusy" :prefix-icon="IconSearch" />
      <el-select v-model="statusFilter" class="alarm-status-filter" aria-label="筛选告警状态" :disabled="queryBusy"><el-option label="全部状态" value="all" /><el-option label="未确认" value="open" /><el-option label="已确认" value="acknowledged" /><el-option label="已恢复" value="recovered" /></el-select>
      <el-select v-model="severityFilter" class="alarm-severity-filter" aria-label="筛选告警级别" :disabled="queryBusy"><el-option label="全部级别" value="all" /><el-option label="信息" value="info" /><el-option label="警告" value="warning" /><el-option label="错误" value="error" /><el-option label="严重" value="critical" /></el-select>
      <div class="query-date-range">
        <el-date-picker v-model="occurredFrom" type="datetime" value-format="YYYY-MM-DDTHH:mm" format="YYYY-MM-DD HH:mm" placeholder="首次发生从" aria-label="首次发生时间从" :disabled="queryBusy" />
        <span>至</span>
        <el-date-picker v-model="occurredTo" type="datetime" value-format="YYYY-MM-DDTHH:mm" format="YYYY-MM-DD HH:mm" placeholder="首次发生至" aria-label="首次发生时间至" :disabled="queryBusy" />
      </div>
      <div class="history-presets" aria-label="快捷时间范围">
        <button v-for="item in ([['today', '今天'], ['yesterday', '昨天'], ['week', '近7天'], ['all', '全部']] as const)" :key="item[0]" type="button" :disabled="queryBusy || !store.fresh" @click="chooseTimeRange(item[0])">{{ item[1] }}</button>
      </div>
      <el-button native-type="submit" type="primary" class="query-submit" :class="{ changed: queryChanged }" :disabled="queryBusy || !store.fresh">{{ queryBusy ? '查询中…' : queryChanged ? '应用筛选' : '查询' }}</el-button>
      <el-button class="reset-submit" :disabled="queryBusy || !store.fresh" @click="resetQuery">重置</el-button>
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
    <p v-if="acknowledgeNotice" class="inline-success" role="status">{{ acknowledgeNotice }}</p>

    <div class="management-table">
      <el-table :data="visibleAlarms" row-key="alarmId" stripe height="100%" table-layout="fixed" class="business-table alarm-table">
        <el-table-column label="操作" width="82" fixed="left"><template #default="{ row }"><el-button link type="primary" @click="selectedAlarm = row"><ActionIcon name="preview"/>详情</el-button></template></el-table-column>
        <el-table-column label="告警编号" min-width="230" show-overflow-tooltip><template #default="{ row }"><span class="table-mono">{{ row.alarmId }}</span></template></el-table-column>
        <el-table-column label="关联对象" min-width="150"><template #default="{ row }"><b class="table-strong">{{ row.sourceId }}</b><small class="table-subline">{{ alarmSourceLabel(row.sourceType) }}</small></template></el-table-column>
        <el-table-column label="告警信息" min-width="360" show-overflow-tooltip><template #default="{ row }"><b class="table-strong">{{ alarmTitleText(row.title) }}</b><small class="table-subline">{{ alarmMessageText(row.message) }}</small></template></el-table-column>
        <el-table-column label="级别" width="90"><template #default="{ row }"><span class="severity" :class="row.severity.toLowerCase()">{{ alarmSeverity(row.severity) }}</span></template></el-table-column>
        <el-table-column label="状态" width="105"><template #default="{ row }"><span class="alarm-state" :class="statusTone(row.status)">{{ alarmStatus(row.status) }}</span></template></el-table-column>
        <el-table-column label="首次发生" width="155"><template #default="{ row }">{{ shortTime(row.firstOccurredAt) }}</template></el-table-column>
        <el-table-column label="最近观察" width="155"><template #default="{ row }">{{ shortTime(row.lastObservedAt) }}</template></el-table-column>
        <el-table-column label="恢复时间" width="155"><template #default="{ row }">{{ shortTime(row.recoveredAt) }}</template></el-table-column>
        <template #empty><div class="table-empty">{{ queryBusy ? '正在查询告警记录…' : queryError && !queriedAlarms ? '读取失败，请重新查询。' : '当前条件下没有告警，可调整关键词、状态、级别或首次发生时间。' }}</div></template>
      </el-table>
    </div>

    <footer class="alarm-result-footer">
      <div class="result-total"><span>告警总数</span><b>{{ totalCount }}</b></div>
      <div class="result-controls">
        <el-pagination background :current-page="alarmPage" :page-size="appliedQuery.pageSize" :page-sizes="[20, 50, 100, 200]" :total="totalCount" layout="sizes, prev, pager, next" @current-change="queryAlarms" @size-change="changeAlarmPageSize" />
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

    <div class="inline-warning">查看详情不会解除任务、路权或车辆动作限制。确认告警只记录现场已接手核对，不会清除告警、恢复故障或代替异常恢复流程。</div>
    <template #footer>
      <div class="dialog-footer-actions">
        <span v-if="selectedAlarm.status === 'Open' && !store.alarmActionsEnabled">Web告警确认未启用，当前只能只读查看。</span>
        <button @click="selectedAlarm = null">关闭</button>
        <button v-if="selectedAlarm.status === 'Open'" class="acknowledge-primary" :disabled="!store.alarmActionsEnabled" @click="beginAcknowledge">确认告警…</button>
      </div>
    </template>
  </ConsoleDialog>

  <ConsoleDialog v-if="acknowledgeTarget" title="确认告警 · 核对操作边界" @close="closeAcknowledge">
    <div class="ack-target">
      <span>{{ alarmSourceLabel(acknowledgeTarget.sourceType) }} · {{ acknowledgeTarget.sourceId }}</span>
      <h3>{{ alarmTitleText(acknowledgeTarget.title) }}</h3>
      <dl>
        <div><dt>告警编号</dt><dd>{{ acknowledgeTarget.alarmId }}</dd></div>
        <div><dt>级别</dt><dd><span class="severity" :class="acknowledgeTarget.severity.toLowerCase()">{{ alarmSeverity(acknowledgeTarget.severity) }}</span></dd></div>
        <div><dt>最近观察</dt><dd>{{ shortTime(acknowledgeTarget.lastObservedAt) }}</dd></div>
      </dl>
    </div>

    <div class="ack-boundary">
      <strong>这次操作只会把告警标记为“已确认”。</strong>
      <ul>
        <li>不会清除告警，也不会证明报警条件已经恢复。</li>
        <li>不会解除任务、路权、PLC、设备或车辆动作限制。</li>
        <li>不会代替异常恢复、单车导航调试或现场急停。</li>
      </ul>
    </div>

    <label class="ack-comment">
      <span>现场核对说明</span>
      <textarea v-model="acknowledgeComment" maxlength="1000" :disabled="acknowledgeBusy || acknowledgeUncertain"
        placeholder="例如：已联系现场确认车辆受阻，正在按原任务核对处置" />
      <small>{{ acknowledgeComment.trim().length }}/1000 · 至少2个字符</small>
    </label>
    <label class="ack-check">
      <input v-model="acknowledgeConfirmed" type="checkbox" :disabled="acknowledgeBusy || acknowledgeUncertain" />
      <span>我已核对当前告警对象，理解“确认告警”不等于恢复条件或解除原任务责任</span>
    </label>
    <p v-if="acknowledgeValidation && acknowledgeConfirmed" class="inline-warning">{{ acknowledgeValidation }}</p>
    <p v-if="acknowledgeError" class="inline-warning" role="alert">{{ acknowledgeError }}</p>
    <p v-if="acknowledgeUncertain" class="ack-uncertain">为避免重复写入，本次申请已冻结。关闭窗口并回读告警状态；只有重新核对待确认告警后才能再次发起。</p>

    <template #footer>
      <div class="dialog-footer-actions">
        <button :disabled="acknowledgeBusy" @click="closeAcknowledge">取消</button>
        <button v-if="acknowledgeUncertain" @click="closeAcknowledgeAndReload">关闭并回读状态</button>
        <button v-else class="acknowledge-primary" :disabled="!acknowledgeCanSubmit" @click="acknowledgeAlarm">
          {{ acknowledgeBusy ? '正在确认…' : '确认当前告警' }}
        </button>
      </div>
    </template>
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
.dialog-footer-actions{display:flex;align-items:center;justify-content:flex-end;gap:8px;width:100%}
.dialog-footer-actions>span{margin-right:auto;color:var(--ui-text-3);font-size:11px}
.acknowledge-primary{border-color:color-mix(in srgb,var(--ui-warning) 55%,var(--ui-line))!important;background:color-mix(in srgb,var(--ui-warning) 14%,var(--ui-surface-raised))!important;color:var(--ui-text)!important}
.ack-target{padding:15px 16px;border:1px solid var(--ui-line);border-radius:8px;background:color-mix(in srgb,var(--ui-warning) 6%,var(--ui-surface-raised))}
.ack-target>span{color:var(--ui-text-3);font-size:11px}
.ack-target h3{margin:7px 0 13px;color:var(--ui-text);font-size:17px}
.ack-target dl{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin:0}
.ack-target dl>div{min-width:0;padding:8px 10px;border-radius:6px;background:var(--ui-surface-soft)}
.ack-target dt{color:var(--ui-text-3);font-size:10px}
.ack-target dd{margin:4px 0 0;color:var(--ui-text-2);font:11px Consolas,monospace;overflow-wrap:anywhere}
.ack-boundary{margin-top:12px;padding:12px 14px;border:1px solid color-mix(in srgb,var(--ui-warning) 36%,var(--ui-line));border-radius:7px;background:color-mix(in srgb,var(--ui-warning) 7%,var(--ui-surface-raised))}
.ack-boundary strong{color:var(--ui-text)}
.ack-boundary ul{margin:7px 0 0;padding-left:18px;color:var(--ui-text-2);font-size:11px;line-height:1.8}
.ack-comment{display:flex;flex-direction:column;gap:6px;margin-top:12px;color:var(--ui-text-2);font-size:11px}
.ack-comment textarea{min-height:92px;resize:vertical;padding:9px 10px;border:1px solid var(--ui-line-strong);border-radius:6px;background:var(--ui-field);color:var(--ui-text);font:12px/1.65 inherit}
.ack-comment small{align-self:flex-end;color:var(--ui-text-3)}
.ack-check{display:flex;align-items:flex-start;gap:8px;margin-top:10px;color:var(--ui-text-2);font-size:11px;line-height:1.6}
.ack-check input{margin-top:2px}
.ack-uncertain{margin:8px 0 0;color:var(--ui-warning);font-size:11px;line-height:1.7}
.inline-success{margin:0;padding:7px 10px;border:1px solid color-mix(in srgb,var(--ui-success) 38%,var(--ui-line));border-radius:6px;background:color-mix(in srgb,var(--ui-success) 7%,var(--ui-surface));color:var(--ui-text-2);font-size:11px}
@media(max-width:1450px){
  .alarm-query-toolbar{grid-template-columns:minmax(180px,1fr) 106px 106px minmax(290px,1fr) 150px 90px}
  .history-presets{grid-column:5;grid-row:1;justify-self:end}
  .query-submit{grid-column:6;grid-row:1;width:90px;padding:4px 10px}
  .reset-submit{display:none}
  .alarm-summary-strip{grid-template-columns:repeat(5,minmax(85px,1fr))}
  .alarm-summary-strip p{display:none}
}
@media(max-width:900px){
  .alarm-page-header{align-items:flex-start;flex-wrap:wrap}
  .alarm-page-actions{width:100%;justify-content:flex-end}
  .alarm-query-toolbar{grid-template-columns:1fr 1fr}
  .query-search,.query-date-range,.history-presets{grid-column:1/-1}
  .history-presets{grid-row:auto;justify-self:start}
  .query-submit{grid-column:2;grid-row:auto;width:auto}
  .alarm-summary-strip{grid-template-columns:repeat(3,1fr)}
  .alarm-summary-strip>div{padding:7px 9px}
}
@media(max-width:600px){
  .alarm-history-page{padding-bottom:76px}
  .alarm-summary-strip{grid-template-columns:repeat(2,1fr)}
  .alarm-result-footer{align-items:flex-start;flex-direction:column}
  .result-controls{justify-content:space-between;width:100%;min-width:0;overflow-x:auto}
  .result-controls .el-pagination{max-width:100%;overflow-x:auto}
  .alarm-lifecycle{grid-template-columns:1fr}
  .alarm-detail-hero{flex-direction:column}
  .ack-target dl{grid-template-columns:1fr}
  .dialog-footer-actions{align-items:stretch;flex-direction:column}
  .dialog-footer-actions>span{margin:0 0 2px}
  .dialog-footer-actions button{width:100%}
}
.history-presets button{color:var(--ui-text-2)}
.alarm-detail-hero{border-color:var(--ui-line);background:color-mix(in srgb,var(--ui-primary) 8%,var(--ui-surface-raised))}
.alarm-detail-hero span{color:var(--ui-text-3)}
.alarm-detail-hero h3{color:var(--ui-text)}
.alarm-detail-hero p{color:var(--ui-text-2)}
.alarm-detail-pills .severity,.alarm-detail-pills .alarm-state{border-color:var(--ui-line-strong);background:var(--ui-surface-soft)}
.alarm-detail-summary span{color:var(--ui-text-3)}
.alarm-detail-summary b{color:var(--ui-text)}
.alarm-lifecycle>div{border-color:var(--ui-line);background:var(--ui-surface-raised)}
.alarm-lifecycle h4{color:var(--ui-primary-strong)}
.alarm-lifecycle dt{color:var(--ui-text-3)}
.alarm-lifecycle dd{color:var(--ui-text-2)}
.empty-lifecycle{color:var(--ui-text-3)}
.alarm-raw-message{border-color:var(--ui-line);background:var(--ui-surface-soft);color:var(--ui-text-2)}
.alarm-raw-message summary{color:var(--ui-primary-strong)}
</style>
