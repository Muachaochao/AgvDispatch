<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref } from 'vue';
import { IconX } from '@tabler/icons-vue';
defineProps<{ title: string; wide?: boolean }>();
const emit = defineEmits<{ close: [] }>();
const panel = ref<HTMLElement>();
let previous: HTMLElement | null = null;
function keydown(event: KeyboardEvent) {
  if (event.key === 'Escape') { event.stopPropagation(); emit('close'); }
  if (event.key !== 'Tab') return;
  const items = [...(panel.value?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), [tabindex="0"]') ?? [])];
  if (!items.length) return;
  if (event.shiftKey && document.activeElement === items[0]) { event.preventDefault(); items.at(-1)?.focus(); }
  else if (!event.shiftKey && document.activeElement === items.at(-1)) { event.preventDefault(); items[0].focus(); }
}
onMounted(async () => { previous = document.activeElement as HTMLElement; await nextTick(); panel.value?.querySelector<HTMLElement>('button')?.focus(); });
onUnmounted(() => previous?.focus());
</script>
<template><Teleport to="body"><div class="dialog-backdrop" @click.self="emit('close')"><section ref="panel" class="console-dialog" :class="{ wide }" role="dialog" aria-modal="true" :aria-label="title" @keydown="keydown"><header><h2>{{ title }}</h2><button class="icon-button" aria-label="关闭弹窗" @click="emit('close')"><IconX :size="20" /></button></header><div class="dialog-content"><slot /></div><footer><slot name="footer"><button @click="emit('close')">关闭</button></slot></footer></section></div></Teleport></template>
