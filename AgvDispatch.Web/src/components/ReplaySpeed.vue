<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue';
import { replayPresets, replayRate } from '../replay-evidence';

const props = defineProps<{ label: string; disabled?: boolean }>();
const rate = defineModel<number>({ required: true });
const draft = ref(rate.value);
const list = useId();
const title = computed(() => props.label + '（0.1～128 倍）');
watch(rate, value => { draft.value = value; });
function commit() {
  draft.value = replayRate(typeof draft.value === 'number' ? draft.value : Number.NaN);
  rate.value = draft.value;
}
</script>

<template>
  <label class="replay-speed">
    <span>{{ label }}</span>
    <input v-model.number="draft" :aria-label="title" :disabled="disabled" :list="list"
      type="number" min="0.1" max="128" step="any" @change="commit" @keydown.enter.prevent="commit" />
    <b>×</b>
    <datalist :id="list"><option v-for="preset in replayPresets" :key="preset" :value="preset" /></datalist>
  </label>
</template>

<style scoped>
.replay-speed {
  display:inline-grid;
  grid-template-columns:auto 64px 12px;
  align-items:center;
  gap:3px 7px;
  min-height:46px;
  padding:5px 9px;
  color:var(--replay-speed-color,inherit);
  background:var(--replay-speed-bg,#f5f8fb);
  border:1px solid var(--replay-speed-border,#cbd8e4);
  border-radius:9px;
}
.replay-speed>span { grid-column:1 / -1; color:var(--replay-speed-label,#687b8f); font-size:9px; font-weight:800; letter-spacing:.08em; text-transform:uppercase; }
.replay-speed input {
  width:64px;
  min-width:0;
  color:inherit;
  background:transparent;
  border:0;
  outline:0;
  font:800 14px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;
}
.replay-speed b { font-size:11px; font-weight:800; }
.replay-speed:focus-within { border-color:var(--replay-speed-focus,#2d86b8); box-shadow:0 0 0 2px color-mix(in srgb,var(--replay-speed-focus,#2d86b8) 14%,transparent); }
</style>
