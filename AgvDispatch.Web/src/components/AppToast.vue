<script setup lang="ts">
import { useToastStore } from '../stores/toast';
import { IconX } from '@tabler/icons-vue';
const toast = useToastStore();
</script>
<template>
  <Teleport to="body">
    <div v-if="toast.items.length" class="toast-container" aria-live="polite">
      <div v-for="item in toast.items" :key="item.id" class="toast-item" :class="item.type" role="status">
        <span>{{ item.message }}</span>
        <button class="toast-close" aria-label="关闭" @click="toast.dismiss(item.id)"><IconX :size="14" /></button>
      </div>
    </div>
  </Teleport>
</template>
<style scoped>
.toast-container{position:fixed;top:80px;right:18px;z-index:100;display:flex;flex-direction:column;gap:8px;max-width:420px;pointer-events:none}
.toast-item{display:flex;align-items:center;gap:10px;padding:12px 16px;border-radius:5px;background:#1a2b4a;border:1px solid #2e4a6e;color:#cde0f5;font-size:12px;line-height:1.6;pointer-events:auto;animation:toast-in .25s ease-out;box-shadow:0 8px 30px #0009}
.toast-item.success{border-color:#2a7a6a;background:#132e38}.toast-item.success span{color:#5ee8c2}
.toast-item.warning{border-color:#7a6d3a;background:#2a2520}.toast-item.warning span{color:#eac66e}
.toast-item.error{border-color:#7a3a3a;background:#2a1820}.toast-item.error span{color:#f28b8b}
.toast-close{background:transparent;border:0;color:#8ea5c5;padding:2px;cursor:pointer;min-height:auto;flex-shrink:0}
@keyframes toast-in{from{opacity:0;transform:translateX(30px)}to{opacity:1;transform:none}}
</style>
