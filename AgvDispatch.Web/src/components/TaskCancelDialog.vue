<script setup lang="ts">
import { ref } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { newTaskRequestId } from '../task-request';
import ConsoleDialog from '../ConsoleDialog.vue';

const props = defineProps<{ taskId: string; stage: string }>();
const emit = defineEmits<{ done: [] }>();

const store = useDispatchStore();
const toast = useToastStore();

const open = ref(false);
const reason = ref('');
const busy = ref(false);

function canCancel(stage: string): boolean {
  return ['Executing', 'Running', 'GoingToPickup', 'PickingUp', 'GoingToDumper',
    'WaitingForDumper', 'Dumping', 'ReturningEmptyBox', 'WaitingForPlcConfirmation',
    'SubmittingToAgv', 'WaitingForVehicle', 'Created'].includes(stage);
}

async function submit() {
  if (busy.value || reason.value.trim().length < 10) return;
  busy.value = true;
  try {
    const result = await store.api<{ accepted: boolean; message: string }>(`/api/tasks/${encodeURIComponent(props.taskId)}/cancel`, {
      requestId: newTaskRequestId(),
      taskId: props.taskId,
      currentStage: props.stage,
      reason: reason.value.trim()
    });
    if (result.accepted) {
      toast.success(result.message || '取消请求已提交');
    } else {
      toast.warning(result.message || '取消请求未被接受');
    }
    open.value = false;
    emit('done');
  } catch (ex) {
    toast.error(`取消请求失败：${String(ex)}`);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <button v-if="canCancel(stage)" class="cancel-task-button" :disabled="!store.fresh || !store.submissionEnabled" @click="open = true; reason = ''">
    申请取消任务
  </button>

  <ConsoleDialog v-if="open" title="申请取消调度任务" @close="open = false">
    <p class="form-explanation">取消请求将提交后台核验。已被车辆接受的动作不会因此自动停止，需现场确认。</p>
    <div class="cancel-info">
      <dl class="property-list">
        <dt>任务编号</dt><dd class="cyan mono">{{ taskId }}</dd>
        <dt>当前状态</dt><dd>{{ stage }}</dd>
      </dl>
    </div>
    <label class="cancel-reason-label">取消原因（至少10字）
      <textarea v-model="reason" rows="3" maxlength="500" :disabled="busy" placeholder="请描述取消原因，例如：现场载荷已人工移走，需取消当前运输任务"></textarea>
    </label>
    <div class="inline-warning">{{ store.submissionEnabled ? '取消请求经后台核验后执行，不保证立即生效。车辆在途动作需现场核对。' : 'Web任务操作未启用。' }}</div>
    <template #footer>
      <button @click="open = false">返回</button>
      <button class="accent-button" style="border-color:#a03030;background:#3a1520" :disabled="busy || reason.trim().length < 10 || !store.fresh || !store.submissionEnabled" @click="submit">
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
