<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { DispatchScene } from '../DispatchScene';
import { isRunningStage, label, mayPlot } from '../state';
import { vehicleCategory, categoryLabels, shortTime, alarmSeverity, alarmStatus } from '../dashboard';
import { ElTag, ElProgress, ElDialog, ElDescriptions, ElDescriptionsItem, ElButton, ElSelect, ElOption, ElEmpty } from 'element-plus';
import type { Alarm } from '../types';
import { IconRobot, IconRoute, IconCircleCheck, IconArrowsMaximize, IconZoomIn, IconZoomOut } from '@tabler/icons-vue';
import RingChart from '../RingChart.vue';
import TaskExecutionDetails from '../TaskExecutionDetails.vue';

const store = useDispatchStore();
const router = useRouter();

const selectedVehicle = ref('');
const selectedTask = ref('');
const selectedEdge = ref('');
const detail = ref<import('../types').TaskDetail | null>(null);
const mapElement = ref<HTMLElement>();
const mapView = ref<'top' | 'isometric'>('top');
const showTaskDialog = ref(false);
const selectedAlarm = ref<Alarm | null>(null);
const selectedDevice = ref<import('../types').Device | null>(null);
let scene: DispatchScene | undefined;
let detailEpoch = 0;

const chosenVehicle = computed(() => store.vehicles.find(v => v.vehicleId === selectedVehicle.value));
const selectedRoad = computed(() => store.snapshot?.map?.edges.find(edge => edge.id === selectedEdge.value));
const alarmDialogVisible = computed({ get: () => selectedAlarm.value !== null, set: (v: boolean) => { if (!v) selectedAlarm.value = null; } });
const deviceDialogVisible = computed({ get: () => selectedDevice.value !== null, set: (v: boolean) => { if (!v) selectedDevice.value = null; } });

function draw() { scene?.update(store.snapshot, selectedVehicle.value, selectedEdge.value); }

watch(() => store.snapshot, () => {
  if (!selectedVehicle.value && store.vehicles.length) selectedVehicle.value = store.vehicles[0].vehicleId;
  if (!selectedTask.value && store.allTasks.length) selectTask(store.allTasks[0].taskId);
  else if (selectedTask.value) void loadDetail(selectedTask.value);
  draw();
});
watch([selectedVehicle, selectedEdge], draw);

async function loadDetail(id: string) {
  const selection = ++detailEpoch;
  try {
    const result = await store.api<import('../types').TaskDetail | null>(`/api/tasks/${encodeURIComponent(id)}`);
    if (selection === detailEpoch && selectedTask.value === id && store.fresh) detail.value = result;
  } catch { detail.value = null; }
}
function selectTask(id: string) { selectedTask.value = id; detail.value = null; void loadDetail(id); }
function openTask(id: string) { showTaskDialog.value = true; selectTask(id); }
function setMapView(mode: 'top' | 'isometric') { mapView.value = mode; scene?.view(mode); }
function fullscreen() { void mapElement.value?.parentElement?.requestFullscreen().catch(() => { store.sceneError = '当前浏览器未允许全屏'; }); }

onMounted(() => {
  try { scene = new DispatchScene(mapElement.value!, id => { selectedVehicle.value = id; }); draw(); }
  catch (ex) { store.sceneError = `三维视图初始化失败：${String(ex)}`; }
});
onUnmounted(() => { scene?.dispose(); });
</script>

