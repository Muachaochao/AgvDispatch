import { defineStore } from 'pinia';
import { ref } from 'vue';

export interface ToastItem {
  id: number;
  type: 'success' | 'warning' | 'error';
  message: string;
}

let nextId = 1;

export const useToastStore = defineStore('toast', () => {
  const items = ref<ToastItem[]>([]);

  function show(type: ToastItem['type'], message: string, duration = 4000) {
    const id = nextId++;
    items.value.push({ id, type, message });
    if (items.value.length > 5) items.value.shift();
    setTimeout(() => dismiss(id), duration);
  }

  function dismiss(id: number) {
    items.value = items.value.filter(item => item.id !== id);
  }

  function success(message: string) { show('success', message); }
  function warning(message: string) { show('warning', message, 6000); }
  function error(message: string) { show('error', message, 8000); }

  return { items, show, dismiss, success, warning, error };
});
