<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue';
import ConsoleDialog from './ConsoleDialog.vue';
import { useDispatchStore } from './stores/dispatch';
const props = defineProps<{ mapId: string }>();
const emit = defineEmits<{ close: []; published: [] }>();
const store = useDispatchStore();
const workspace = ref<any>(null), busy = ref(false), reviewed = ref(false), message = ref('');
const context = computed(() => [store.demoMode, store.fresh, store.adapterIdentity?.sha256].join('|'));
const loadedContext = ref('');
const available = computed(() => !store.demoMode && store.fresh);
const valid = computed(() => available.value && loadedContext.value === context.value && workspace.value?.draft?.editToken);
let epoch = 0;
watch(context, () => { ++epoch; workspace.value = null; reviewed.value = false; busy.value = false; message.value = '连接或数据源变化，请重新读取已保存草稿。'; });
onUnmounted(() => { ++epoch; });
async function load() {
  if (!available.value || busy.value) return;
  const mine = ++epoch; busy.value = true; reviewed.value = false; workspace.value = null;
  try {
    const result: any = await store.api(`/api/maps/${encodeURIComponent(props.mapId)}/workspace`);
    if (mine !== epoch) return;
    if (result.project?.mapId !== props.mapId || !result.draft?.versionId || !result.draft?.editToken) throw new Error('未取得匹配的后台草稿。');
    workspace.value = result; loadedContext.value = context.value; message.value = '';
  } catch (ex) { if (mine === epoch) message.value = String(ex); }
  finally { if (mine === epoch) busy.value = false; }
}
async function publish() {
  if (!valid.value || !reviewed.value || busy.value) return;
  const mine = ++epoch, draft = workspace.value.draft; busy.value = true;
  try {
    const result: any = await store.api('/api/maps/publish', { mapId: props.mapId, versionId: draft.versionId, expectedEditToken: draft.editToken });
    if (mine !== epoch) return;
    reviewed.value = false; workspace.value = null;
    if (result.accepted !== true) { message.value = result.message || '发布未被接受，请重新读取核对。'; return; }
    emit('published');
  } catch (ex) { if (mine === epoch) { reviewed.value = false; workspace.value = null; message.value = `发布结果未确认，请刷新目录和版本历史核对后再操作，不会自动重试。${String(ex)}`; } }
  finally { if (mine === epoch) busy.value = false; }
}
void load();
</script>
<template>
  <ConsoleDialog title="发布已保存地图草稿" @close="emit('close')">
    <p class="publish-summary">发布对象是后台已保存的草稿。工程编辑中的未保存修改不包含在本次发布中。发布不切换运行地图，也不上传车辆。</p>
    <p v-if="!available" class="inline-warning">请退出演示并连接后台。</p>
    <p v-if="message" class="inline-warning" role="status">{{message}}</p>
    <dl v-if="workspace" class="property-list">
      <dt>地图</dt><dd>{{workspace.project.name}} · {{mapId}}</dd>
      <dt>草稿版本</dt><dd>{{workspace.draft.versionId}}</dd>
      <dt>点位／道路</dt><dd>{{workspace.draft.definition.nodes.length}} ／ {{workspace.draft.definition.edges.length}}</dd>
      <dt>单位／坐标系</dt><dd>{{workspace.draft.definition.unit}} ／ {{workspace.draft.definition.coordinateSystem}}</dd>
    </dl>
    <label><input v-model="reviewed" type="checkbox" :disabled="!valid || busy" />已核对后台已保存草稿，确认发布此版本</label>
    <template #footer>
      <button @click="emit('close')">取消</button>
      <button :disabled="busy || !available" @click="load">重新读取草稿</button>
      <button class="accent-button" :disabled="busy || !valid || !reviewed" @click="publish">确认发布草稿</button>
    </template>
  </ConsoleDialog>
</template>
