<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import { shortTime } from '../dashboard';

interface TimeoutRecovery {
  recoveryId: string;
  taskId: string;
  stepId: string;
  vehicleId: string;
  stepType: number | string;
  state: string;
  outcome: string;
  originalVendorTaskId: string;
  newVendorTaskId: string | null;
  currentStation: string | null;
  destinationSite: string;
  originalPath: string[];
  candidatePath: string[];
  sendAttempted: boolean;
  message: string;
  revision: number;
  updatedAt: string;
}

const props = defineProps<{ taskId: string; fresh: boolean }>();
const store = useDispatchStore();
const row = ref<TimeoutRecovery | null>(null);
const busy = ref(false);
const message = ref('');
const reason = ref('障碍已清除');
const locationConfirmed = ref(false);
const loadConfirmed = ref(false);
const plcConfirmed = ref(false);
const executeConfirmed = ref(false);
let epoch = 0;
const timeoutStateLabels: Record<string, string> = {
  ReadyForControlledDetour: '可以进行受控改道',
  ManualIntervention: '需要人工处理',
  Executing: '正在发送替代导航',
  Executed: '替代导航已发送',
  Uncertain: '发送结果待核对'
};
const timeoutOutcomeLabels: Record<string, string> = {
  AwaitingObstacleClear: '等待障碍清除',
  SafetyHold: '安全暂停',
  ManualRelocationRequired: '需要人工恢复车辆位置',
  ManualIntervention: '需要人工处置',
  ReadyForControlledDetour: '条件满足，可以受控改道'
};

const canExecute = computed(() =>
  props.fresh && store.recoveryActionsEnabled && !!props.taskId.trim() && !!row.value &&
  row.value.state === 'ReadyForControlledDetour' && !row.value.sendAttempted &&
  locationConfirmed.value && loadConfirmed.value && plcConfirmed.value &&
  executeConfirmed.value && reason.value.trim().length > 0
);

function clearConfirmations() {
  locationConfirmed.value = false;
  loadConfirmed.value = false;
  plcConfirmed.value = false;
  executeConfirmed.value = false;
}

async function load() {
  if (!props.taskId.trim() || busy.value) return;
  const mine = ++epoch;
  busy.value = true;
  try {
    const response = await fetch(`/api/timeout-recovery/${encodeURIComponent(props.taskId.trim())}`, {
      signal: AbortSignal.timeout(20000)
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail ?? data.error ?? '读取超时恢复候选失败');
    if (mine === epoch) {
      row.value = data;
      message.value = data?.message ?? '没有已捕获的运输超时恢复记录。';
      clearConfirmations();
    }
  } catch (error) {
    if (mine === epoch) {
      row.value = null;
      message.value = String(error);
    }
  } finally {
    if (mine === epoch) busy.value = false;
  }
}

async function execute() {
  if (!canExecute.value || !row.value) return;
  const mine = ++epoch;
  busy.value = true;
  try {
    const response = await fetch(`/api/timeout-recovery/${encodeURIComponent(row.value.taskId)}/execute`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        taskId: row.value.taskId,
        expectedRevision: row.value.revision,
        executeConfirmed: executeConfirmed.value,
        vehicleAndLocationConfirmed: locationConfirmed.value,
        loadAndOwnershipConfirmed: loadConfirmed.value,
        plcConditionsConfirmed: plcConfirmed.value,
        reason: reason.value.trim()
      }),
      signal: AbortSignal.timeout(45000)
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail ?? data.error ?? '执行恢复导航失败');
    if (mine === epoch) {
      message.value = data.message;
      row.value = data.snapshot ?? row.value;
      clearConfirmations();
    }
  } catch (error) {
    if (mine === epoch) message.value = `结果需核对：${String(error)}。禁止直接重复发送，请重新查询。`;
  } finally {
    if (mine === epoch) busy.value = false;
  }
}

watch(() => props.taskId, () => { epoch++; row.value = null; message.value = ''; clearConfirmations(); void load(); });
onMounted(() => { void load(); });
</script>

<template>
  <section class="timeout-recovery">
    <header>
      <h3>运输超时替代导航</h3>
      <button :disabled="busy || !taskId.trim()" @click="load">{{ busy ? '读取中…' : '读取 / 刷新恢复候选' }}</button>
    </header>
    <p class="warning">只有原厂家导航已超时终结、车辆连续停稳、实际站点和地图一致、新路线获得路权且PLC条件通过时，才允许发送一次替代3066任务链。发送后结果未知时只查询，不重复发送。</p>
    <p v-if="message" role="status">{{ message }}</p>
    <article v-if="row">
      <b>{{ row.taskId }} · {{ timeoutStateLabels[row.state] ?? '状态待核对' }} · {{ timeoutOutcomeLabels[row.outcome] ?? '恢复结论待核对' }} · 修订 {{ row.revision }}</b>
      <p>车辆 {{ row.vehicleId }} · 当前站点 {{ row.currentStation ?? '未知' }} · 原目的站 {{ row.destinationSite }}</p>
      <p>原厂家任务 {{ row.originalVendorTaskId }} · 新厂家任务 {{ row.newVendorTaskId ?? '尚未发送' }}</p>
      <p>原路线：{{ row.originalPath.join(' → ') || '未知' }}</p>
      <p>候选路线：{{ row.candidatePath.join(' → ') || '不可用' }}</p>
      <p>更新：{{ shortTime(row.updatedAt) }} · 发送尝试：{{ row.sendAttempted ? '是（禁止重发）' : '否' }}</p>
      <div class="confirm-grid">
        <label><input v-model="locationConfirmed" type="checkbox" :disabled="busy">已核对车辆和实际站点</label>
        <label><input v-model="loadConfirmed" type="checkbox" :disabled="busy">已核对箱体、载荷和任务归属</label>
        <label><input v-model="plcConfirmed" type="checkbox" :disabled="busy">已核对PLC条件和前序步骤</label>
        <label><input v-model="executeConfirmed" type="checkbox" :disabled="busy">确认只发送一次替代导航</label>
      </div>
      <label>处理原因（可简写）<textarea v-model="reason" rows="3" maxlength="1000" :disabled="busy" /></label>
      <button :disabled="!canExecute" @click="execute">正式发送一次替代3066导航</button>
      <p v-if="!store.recoveryActionsEnabled" class="warning">当前Web恢复写操作未启用；只能查看，不能发送。</p>
    </article>
  </section>
</template>

<style scoped>
.timeout-recovery{margin-top:28px;padding:18px;border:1px solid var(--ui-line);background:color-mix(in srgb,var(--ui-warning) 7%,var(--ui-surface))}
.timeout-recovery header{display:flex;gap:12px;align-items:center;justify-content:space-between}
.timeout-recovery h3{color:var(--ui-text);font-size:15px;margin:0}
.timeout-recovery p,.timeout-recovery label{font-size:12px;line-height:1.9;color:var(--ui-text-2)}
.timeout-recovery .warning{color:var(--ui-warning)}
.timeout-recovery article{border-left:2px solid var(--ui-warning);padding-left:14px;margin-top:14px}
.timeout-recovery .confirm-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:4px 16px;margin:14px 0}
.timeout-recovery textarea{display:block;width:100%;margin-top:8px;background:var(--ui-field-bg);color:var(--ui-text);border:1px solid var(--ui-line-strong);padding:10px;resize:vertical}
.timeout-recovery label{margin:8px 0}
.timeout-recovery button{margin-top:10px}
</style>
