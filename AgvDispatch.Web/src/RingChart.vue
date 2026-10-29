<script setup lang="ts">
import { computed } from 'vue';

const props = withDefaults(defineProps<{
  segments: { label: string; value: number; color: string }[];
  total: number;
  caption?: string;
  available?: boolean;
}>(), { caption: '任务总数', available: true });

const radius = 78;
const circumference = 2 * Math.PI * radius;
const activeSegments = computed(() => props.total > 0 ? props.segments.filter(segment => segment.value > 0) : []);
const renderedSegments = computed(() => {
  let start = 0;
  return activeSegments.value.map((segment, index) => {
    const rawLength = segment.value / props.total * circumference;
    const gap = activeSegments.value.length === 1 ? 0 : Math.min(8, rawLength * 0.24);
    const visibleLength = Math.max(1, rawLength - gap);
    const result = {
      ...segment,
      index,
      dasharray: `${visibleLength} ${circumference - visibleLength}`,
      dashoffset: -start
    };
    start += rawLength;
    return result;
  });
});
</script>

<template>
  <div class="ring-chart" role="img" :aria-label="available ? `任务分布，总计${total}条：${segments.map(s => `${s.label}${s.value}条`).join('，')}` : '后台全量任务统计暂不可用'">
    <svg viewBox="0 0 208 208" aria-hidden="true">
      <circle class="ring-track" cx="104" cy="104" :r="radius" />
      <circle
        v-for="segment in renderedSegments"
        :key="`${segment.label}-${segment.index}`"
        class="ring-segment"
        cx="104"
        cy="104"
        :r="radius"
        :stroke="segment.color"
        :stroke-dasharray="segment.dasharray"
        :stroke-dashoffset="segment.dashoffset"
      />
    </svg>
    <div class="ring-center">
      <strong>{{ available ? total : '—' }}</strong>
      <span>{{ available ? caption : '统计待更新' }}</span>
    </div>
  </div>
</template>

<style scoped>
.ring-chart {
  position: relative;
  width: 194px;
  height: 194px;
  margin: 2px auto 0;
  display: grid;
  place-items: center;
}

.ring-chart::before {
  content: "";
  position: absolute;
  inset: 29px;
  border-radius: 50%;
  background: radial-gradient(circle, rgba(42, 117, 156, .14), rgba(12, 26, 52, .02) 68%);
}

svg {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  overflow: visible;
}

.ring-track,
.ring-segment {
  fill: none;
  stroke-width: 18;
}

.ring-track {
  stroke: #223354;
}

.ring-segment {
  stroke-linecap: round;
  transform: rotate(-90deg);
  transform-origin: 104px 104px;
}

.ring-center {
  position: relative;
  z-index: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  pointer-events: none;
}

.ring-center strong {
  color: #eff8ff;
  font: 700 38px/1 Consolas, "Segoe UI", monospace;
  letter-spacing: 1px;
  text-shadow: 0 0 18px rgba(81, 198, 230, .18);
}

.ring-center span {
  color: #9cb4d3;
  font-size: 12px;
  font-weight: 500;
  letter-spacing: .5px;
}
</style>
