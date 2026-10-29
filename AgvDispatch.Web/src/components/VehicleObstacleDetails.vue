<script setup lang="ts">
import { computed } from 'vue';
import type { Vehicle } from '../types';
import { vehicleObstacle } from '../obstacles';
import { shortTime } from '../dashboard';
const props = defineProps<{ vehicle: Vehicle; current: boolean; now: number }>();
const obstacle = computed(() => vehicleObstacle(props.vehicle, props.current, props.now));
</script>

<template>
  <div class="obstacle-details">
    <span class="state-label" :class="obstacle.tone">{{ obstacle.label }}</span>
    <p>{{ obstacle.message }}</p>
    <dl class="property-list">
      <dt>本次受阻开始</dt><dd>{{ shortTime(obstacle.since) }}</dd>
      <dt>已观察到受阻</dt><dd>{{ obstacle.duration }}</dd>
      <dt>最近解除观察（历史）</dt><dd>{{ shortTime(obstacle.lastClearedAt) }}</dd>
      <dt>最近车辆采集</dt><dd>{{ shortTime(vehicle.lastSuccessfulPollAt) }}</dd>
      <dt>判断来源</dt><dd>{{ obstacle.source || '待核对' }}</dd>
    </dl>
    <h3>当前导航策略</h3><p>{{ obstacle.navigationPolicy }}</p>
    <p>障碍解除后，由车端继续原任务；继续执行与任务完成须分别由原车端任务反馈确认。</p>
    <h3>绕行能力证据</h3><p>{{ obstacle.localDetourEvidence }}</p>
    <p>持续受阻达到30秒会提示现场检查。等待时长不作为完成、取消原任务或释放路权的依据。</p>
  </div>
</template>

<style scoped>
.obstacle-details{line-height:1.8;color:#b6cbe4}.obstacle-details p{margin:12px 0;overflow-wrap:anywhere}.obstacle-details h3{font-size:13px;margin-top:20px;color:#d2e5f7}.obstacle-details .property-list{margin:18px 0;gap:12px}.obstacle-details .property-list dd{font-size:12px}
</style>
