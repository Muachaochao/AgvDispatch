<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import { chargerMonitoringState } from '../equipment-status';
import type { ChargingSession as Session, ChargingStatus as Status } from '../equipment-status';

const status = ref<Status>();
const error = ref(''); const loadError = ref(''); const message = ref(''); const pending = ref(false);
const monitoring = computed(() => chargerMonitoringState(status.value, loadError.value));
const selected = ref<Session>(); const action = ref('stop'); const reason = ref('');
const vehicleConfirmed = ref(false); const chargerConfirmed = ref(false);
let timer: ReturnType<typeof setInterval> | undefined;
let disposed = false;
const phases: Record<string, string> = { Queued: '排队', WaitingForBusiness: '等待业务安全结束', Reserved: '已预留充电位',
  SendingToCharger: '前往命令待确认', GoingToCharger: '前往充电位', StartingCharge: '启动充电待确认', AwaitingCurrent: '等待充电电流',
  Charging: '充电中', StoppingCharge: '停止充电待确认', AwaitingStop: '等待停止确认', SendingExit: '退出命令待确认', Exiting: '退出充电位', Hold: '暂停待核对', Completed: '已结束' };
const when = (v: string) => new Date(v).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false });
async function load() {
  try {
    const response = await fetch('/api/charging', { cache: 'no-store' }); const data = await response.json();
    if (!response.ok) throw new Error(data.detail ?? data.error ?? '无法读取充电状态。');
    if (!disposed) { status.value = data; loadError.value = ''; }
  } catch (e) { if (!disposed) loadError.value = e instanceof Error ? e.message : String(e); }
}
function open(row: Session, kind: string) {
  selected.value = row; action.value = kind; reason.value = ''; error.value = ''; vehicleConfirmed.value = false; chargerConfirmed.value = false;
}
async function submit() {
  if (!selected.value || pending.value) return;
  pending.value = true; error.value = ''; message.value = '';
  try {
    const response = await fetch(action.value === 'stop' ? '/api/charging/stop' : '/api/charging/resolve', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId: selected.value.sessionId, reason: reason.value.trim(),
        decision: action.value === 'continue' ? 'ContinueOriginal' : 'CloseAfterPhysicalExit',
        vehicleAndLoadConfirmed: vehicleConfirmed.value, chargerAndLocationConfirmed: chargerConfirmed.value })
    });
    const result = await response.json();
    if (!response.ok || !result.accepted) throw new Error(result.detail ?? result.message ?? result.error ?? '操作未完成，请核对原会话。');
    message.value = result.message; selected.value = undefined;
  } catch (e) { error.value = e instanceof Error ? e.message : String(e); }
  finally { pending.value = false; await load(); }
}
onMounted(() => { void load(); timer = setInterval(() => { if (!pending.value) void load(); }, 5000); });
onUnmounted(() => { disposed = true; clearInterval(timer); });
</script>

