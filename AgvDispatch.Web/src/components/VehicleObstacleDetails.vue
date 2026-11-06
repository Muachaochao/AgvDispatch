<script setup lang="ts">
import { computed } from 'vue';
import type { Vehicle } from '../types';
import { obstacleDuration, vehicleObstacle } from '../obstacles';
import { shortTime } from '../dashboard';
const props = defineProps<{ vehicle: Vehicle; current: boolean; now: number }>();
const obstacle = computed(() => vehicleObstacle(props.vehicle, props.current, props.now));
const recovery = computed(() => props.vehicle.recovery);
const recoveryStateLabel = computed(() => ({
  Blocked: '正在确认连续受阻',
  DetourReady: '替代路线已准备好',
  CancelIntent: '正在安全取消原导航',
  AwaitingOriginalTerminal: '等待原导航确认结束',
  SendIntent: '正在准备发送替代路线',
  Executing: '正在执行替代路线',
  NoRoute: '暂未找到替代路线',
  Warning: '持续受阻，已预警',
  Alarm: '持续受阻，已报警',
  ClosureSuggested: '建议现场封路',
  SafetyHold: '安全条件未通过',
  Frozen: '任务已冻结，等待人工处置',
  Uncertain: '结果待核对，禁止重发',
  Recovered: '障碍影响已解除'
} as Record<string, string>)[recovery.value?.state ?? ''] ?? '正在核对恢复状态');
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
    <template v-if="recovery">
      <h3>后台自动处置</h3>
      <span class="state-label" :class="recovery.state === 'Executing' || recovery.state === 'Recovered' ? 'idle' : recovery.state === 'Frozen' || recovery.state === 'Uncertain' ? 'fault' : 'blocked'">
        {{ recoveryStateLabel }}
      </span>
      <p>{{ recovery.message }}</p>
      <dl class="property-list">
        <dt>已经受阻</dt><dd>{{ obstacleDuration(recovery.durationSeconds) }}</dd>
        <dt>下一步</dt><dd>{{ recovery.nextAction }}</dd>
        <dt>替代路线</dt><dd>{{ recovery.candidatePath.length ? recovery.candidatePath.join(' → ') : '尚未形成或当前没有安全替代路线' }}</dd>
        <dt>推荐安全点</dt><dd>{{ recovery.safeSite || '当前无需或暂未找到' }}</dd>
        <dt>原任务</dt><dd>{{ recovery.taskId }}</dd>
      </dl>
    </template>
    <h3>安全边界</h3><p>{{ obstacle.localDetourEvidence }}</p>
    <p>5秒开始规划；10秒预警；30秒报警；60秒建议封锁实际受阻方向。无路线时推荐安全点并冻结原任务，载荷、PLC责任和路权不会自动释放。</p>
  </div>
</template>

<style scoped>
.obstacle-details{line-height:1.8;color:#b6cbe4}.obstacle-details p{margin:12px 0;overflow-wrap:anywhere}.obstacle-details h3{font-size:13px;margin-top:20px;color:#d2e5f7}.obstacle-details .property-list{margin:18px 0;gap:12px}.obstacle-details .property-list dd{font-size:12px}
</style>
