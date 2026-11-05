<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import { chargerMonitoringState, chargingPhaseLabels, matchingChargingWorkflow } from '../equipment-status';
import type { ChargingSession as Session } from '../equipment-status';
import { useDispatchStore } from '../stores/dispatch';
import { useChargingStore } from '../stores/charging';
import { chargingRequestStorageKey, findChargingRequestSession, readChargingRequestIntent } from '../charging-request';
import type { ChargingRequestIntent } from '../charging-request';
import { industrialText } from '../operator-labels';

const dispatch = useDispatchStore();
const reader = useChargingStore();
const status = computed(() => reader.status);
const loadError = computed(() => reader.loadError);
const error = ref(''); const message = ref(''); const pending = ref(false);
const monitoring = computed(() => chargerMonitoringState(status.value, loadError.value, dispatch.now));
const selected = ref<Session>(); const action = ref('stop'); const reason = ref('');
const vehicleConfirmed = ref(false); const chargerConfirmed = ref(false);
const phases = chargingPhaseLabels;
const requestVehicle = ref(''); const requestReturnSite = ref(''); const requestMode = ref('production');
const requestIntent = ref<ChargingRequestIntent>(); const storageError = ref('');
const availableVehicleIds = computed(() => [...new Set([
  ...dispatch.vehicles.map(vehicle => vehicle.vehicleId),
  ...status.value?.options.sites.flatMap(site => site.allowedVehicleIds) ?? []
])]);
const requestLocked = computed(() => pending.value || !!requestIntent.value || !!storageError.value);
const when = (v: string) => new Date(v).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false });
const controlMode = (mode?: string) => mode === 'RobokitScript' ? '反光板识别与插接' : mode === 'Relay6005' || !mode ? '车端标准继电器' : '控制方式待核对';
const phaseLabel = (phase?: string) => phase ? (phases[phase] ?? '阶段待核对') : '阶段待核对';
const target = (row: Session) => row.requiredPositiveSeconds != null
  ? `连续正电流 ${row.requiredPositiveSeconds} 秒` : `目标电量 ${row.targetBatteryPercent ?? status.value?.options.targetBatteryPercent ?? '—'}%`;
const positiveSeconds = (row: Session) => {
  const value = matchingChargingWorkflow(row)?.continuousPositiveSeconds;
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? `${value.toFixed(1)} 秒` : '尚未取得';
};
const sessionForCharger = (chargerId: string) => (status.value?.sessions ?? [])
  .find(row => row.site?.chargerId === chargerId && row.phase !== 'Completed');