<template>
  <section class="management-page charging-page">
    <header class="management-title"><h2>充电管理</h2><button @click="load" :disabled="pending">刷新状态</button></header>
    <p v-if="error || loadError" role="alert" class="charge-error">{{error || loadError}}</p>
    <p v-if="message" role="status">{{message}}</p>
    <div class="charge-section charge-monitoring">
      <h3>充电桩状态 <span class="charge-monitor-label"><i class="status-dot" :class="monitoring.tone"></i>{{monitoring.label}}</span></h3>
      <dl class="charge-monitor-fields">
        <div><dt>桩端通信</dt><dd>{{monitoring.communication}}</dd></div>
        <div><dt>待机 / 充电</dt><dd>{{monitoring.operation}}</dd></div>
        <div><dt>桩端故障</dt><dd>{{monitoring.fault}}</dd></div>
        <div><dt>最近桩端反馈</dt><dd>{{monitoring.lastFeedback}}</dd></div>
      </dl>
      <p>{{monitoring.summary}}</p>
      <p>独立脚本的充电实测属于测试记录，不代表此刻在线、正在充电或没有故障。当前插头对接脚本尚未接入下方的后台自动充电流程。</p>
    </div>
    <template v-if="status">
      <div class="charge-policy">
        <strong>后台自动充电策略：{{monitoring.policy}}</strong>
        <span>≤{{status.options.startBatteryPercent}}% 申请充电</span><span>≤{{status.options.criticalBatteryPercent}}% 停止接新业务</span><span>≥{{status.options.targetBatteryPercent}}% 结束充电</span>
      </div>
      <p v-if="status.blockReason" class="charge-notice">{{status.blockReason}}</p>
      <p class="charge-explanation">策略开关只表示后台配置，不表示充电桩在线或车辆已对接成功。策略要求箱体循环与设备握手安全结束后才前往充电，确认停止充电并退出充电位后释放资源；异常时保留原会话和占用。</p>
      <div class="charge-section"><h3>充电位配置</h3>
        <p v-if="!status.options.sites.length">尚未配置充电位。请在现场核定充电点、出口、独占区域及充电控制方式后更新后台配置。</p>
        <div v-else class="charge-table-wrap"><table><thead><tr><th>充电位</th><th>厂家充电点</th><th>出口</th><th>允许车辆</th><th>现场核定</th></tr></thead><tbody>
          <tr v-for="site in status.options.sites" :key="site.id"><td>{{site.id}}</td><td>{{site.chargingSiteId}}</td><td>{{site.exitSiteId}}</td><td>{{site.allowedVehicleIds.join('、') || '未指定'}}</td><td>{{site.commissioned ? '已配置核定' : '待核定'}}</td></tr>
        </tbody></table></div>
      </div>
      <div class="charge-section"><h3>后台自动充电会话 <small>{{loadError ? '上次成功读取' : '后台读取时间'}}：{{when(status.capturedAt)}}</small></h3>
        <p v-if="!status.sessions.length">暂无充电会话。</p>
        <div v-else class="charge-table-wrap"><table><thead><tr><th>车辆 / 充电位</th><th>后台流程阶段</th><th>车辆电池采样</th><th>流程说明</th><th>操作</th></tr></thead><tbody>
          <tr v-for="row in [...status.sessions].reverse()" :key="row.sessionId">
            <td><strong>{{row.vehicleId}}</strong><small>{{row.site?.id ?? '等待分配'}}</small></td>
            <td><span :class="{ 'charge-error': row.phase === 'Hold' }">{{phases[row.phase] ?? row.phase}}</span><small>{{when(row.updatedAt)}}</small></td>
            <td>{{row.battery?.batteryPercent ?? '—'}}%<small>{{row.battery?.current ?? '—'}} A · {{row.battery?.temperature ?? '—'}} ℃</small></td>
            <td class="charge-reason">{{row.message}}<small>{{row.sessionId}}</small></td>
            <td><div class="charge-actions"><button v-if="row.phase !== 'Completed'" :disabled="pending" @click="open(row, 'stop')">停止申请</button>
              <template v-if="row.phase === 'Hold'"><button :disabled="pending" @click="open(row, 'continue')">核对续接</button><button :disabled="pending" @click="open(row, 'close')">核对离位</button></template></div></td>
          </tr>
        </tbody></table></div>
      </div>
    </template>
    <ConsoleDialog v-if="selected" :title="action === 'stop' ? '申请停止充电' : action === 'continue' ? '核对后续接原充电会话' : '核对车辆已经退出充电位'" @close="!pending && (selected = undefined)">
      <div class="charge-review"><p>{{selected.vehicleId}} · {{phases[selected.phase] ?? selected.phase}}</p>
        <p>{{action === 'stop' ? '后台尝试关闭充电继电器并暂停会话，保留资源。停止后须人工核对，再续接原流程或核定实际离位。' : action === 'continue' ? '查询原命令和实时采样后续接；发送结果未知时不能通过此操作重发。' : '仅在车辆实际退出、充电已停止且原任务终态均有证据时关闭。后台会重新核验。'}}</p>
        <label>操作依据（至少10字）<textarea v-model="reason" rows="3" maxlength="1000" :disabled="pending"></textarea></label>
        <template v-if="action !== 'stop'">
          <label><input v-model="vehicleConfirmed" type="checkbox" :disabled="pending">已核对车辆、载荷和原任务，现场安全</label>
          <label><input v-model="chargerConfirmed" type="checkbox" :disabled="pending">已核对充电状态、实际位置和充电位</label>
        </template>
        <p v-if="error" role="alert" class="charge-error">{{error}}</p>
        <button :disabled="pending || reason.trim().length < 10 || action !== 'stop' && (!vehicleConfirmed || !chargerConfirmed)" @click="submit">{{pending ? '核对中…' : '提交核对'}}</button>
      </div>
    </ConsoleDialog>
  </section>
</template>

<style scoped>
.charge-section.charge-monitoring{margin-top:0}.charge-monitoring h3{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap}.charge-monitor-label{display:inline-flex;align-items:center;gap:7px;color:#9bacbf;font-size:12px;font-weight:normal}.charge-monitor-fields{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:16px;margin:0 0 16px}.charge-monitor-fields>div{min-width:0}.charge-monitor-fields dt{font-size:12px;color:#94a8c0}.charge-monitor-fields dd{margin:8px 0 0;color:#d2dfec}.charge-monitoring p{color:#a8bbcf;line-height:1.8;margin-top:8px}@media(max-width:700px){.charge-monitor-fields{grid-template-columns:repeat(2,minmax(0,1fr))}}
.charging-page{overflow:auto}.charge-policy{display:flex;gap:24px;flex-wrap:wrap;align-items:center;padding:20px;border:1px solid #2b405b;border-radius:8px;background:#101e31}.charge-policy strong{color:#65d9c2}.charge-notice{padding:14px;border-left:3px solid #dfb86a;background:#2b251b;color:#f3d599}.charge-error{color:#ffaaa7}.charge-explanation{line-height:1.8;color:#a8bbcf}.charge-section{margin-top:25px;padding:20px;background:#101e31;border:1px solid #2b405b;border-radius:8px}.charge-section h3{margin:0 0 18px}.charge-section h3 small{display:inline;margin-left:14px;font-weight:normal}.charge-table-wrap{overflow-x:auto}table{width:100%;border-collapse:collapse;text-align:left}th,td{padding:14px 12px;border-bottom:1px solid #2b405b;vertical-align:top}th{color:#94a8c0;white-space:nowrap}small{display:block;color:#9bacbf;font-size:12px;margin-top:7px}.charge-reason{min-width:230px;max-width:540px;line-height:1.6;overflow-wrap:anywhere}.charge-actions{display:flex;gap:8px;flex-wrap:wrap}button{white-space:nowrap}.charge-review{display:grid;gap:16px;max-width:650px;padding:20px;line-height:1.7}.charge-review label{display:block}.charge-review textarea{display:block;width:100%;margin-top:8px}
</style>