<template>
  <div class="control-grid">
    <aside class="left-column">
      <section class="control-panel statistics-panel">
        <header class="section-heading"><h2>车辆状态统计 <small>FLEET STATUS</small></h2><ElTag size="small" effect="plain" round>{{ store.snapshot?.status ? `已观察 ${store.vehicles.length} 辆` : '待采集' }}</ElTag></header>
        <div class="fleet-counts">
          <button v-for="stat in store.vehicleStats.filter(s => s.key !== 'unknown' || s.count > 0)" :key="stat.key" :class="stat.key" @click="router.push({ path: '/vehicles', query: { filter: stat.key } })">
            <i class="status-dot" :class="stat.key"></i><b>{{ stat.count }}</b><span>{{ stat.label }}</span>
          </button>
        </div>
      </section>

      <section class="control-panel vehicle-focus">
        <header class="section-heading"><h2>车辆详情 <small>VEHICLE</small></h2><ElButton text size="small" @click="router.push('/vehicles')">列表</ElButton></header>
        <template v-if="chosenVehicle">
          <div class="focus-vehicle"><IconRobot :size="35" stroke="1.2" /><div><b>{{ chosenVehicle.vehicleId }}</b><small>{{ chosenVehicle.displayName }}</small></div>
            <ElTag :type="({ running:'success', waiting:'warning', fault:'danger', offline:'info', idle:'primary', charging:'primary', unknown:'info' } as Record<string,any>)[vehicleCategory(chosenVehicle, store.allTasks, store.fresh)] ?? 'info'" size="small" effect="dark">{{ categoryLabels[vehicleCategory(chosenVehicle, store.allTasks, store.fresh)] }}</ElTag>
          </div>
          <div class="battery-line"><span>电量</span><ElProgress :percentage="chosenVehicle.batteryPercent ?? 0" :stroke-width="6" :color="(chosenVehicle.batteryPercent ?? 0) > 20 ? '#23d0a1' : '#f56c6c'" style="flex:1" /><b>{{ chosenVehicle.batteryPercent ?? '—' }}%</b></div>
          <dl class="property-list">
            <dt>当前站点</dt><dd>{{ chosenVehicle.currentStation ?? '未知' }}</dd>
            <dt>导航状态</dt><dd>{{ chosenVehicle.navigationState ?? '未知' }}</dd>
            <dt>原始坐标</dt><dd>{{ chosenVehicle.x?.toFixed(2) ?? '—' }} / {{ chosenVehicle.y?.toFixed(2) ?? '—' }}</dd>
            <dt>定位质量</dt><dd>{{ label(chosenVehicle.quality) }} / {{ chosenVehicle.localizationConfidence ?? '—' }}</dd>
            <dt>地图叠加</dt><dd>{{ store.snapshot && mayPlot(store.snapshot, chosenVehicle, store.now) ? '已核定且有效' : '待核对 / 不显示' }}</dd>
          </dl>
        </template>
        <p v-else class="empty-panel">连接后台后选择车辆</p>
        <div class="vehicle-picker">
          <ElButton v-for="v in store.vehicles" :key="v.vehicleId" :type="v.vehicleId === selectedVehicle ? 'primary' : 'default'" size="small" @click="selectedVehicle = v.vehicleId">{{ v.vehicleId }}</ElButton>
        </div>
      </section>

      <section class="control-panel alert-preview">
        <header class="section-heading"><h2>异常告警 <small>ALARM CENTER</small></h2><ElButton text size="small" @click="router.push('/alarms')">更多 {{ store.openAlarms.length }}</ElButton></header>
        <div class="mini-table-head"><span>级别 / 对象</span><span>告警信息</span></div>
        <button v-for="alarm in store.openAlarms.slice(0, 5)" :key="alarm.alarmId" class="alarm-preview-row" @click="selectedAlarm = alarm">
          <div><ElTag :type="alarm.severity === 'Error' || alarm.severity === 'Critical' ? 'danger' : 'warning'" size="small" effect="dark">{{ alarmSeverity(alarm.severity) }}</ElTag><small>{{ alarm.sourceId }}</small></div>
          <span>{{ alarm.title }}<small>{{ shortTime(alarm.lastObservedAt) }}</small></span>
        </button>
        <p v-if="!store.openAlarms.length" class="empty-panel"><IconCircleCheck :size="25" />{{ store.fresh ? '暂无未确认告警' : '等待有效告警数据' }}</p>
        <div class="panel-footnote">确认与恢复是不同状态，不在此自动清除告警</div>
      </section>
    </aside>

    <section class="control-panel map-control">
      <header class="section-heading map-heading">
        <div><h2>{{ store.snapshot?.map?.name ?? '当前运行地图' }}</h2><small>{{ store.snapshot?.map?.id ?? '未连接' }} · {{ store.snapshot?.map?.version ?? '—' }}</small></div>
        <div class="map-toolbar">
          <ElButton :type="mapView === 'top' ? 'primary' : 'default'" size="small" @click="setMapView('top')">2D</ElButton>
          <ElButton :type="mapView === 'isometric' ? 'primary' : 'default'" size="small" @click="setMapView('isometric')">3D</ElButton>
          <ElButton size="small" circle @click="fullscreen"><IconArrowsMaximize :size="15" /></ElButton>
        </div>
      </header>
      <div class="map-viewport">
        <div ref="mapElement" class="scene-container" aria-label="可交互的运行地图"></div>
        <div v-if="store.sceneError || !store.snapshot?.map" class="scene-empty">{{ store.sceneError || '尚未取得后台运行地图。请确认C#主服务已启动、管道名称一致且已激活运行地图。' }}</div>
        <div class="map-status-legend"><span><i class="status-dot running"></i>执行</span><span><i class="status-dot waiting"></i>等待</span><span><i class="status-dot fault"></i>故障</span><span><i class="status-dot offline"></i>离线</span></div>
        <div class="map-navigation"><IconRoute :size="15" /><span>厂家站点与导航道路</span><small>显示不作为执行放行依据</small></div>
        <div class="map-zoom"><button aria-label="缩小" @click="scene?.zoom(1.2)"><IconZoomOut :size="16" /></button><button @click="scene?.view(mapView)">复位</button><button aria-label="放大" @click="scene?.zoom(0.8)"><IconZoomIn :size="16" /></button></div>
        <div class="map-caption-bottom"><span>{{ store.snapshot?.map?.nodes.length ?? 0 }} 节点 / {{ store.snapshot?.map?.edges.length ?? 0 }} 道路</span><span>{{ store.mapVehicleCount }} 辆可定位 · {{ mapView === 'top' ? '右键平移 · 滚轮缩放' : '左键旋转 · 右键平移' }}</span></div>
      </div>
      <div class="map-bottom-tools">
        <label>路段定位
          <ElSelect v-model="selectedEdge" placeholder="选择道路" size="small" clearable filterable style="max-width:270px">
            <ElOption v-for="edge in store.snapshot?.map?.edges ?? []" :key="edge.id" :label="`${edge.id} · ${edge.from} → ${edge.to}`" :value="edge.id" />
          </ElSelect>
        </label>
        <span v-if="selectedRoad">{{ selectedRoad.from }} {{ selectedRoad.bidirectional ? '↔' : '→' }} {{ selectedRoad.to }}</span>
        <span v-else>米制导航图 / {{ store.snapshot?.map?.coordinateSystem ?? '待核定' }}</span>
      </div>
      <div v-for="warning in store.snapshot?.map?.warnings ?? []" :key="warning" class="inline-warning">{{ warning }}</div>
    </section>

    <aside class="right-column">
      <section class="control-panel distribution-panel">
        <header class="section-heading"><h2>任务状态分布 <small>TASK STATUS</small></h2></header>
        <RingChart :segments="store.ringSegments" :total="store.allTasks.length" />
        <div class="ring-legend"><span v-for="segment in store.ringSegments" :key="segment.label"><i class="status-dot" :style="{ background: segment.color }"></i>{{ segment.label }}<b>{{ segment.value }}</b></span></div>
        <p class="panel-footnote">当前快照最多50条，不代表全历史统计</p>
      </section>

      <section class="control-panel live-tasks">
        <header class="section-heading"><h2>当前任务 <small>LIVE TASKS</small></h2><ElButton text size="small" @click="router.push('/tasks')">全部</ElButton></header>
        <div class="current-task-counts"><span>执行 <b>{{ store.ringSegments[0].value }}</b></span><span>等待 <b>{{ store.ringSegments[1].value }}</b></span></div>
        <button v-for="task in store.allTasks.slice(0, 6)" :key="task.taskId" class="live-task" @click="openTask(task.taskId)">
          <div><b>{{ task.taskId }}</b>
            <ElTag :type="task.stage === 'Faulted' ? 'danger' : isRunningStage(task.stage) ? 'success' : 'warning'" size="small" effect="dark">{{ label(task.stage) }}</ElTag>
          </div>
          <p>{{ task.stationId }}<span>{{ task.vehicleId ?? '待分配' }}</span></p>
          <small>{{ task.errorMessage || '点击查看步骤及PLC条件' }}</small>
        </button>
        <p v-if="!store.allTasks.length" class="empty-panel">暂无任务数据</p>
      </section>

      <section class="control-panel shared-panel">
        <header class="section-heading"><h2>共享区域 <small>TRAFFIC</small></h2></header>
        <div v-for="resource in store.resources" :key="resource.resourceId" class="shared-resource">
          <span><i class="status-dot" :class="resource.occupiedCount ? 'amber' : 'green'"></i>{{ resource.resourceId }}</span>
          <b>{{ resource.occupiedCount }}/{{ resource.capacity }}</b>
          <small>{{ resource.leases.map(l => l.vehicleId).join('、') || (resource.occupiedCount ? '已占用' : '空闲') }}</small>
        </div>
        <p v-if="!store.resources.length" class="empty-panel">暂无资源数据</p>
      </section>
    </aside>
  </div>

  <ElDialog v-model="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" width="1000px" destroy-on-close>
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <ElEmpty v-else :description="store.error || '正在读取任务详情…'" />
  </ElDialog>

  <ElDialog v-model="alarmDialogVisible" title="告警详情" width="620px">
    <template v-if="selectedAlarm">
      <ElDescriptions :column="2" border size="small">
        <ElDescriptionsItem label="告警编号">{{ selectedAlarm.alarmId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="关联对象">{{ selectedAlarm.sourceId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="级别 / 状态">{{ alarmSeverity(selectedAlarm.severity) }} / {{ alarmStatus(selectedAlarm.status) }}</ElDescriptionsItem>
        <ElDescriptionsItem label="代码">{{ selectedAlarm.code }}</ElDescriptionsItem>
      </ElDescriptions>
      <h3 class="subheading">{{ selectedAlarm.title }}</h3>
      <p class="long-message">{{ selectedAlarm.message }}</p>
      <p class="muted-note">首次发生：{{ shortTime(selectedAlarm.firstOccurredAt) }}<br>最近观察：{{ shortTime(selectedAlarm.lastObservedAt) }}<br>恢复时间：{{ shortTime(selectedAlarm.recoveredAt) }}</p>
      <div class="inline-warning">此处只查看，确认及恢复按现有WPF流程处理。</div>
    </template>
  </ElDialog>

  <ElDialog v-model="deviceDialogVisible" :title="(selectedDevice?.name ?? '') + ' · PLC设备状态'" width="800px">
    <template v-if="selectedDevice">
      <p class="form-explanation">{{ selectedDevice.summary }}</p>
      <table class="dialog-table"><thead><tr><th>信号</th><th>值</th><th>质量</th><th>地址</th><th>更新时间</th></tr></thead>
      <tbody><tr v-for="field in selectedDevice.fields" :key="field.name"><td>{{ field.name }}</td><td>{{ field.value }}</td><td>{{ field.quality }}</td><td>{{ field.address }}</td><td>{{ field.updatedAt }}</td></tr></tbody></table>
    </template>
  </ElDialog>
</template>