function clearRequestIntent() {
  sessionStorage.removeItem(chargingRequestStorageKey); requestIntent.value = undefined;
}
function reconcileRequest(intent = requestIntent.value) {
  if (!intent || loadError.value) return false;
  const row = findChargingRequestSession(intent.requestId, status.value?.sessions ?? []);
  if (!row) return false;
  clearRequestIntent(); error.value = '';
  message.value = `已查询到原充电申请：${row.vehicleId}，${phaseLabel(row.phase)}。`;
  return true;
}
async function load() {
  await reader.load(); reconcileRequest();
}
async function requestCharge(retryOriginal = false) {
  if (pending.value || storageError.value || (!retryOriginal && requestIntent.value)) return;
  const intent = retryOriginal ? requestIntent.value : {
    requestId: crypto.randomUUID(), vehicleId: requestVehicle.value.trim(),
    requiredPositiveSeconds: requestMode.value === 'test' ? 60 : null,
    returnSiteId: requestReturnSite.value.trim() || null
  };
  if (!intent?.vehicleId) return;
  if (!retryOriginal && intent.requiredPositiveSeconds === 60 && !status.value?.options.allowTimedTests) return;
  pending.value = true; error.value = ''; message.value = '';
  const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 35000);
  try {
    // Persist before sending. An uncertain response keeps the exact original ID and payload.
    sessionStorage.setItem(chargingRequestStorageKey, JSON.stringify(intent)); requestIntent.value = intent;
    const response = await fetch('/api/charging/request', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(intent), signal: controller.signal
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail ?? result.error ?? '申请应答未确认，请查询原记录。');
    if (!result.accepted) {
      clearRequestIntent(); throw new Error(result.message ?? '充电申请未受理。');
    }
    clearRequestIntent(); message.value = result.message ?? '充电申请已受理，等待业务安全收尾和充电位分配。';
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
    await load();
    reconcileRequest(intent);
  } finally { clearTimeout(timeout); pending.value = false; await load(); }
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
watch(() => reader.status, () => { reconcileRequest(); });
onMounted(() => {
  try {
    const saved = sessionStorage.getItem(chargingRequestStorageKey);
    requestIntent.value = readChargingRequestIntent(saved);
    if (saved && !requestIntent.value) storageError.value = '保存的原申请记录无法解析，请核对后台充电记录后处理浏览器中的原记录。';
    if (requestIntent.value) {
      requestVehicle.value = requestIntent.value.vehicleId; requestReturnSite.value = requestIntent.value.returnSiteId ?? '';
      requestMode.value = requestIntent.value.requiredPositiveSeconds === 60 ? 'test' : 'production';
    }
  } catch { storageError.value = '浏览器无法保存充电申请记录，暂不能发送申请。'; }
  void load();
});
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
        <div><dt>桩端输出反馈</dt><dd>{{monitoring.output}}</dd></div>
        <div><dt>车端充电证据</dt><dd>{{monitoring.vehicleCharge}}</dd></div>
      </dl>
      <p>{{monitoring.summary}}</p>
      <p>充电桩ID、IP、端口及充电位绑定统一在 <RouterLink to="/communication">PLC / AGV / 充电桩通信配置</RouterLink> 管理。</p>
      <div v-if="status?.chargers?.length" class="charge-table-wrap">
        <table><thead><tr><th>充电桩</th><th>地址 / 端口</th><th>常驻通信</th><th>当前充电会话</th><th>最后检查</th></tr></thead><tbody>
          <tr v-for="charger in status.chargers" :key="charger.chargerId">
            <td><strong>{{charger.chargerId}}</strong><small>{{charger.chargerName}}</small></td>
            <td>{{charger.address}}<small>{{charger.ackPort ?? 20207}} / {{charger.statusPort ?? 20204}}</small></td>
            <td :class="{ 'charge-error': charger.outcome !== 'connected' }">{{charger.outcome === 'connected' ? '20207 / 20204 可连接' : industrialText(charger.message)}}</td>
            <td><template v-if="sessionForCharger(charger.chargerId)">{{sessionForCharger(charger.chargerId)?.vehicleId}} · {{phaseLabel(sessionForCharger(charger.chargerId)!.phase)}}<details><summary>原始阶段</summary><code>{{sessionForCharger(charger.chargerId)!.phase}}</code></details><small>{{matchingChargingWorkflow(sessionForCharger(charger.chargerId)!)?.station?.current ?? '—'}} A · {{matchingChargingWorkflow(sessionForCharger(charger.chargerId)!)?.station?.voltage ?? '—'}} V</small></template><span v-else>无</span></td>
            <td>{{when(charger.observedAt)}}</td>
          </tr>
        </tbody></table>
      </div>
      <p>后台常驻检查 20207/20204 通信。工作模式、报警和输出只在新鲜充电会话中存在；厂家提供独立只读状态协议前，端口在线不能代替这些字段。</p>
    </div>
    <template v-if="status">
      <div class="charge-policy">
        <strong>后台自动充电策略：{{monitoring.policy}}</strong>
        <span>≤{{status.options.startBatteryPercent}}% 申请充电并停止接新业务</span><span>≤{{status.options.criticalBatteryPercent}}% 始终禁止新业务</span><span>≥{{status.options.targetBatteryPercent}}% 结束充电</span>
      </div>
      <p v-if="status.blockReason" class="charge-notice">{{status.blockReason}}</p>
      <p class="charge-explanation">当前箱体循环与设备握手安全结束后，车辆才交给充电调度。充电排队及运行期间不接新业务；确认停止充电、退桩并完成返程后恢复接单，异常时保留原会话和占用。</p>
      <div class="charge-section charge-request"><h3>申请充电</h3>
        <form class="charge-request-form" @submit.prevent="requestCharge()">
          <label>车辆<input v-model="requestVehicle" list="charging-vehicles" maxlength="128" placeholder="选择或输入车辆编号" :disabled="requestLocked" required></label>
          <datalist id="charging-vehicles"><option v-for="id in availableVehicleIds" :key="id" :value="id"></option></datalist>
          <label>结束条件<select v-model="requestMode" :disabled="requestLocked"><option value="production">充到目标电量 {{status.options.targetBatteryPercent}}%</option><option value="test" :disabled="!status.options.allowTimedTests">测试：连续充电 1 分钟</option></select></label>
          <label>返程站点（选填）<input v-model="requestReturnSite" maxlength="128" placeholder="留空使用原待机点或本轮起点" :disabled="requestLocked"></label>
          <button type="submit" :disabled="requestLocked || !status.enabled || !!loadError || !requestVehicle.trim() || requestMode === 'test' && !status.options.allowTimedTests">{{pending ? '处理中…' : '申请充电'}}</button>
        </form>
        <p>申请进入后台队列，等待当前业务安全收尾、充电位和进出路线核验；不会中断正在执行的箱体循环。一分钟测试按连续实际充电计时，建立充电连接的等待时间不计入。</p>
        <p v-if="!status.enabled">后台自动充电未启用，暂不能发送新的充电申请。</p>
        <p v-if="!status.options.allowTimedTests">一分钟测试尚未启用，当前使用目标电量结束充电。</p>
        <p v-if="storageError" role="alert" class="charge-error">{{storageError}}</p>
        <div v-if="requestIntent" class="charge-request-pending" role="status">
          <p>原申请 {{requestIntent.requestId}} 尚待核对，车辆 {{requestIntent.vehicleId}}。查询不会重新发送任务。</p>
          <button :disabled="pending" @click="load">查询原申请</button>
          <button :disabled="pending || !!loadError" @click="requestCharge(true)">重试原申请</button>
        </div>
      </div>
      <div class="charge-section"><h3>充电位配置</h3>
        <p v-if="!status.options.sites.length">尚未配置充电位。请在现场核定充电点、出口、独占区域及充电控制方式后更新后台配置。</p>
        <div v-else class="charge-table-wrap"><table><thead><tr><th>充电位</th><th>绑定充电桩</th><th>前往站点</th><th>出口</th><th>控制方式</th><th>允许车辆</th><th>现场核定</th></tr></thead><tbody>
          <tr v-for="site in status.options.sites" :key="site.id"><td>{{site.id}}</td><td>{{site.chargerId || '未绑定'}}</td><td>{{site.chargingSiteId}}</td><td>{{site.exitSiteId}}</td><td>{{controlMode(site.controlMode)}}</td><td>{{site.allowedVehicleIds.join('、') || '未指定'}}</td><td>{{site.commissioned ? '已配置核定' : '待核定'}}</td></tr>
        </tbody></table></div>
      </div>
      <div class="charge-section"><h3>后台自动充电会话 <small>{{loadError ? '上次成功读取' : '后台读取时间'}}：{{when(status.capturedAt)}}</small></h3>
        <p v-if="!status.sessions.length">暂无充电会话。</p>
        <div v-else class="charge-table-wrap"><table><thead><tr><th>车辆 / 充电位</th><th>后台流程阶段</th><th>目标 / 返程</th><th>车辆电池采样</th><th>流程说明</th><th>操作</th></tr></thead><tbody>
          <tr v-for="row in [...status.sessions].reverse()" :key="row.sessionId">
            <td><strong>{{row.vehicleId}}</strong><small>{{row.site?.id ?? '等待分配'}}</small></td>
            <td><span :class="{ 'charge-error': row.phase === 'Hold' }">{{phaseLabel(row.phase)}}</span><details><summary>原始阶段</summary><code>{{row.phase}}</code></details><small>{{when(row.updatedAt)}}</small></td>
            <td>{{target(row)}}<small>起点：{{row.originSiteId ?? '待交接后确定'}}</small><small>返程：{{row.returnSiteId ?? row.requestedReturnSiteId ?? '待规划'}}</small></td>
            <td>{{row.battery?.batteryPercent ?? '—'}}%<small>{{row.battery?.current ?? '—'}} A · {{row.battery?.temperature ?? '—'}} ℃</small></td>
            <td class="charge-reason">{{industrialText(row.message)}}
              <template v-if="matchingChargingWorkflow(row)"><small>{{matchingChargingWorkflow(row)?.message}}</small><small>本段连续充电：{{positiveSeconds(row)}} · 停充核验：{{row.shutdownVerified || matchingChargingWorkflow(row)?.shutdownVerified ? '已确认' : '尚未确认'}}</small></template>
              <small>{{row.sessionId}}</small></td>
            <td><div class="charge-actions"><button v-if="row.phase !== 'Completed'" :disabled="pending" @click="open(row, 'stop')">停止申请</button>
              <template v-if="row.phase === 'Hold'"><button :disabled="pending" @click="open(row, 'continue')">核对续接</button><button :disabled="pending" @click="open(row, 'close')">核对离位</button></template></div></td>
          </tr>
        </tbody></table></div>
      </div>
    </template>
    <ConsoleDialog v-if="selected" :title="action === 'stop' ? '申请停止充电' : action === 'continue' ? '核对后续接原充电会话' : '核对车辆已经退出充电位'" @close="!pending && (selected = undefined)">
      <div class="charge-review"><p>{{selected.vehicleId}} · {{phaseLabel(selected.phase)}}</p>
        <p>{{action === 'stop' ? '向后台申请停止并核验充电输出，原会话及资源继续保留。正在导航或对接时，停止申请不能代替现场急停；请观察原任务状态。' : action === 'continue' ? '查询原命令和实时采样后续接；发送结果未知时不能通过此操作重发。' : '仅在车辆实际退出、充电已停止且原任务终态均有证据时关闭。后台会重新核验。'}}</p>
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
.charge-request-form{display:flex;gap:16px;align-items:end;flex-wrap:wrap}.charge-request-form label{display:grid;gap:8px;flex:1 1 190px;color:#a8bbcf}.charge-request-form input,.charge-request-form select{width:100%;min-width:0}.charge-request p{line-height:1.7;color:#a8bbcf}.charge-request-pending{border-left:3px solid #dfb86a;padding:2px 14px;background:#2b251b}.charge-request-pending button{margin:0 8px 12px 0}
.charge-section.charge-monitoring{margin-top:0}.charge-monitoring h3{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap}.charge-monitor-label{display:inline-flex;align-items:center;gap:7px;color:#9bacbf;font-size:12px;font-weight:normal}.charge-monitor-fields{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:16px;margin:0 0 16px}.charge-monitor-fields>div{min-width:0}.charge-monitor-fields dt{font-size:12px;color:#94a8c0}.charge-monitor-fields dd{margin:8px 0 0;color:#d2dfec}.charge-monitoring p{color:#a8bbcf;line-height:1.8;margin-top:8px}@media(max-width:700px){.charge-monitor-fields{grid-template-columns:repeat(2,minmax(0,1fr))}}
.charging-page{overflow:auto}.charge-policy{display:flex;gap:24px;flex-wrap:wrap;align-items:center;padding:20px;border:1px solid #2b405b;border-radius:8px;background:#101e31}.charge-policy strong{color:#65d9c2}.charge-notice{padding:14px;border-left:3px solid #dfb86a;background:#2b251b;color:#f3d599}.charge-error{color:#ffaaa7}.charge-explanation{line-height:1.8;color:#a8bbcf}.charge-section{margin-top:25px;padding:20px;background:#101e31;border:1px solid #2b405b;border-radius:8px}.charge-section h3{margin:0 0 18px}.charge-section h3 small{display:inline;margin-left:14px;font-weight:normal}.charge-table-wrap{overflow-x:auto}table{width:100%;border-collapse:collapse;text-align:left}th,td{padding:14px 12px;border-bottom:1px solid #2b405b;vertical-align:top}th{color:#94a8c0;white-space:nowrap}small{display:block;color:#9bacbf;font-size:12px;margin-top:7px}.charge-reason{min-width:230px;max-width:540px;line-height:1.6;overflow-wrap:anywhere}.charge-actions{display:flex;gap:8px;flex-wrap:wrap}button{white-space:nowrap}.charge-review{display:grid;gap:16px;max-width:650px;padding:20px;line-height:1.7}.charge-review label{display:block}.charge-review textarea{display:block;width:100%;margin-top:8px}
</style>
