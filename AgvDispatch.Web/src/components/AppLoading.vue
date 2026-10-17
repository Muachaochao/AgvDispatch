<script setup lang="ts">
defineProps<{ active: boolean; message?: string }>();
</script>
<template>
  <div v-if="active" class="loading-bar" role="status" :aria-label="message ?? '加载中'">
    <div class="loading-track"><div class="loading-progress"></div></div>
    <span v-if="message" class="loading-text">{{ message }}</span>
  </div>
</template>
<style scoped>
.loading-bar{position:fixed;top:0;left:0;right:0;z-index:50}
.loading-track{height:3px;background:#16253e;overflow:hidden}
.loading-progress{height:100%;width:40%;background:linear-gradient(90deg,#25d3a4,#31c6e6,#25d3a4);animation:loading-slide 1.4s ease-in-out infinite;border-radius:2px}
.loading-text{position:absolute;top:6px;left:50%;transform:translateX(-50%);font-size:10px;color:#8baac6;white-space:nowrap}
@keyframes loading-slide{0%{transform:translateX(-100%)}100%{transform:translateX(350%)}}
</style>
