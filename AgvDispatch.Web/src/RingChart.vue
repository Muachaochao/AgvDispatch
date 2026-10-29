<script setup lang="ts">
import { onMounted, ref, watch, onUnmounted } from 'vue';
const props = withDefaults(defineProps<{ segments: { label: string; value: number; color: string }[]; total: number; caption?: string; available?: boolean }>(), { caption: '任务总数', available: true });
const canvas = ref<HTMLCanvasElement>();
function paint() {
  if (!canvas.value) return;
  const ratio = Math.min(devicePixelRatio, 2), side = 180;
  canvas.value.width = side * ratio; canvas.value.height = side * ratio;
  const ctx = canvas.value.getContext('2d'); if (!ctx) return;
  ctx.scale(ratio, ratio); ctx.clearRect(0, 0, side, side);
  ctx.lineWidth = 13; ctx.strokeStyle = '#263251'; ctx.beginPath(); ctx.arc(90, 90, 65, 0, Math.PI * 2); ctx.stroke();
  let angle = -Math.PI / 2;
  for (const segment of props.segments) {
    if (!props.total || !segment.value) continue;
    const end = angle + segment.value / props.total * Math.PI * 2;
    ctx.strokeStyle = segment.color; ctx.beginPath(); ctx.arc(90, 90, 65, angle + 0.025, end - 0.025); ctx.stroke(); angle = end;
  }
  ctx.textAlign = 'center'; ctx.fillStyle = '#e8f5ff'; ctx.font = '600 29px Consolas, monospace'; ctx.fillText(props.available ? String(props.total) : '—', 90, 91);
  ctx.fillStyle = '#8b9cba'; ctx.font = '11px Microsoft YaHei, sans-serif'; ctx.fillText(props.available ? props.caption : '统计待更新', 90, 113);
}
onMounted(paint); watch(() => [props.total, props.segments, props.available, props.caption], paint, { deep: true });
onUnmounted(() => { /* no animation loop or owned observers */ });
</script>
<template><canvas ref="canvas" class="ring-chart" role="img" :aria-label="available ? `任务分布，总计${total}条：${segments.map(s => `${s.label}${s.value}条`).join('，')}` : '后台全量任务统计暂不可用'"></canvas></template>
