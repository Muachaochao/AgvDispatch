import { defineStore } from 'pinia';
import { computed, ref, shallowRef } from 'vue';
import type { Alarm, CreationOptions, Device, MapSummary, RoutePreview, Snapshot, TaskDetail, TaskRow } from '../types';
import type { AdapterIdentity } from '../release-state';
import { isFresh, isRunningStage, mayPlot } from '../state';
import { vehicleCategory, type VehicleCategory } from '../dashboard';
import { useToastStore } from './toast';
import { demoSnapshot } from '../demo';

export const useDispatchStore = defineStore('dispatch', () => {
  const snapshot = shallowRef<Snapshot | null>(null);
  const error = ref('');
  const sceneError = ref('');
  const now = ref(Date.now());
  const submissionEnabled = ref(false);
  const adapterIdentity = ref<AdapterIdentity | null>(null);

  let socket: WebSocket | undefined;
  let epoch = 0;
  const controllers = new Set<AbortController>();
  let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  let reconnectDelay = 1000;
  let nowTimer: ReturnType<typeof setInterval> | undefined;

  const fresh = computed(() => isFresh(snapshot.value, now.value));
  const vehicles = computed(() => snapshot.value?.status?.agvs ?? []);
  const allTasks = computed(() => snapshot.value?.tasks?.items ?? []);
  const equipment = computed(() => snapshot.value?.devices?.equipment ?? []);
  const resources = computed(() => snapshot.value?.resources ?? []);
  const alarms = computed(() => snapshot.value?.alarms ?? []);
  const openAlarms = computed(() => alarms.value.filter(a => a.status === 'Open'));
  const activeCount = computed(() => allTasks.value.filter(task => !['Completed', 'Cancelled'].includes(task.stage)).length);

  const vehicleCategories: VehicleCategory[] = ['running', 'waiting', 'idle', 'charging', 'fault', 'offline', 'unknown'];
  const vehicleStats = computed(() => vehicleCategories.map(key => ({
    key, label: ({ running: '执行中', waiting: '等待中', idle: '空闲', charging: '充电中', fault: '故障', offline: '离线 / 过期', unknown: '待核对' } as Record<VehicleCategory, string>)[key],
    count: vehicles.value.filter(v => vehicleCategory(v, allTasks.value, fresh.value) === key).length
  })));

  const ringSegments = computed(() => [
    { label: '执行', value: allTasks.value.filter(t => isRunningStage(t.stage)).length, color: '#31c6e6' },
    { label: '等待', value: allTasks.value.filter(t => ['Created', 'WaitingForVehicle'].includes(t.stage)).length, color: '#e9b34f' },
    { label: '完成', value: allTasks.value.filter(t => t.stage === 'Completed').length, color: '#25d3a4' },
    { label: '异常/其他', value: allTasks.value.filter(t => !isRunningStage(t.stage) && !['Created', 'WaitingForVehicle', 'Completed'].includes(t.stage)).length, color: '#ee668b' }
  ]);

  const mapVehicleCount = computed(() => snapshot.value ? vehicles.value.filter(v => mayPlot(snapshot.value!, v, now.value)).length : 0);

  const previewMap = ref<import('../types').WebMap | null>(null);
  const demoMode = ref(false);
  let demoTimer: ReturnType<typeof setInterval> | undefined;
  let demoStep = 0;

  function connectDemo() {
    stopConnection();
    clearInterval(demoTimer);
    ++epoch;
    demoMode.value = true;
    error.value = '';
    demoStep = 0;
    snapshot.value = demoSnapshot(demoStep);
    demoTimer = setInterval(() => {
      demoStep = (demoStep + 1) % 10;
      snapshot.value = demoSnapshot(demoStep);
    }, 3000);
  }

  function stopDemo() {
    clearInterval(demoTimer);
    demoTimer = undefined;
    demoMode.value = false;
  }

  async function api<T>(path: string, payload?: unknown): Promise<T> {
    const controller = new AbortController();
    controllers.add(controller);
    const timeout = setTimeout(() => controller.abort(), 35000);
    try {
      const response = await fetch(path, {
        method: payload === undefined ? 'GET' : 'POST',
        headers: payload === undefined ? undefined : { 'Content-Type': 'application/json' },
        body: payload === undefined ? undefined : JSON.stringify(payload),
        signal: controller.signal, cache: 'no-store'
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.detail ?? data.error ?? `HTTP ${response.status}`);
      return data;
    } finally {
      clearTimeout(timeout);
      controllers.delete(controller);
    }
  }

  function stopConnection() {
    clearTimeout(reconnectTimer);
    reconnectTimer = undefined;
    socket?.close();
    socket = undefined;
    controllers.forEach(c => c.abort());
    controllers.clear();
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(() => {
      reconnectTimer = undefined;
      void connectBackend();
    }, reconnectDelay);
    reconnectDelay = Math.min(reconnectDelay * 2, 30000);
  }

  async function connectBackend() {
    stopConnection();
    const mine = ++epoch;
    snapshot.value = null;
    error.value = '';
    submissionEnabled.value = false;
    adapterIdentity.value = null;
    reconnectDelay = 1000;

    try {
      const config = await api<{ taskSubmissionEnabled: boolean; adapter?: AdapterIdentity }>('/api/config');
      if (mine !== epoch) return;
      submissionEnabled.value = config.taskSubmissionEnabled;
      adapterIdentity.value = config.adapter ?? null;

      const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/api/ws`);
      socket = ws;

      ws.onmessage = event => {
        if (mine !== epoch) return;
        reconnectDelay = 1000;
        try {
          const incoming = JSON.parse(event.data) as Snapshot;
          if (incoming.source !== 'backend' || typeof incoming.connected !== 'boolean')
            throw new Error('后台快照格式无效');
          if (!incoming.connected) {
            snapshot.value = snapshot.value
              ? { ...snapshot.value, connected: false, error: incoming.error }
              : incoming;
            error.value = incoming.error ?? '后台不可用';
          } else {
            snapshot.value = incoming;
            error.value = '';
          }
        } catch (ex) {
          error.value = String(ex);
        }
      };

      ws.onerror = () => {
        if (mine === epoch) {
          error.value = 'WebSocket 连接失败，正在自动重连…';
          scheduleReconnect();
        }
      };

      ws.onclose = () => {
        if (mine !== epoch) return;
        if (snapshot.value) snapshot.value = { ...snapshot.value, connected: false };
        error.value ||= '后台推送已断开，正在自动重连…';
        scheduleReconnect();
      };
    } catch (ex) {
      if (mine === epoch) {
        error.value = String(ex);
        scheduleReconnect();
      }
    }
  }

  function startNowTimer() {
    nowTimer = setInterval(() => { now.value = Date.now(); }, 1000);
    document.addEventListener('visibilitychange', onVisibilityChange);
  }

  function stopNowTimer() {
    clearInterval(nowTimer);
    document.removeEventListener('visibilitychange', onVisibilityChange);
  }

  function onVisibilityChange() {
    if (document.visibilityState === 'visible' && !socket) {
      reconnectDelay = 1000;
      void connectBackend();
    }
  }

  function dispose() {
    ++epoch;
    stopConnection();
    stopDemo();
    stopNowTimer();
  }

  return {
    snapshot, error, sceneError, now, submissionEnabled, adapterIdentity,
    fresh, vehicles, allTasks, equipment, resources, alarms, openAlarms,
    activeCount, vehicleStats, ringSegments, mapVehicleCount,
    api, connectBackend, connectDemo, stopConnection, dispose,
    startNowTimer, stopNowTimer, previewMap, demoMode
  };
});
