import { defineStore } from 'pinia';
import { ref, shallowRef } from 'vue';
import type { ChargingStatus } from '../equipment-status';

// The page and footer share one reader. This store never sends an action.
export const useChargingStore = defineStore('charging', () => {
  const status = shallowRef<ChargingStatus>();
  const loadError = ref('');
  let timer: ReturnType<typeof setInterval> | undefined;
  let inFlight: Promise<void> | undefined;
  let controller: AbortController | undefined;
  let generation = 0;
  async function read(mine: number) {
    const active = new AbortController(); controller = active;
    const timeout = setTimeout(() => active.abort(), 10000);
    try {
      const response = await fetch('/api/charging', { cache: 'no-store', signal: active.signal });
      const data = await response.json();
      if (!response.ok) throw new Error(data.detail ?? data.error ?? '无法读取充电状态。');
      if (mine === generation) { status.value = data; loadError.value = ''; }
    } catch (e) {
      if (mine === generation) loadError.value = e instanceof Error ? e.message : String(e);
    } finally {
      clearTimeout(timeout);
      if (controller === active) controller = undefined;
    }
  }
  function load() {
    if (inFlight) return inFlight;
    const pending = read(generation).finally(() => { if (inFlight === pending) inFlight = undefined; });
    inFlight = pending;
    return pending;
  }
  function start() {
    if (timer) return;
    void load(); timer = setInterval(() => { void load(); }, 5000);
  }
  function stop() {
    ++generation; clearInterval(timer); timer = undefined;
    controller?.abort(); controller = undefined; inFlight = undefined;
    status.value = undefined; loadError.value = '';
  }
  return { status, loadError, load, start, stop };
});
