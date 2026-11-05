<script setup lang="ts">
import { ref, watch } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { newTaskRequestId } from '../task-request';
import ConsoleDialog from '../ConsoleDialog.vue';
import {cancelIntent,type CancelIntent} from '../cancel-intent';
import { label } from '../state';

const props = defineProps<{ taskId: string; stage: string; fresh: boolean }>();
const emit = defineEmits<{ done: [] }>();

const store = useDispatchStore();
const toast = useToastStore();

const open = ref(false);
const reason = ref('');
const busy = ref(false);
watch(() => props.taskId, () => { open.value = false; reason.value = ''; });

function canCancel(stage: string): boolean {
  return ['Executing', 'Running', 'GoingToPickup', 'PickingUp', 'GoingToDumper',
    'WaitingForDumper', 'Dumping', 'ReturningEmptyBox', 'WaitingForPlcConfirmation',
    'SubmittingToAgv', 'WaitingForVehicle', 'Created'].includes(stage);
}

async function submit() {
  if (busy.value || !props.fresh || !store.fresh || !store.cancellationEnabled || !canCancel(props.stage) || reason.value.trim().length < 10) return;
  busy.value = true;
  try {
    const key='agv-cancel-intent-v1:'+props.taskId;
    const raw=sessionStorage.getItem(key);const previous=raw?JSON.parse(raw) as CancelIntent:null;
    const intent=cancelIntent(previous,props.taskId,props.stage,reason.value,newTaskRequestId);
    sessionStorage.setItem(key,JSON.stringify(intent));
    const result = await store.api<{ accepted: boolean; message: string }>(`/api/tasks/${encodeURIComponent(props.taskId)}/cancel`,intent);
    sessionStorage.removeItem(key);
    if (result.accepted) {
      toast.success(result.message || '取消请求已提交');
    } else {
      toast.warning(result.message || '取消请求未被接受');
    }
    open.value = false;
    emit('done');
  } catch (ex) {
    toast.error(`取消结果需核对：${String(ex)}。原请求编号保留，请刷新任务；勿换编号重发。`);
    emit('done');
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <button v-if="canCancel(stage)" class="cancel-task-button" :disabled="!fresh || !store.fresh || !store.cancellationEnabled" @click="open = true; reason = ''">
    申请取消任务
  </button>

  <ConsoleDialog v-if="open" title="申请取消调度任务" @close="open = false">
    <p class="form-explanation">取消请求将提交后台核验。已被车辆接受的动作不会因此自动停止，需现场确认。</p>
    <div class="cancel-info">
      <dl class="property-list">
        <dt>任务编号</dt><dd class="cyan mono">{{ taskId }}</dd>
        <dt>当前状态</dt><dd>{{ label(stage) }}</dd>
      </dl>
    </div>
    <label class="cancel-reason-label">取消原因（至少10字）
      <textarea v-model="reason" rows="3" maxlength="500" :disabled="busy" placeholder="请描述取消原因，例如：现场载荷已人工移走，需取消当前运输任务"></textarea>
    </label>
    <div class="inline-warning">{{ store.cancellationEnabled ? '取消请求经后台核验后执行，不保证立即生效。车辆在途动作需现场核对。' : 'Web任务取消未启用；与新建任务权限分别配置。' }}</div>
    <template #footer>
      <button @click="open = false">返回</button>
      <button class="accent-button" style="border-color:#a03030;background:#3a1520" :disabled="busy || reason.trim().length < 10 || !fresh || !store.fresh || !store.cancellationEnabled" @click="submit">
        {{ busy ? '提交中…' : '确认取消' }}
      </button>
    </template>
  </ConsoleDialog>
</template>

<style scoped>
.cancel-task-button{color:#f08888;border-color:#6a3838;background:#2a1520;margin-top:16px}
.cancel-task-button:hover{border-color:#c05050;background:#3a1a25}
.cancel-info{padding:14px;background:#121e35;border:1px solid #2a4060;margin-bottom:16px}
.cancel-reason-label{display:block;color:#93accc;font-size:12px;margin:16px 0}
.cancel-reason-label textarea{display:block;width:100%;margin-top:8px;background:#0b172a;color:#cee4f7;border:1px solid #3b526c;border-radius:3px;padding:10px;resize:vertical;font:inherit}
</style>
