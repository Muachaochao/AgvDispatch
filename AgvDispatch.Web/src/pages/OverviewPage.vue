<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { DispatchScene } from '../DispatchScene';
import {trafficFocus} from '../traffic-focus';
import TrafficRehearsal from '../TrafficRehearsal.vue';
import TrafficWaitingPanel from '../TrafficWaitingPanel.vue';
import {resourceView} from '../waiting';
import { isFresh, isRunningStage, label, mayPlot } from '../state';
import { vehicleCategory, categoryLabels, shortTime, type VehicleCategory } from '../dashboard';
import type { Snapshot } from '../types';
import {
  IconRobot, IconRoute, IconCircleCheck, IconArrowsMaximize, IconPlus,
  IconZoomIn, IconZoomOut, IconCpu, IconNetwork
} from '@tabler/icons-vue';
import RingChart from '../RingChart.vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import TaskExecutionDetails from '../TaskExecutionDetails.vue';

const store = useDispatchStore();
const router = useRouter();

const selectedVehicle = ref('');
const selectedTask = ref('');
const selectedEdge = ref('');
const locatedResource=ref(''),locationMessage=ref('');
const detail = ref<import('../types').TaskDetail | null>(null);
const mapElement = ref<HTMLElement>();
const mapView = ref<'top' | 'isometric'>('top');
const showTaskDialog = ref(false);
const selectedAlarm = ref<import('../types').Alarm | null>(null);
const selectedDevice = ref<import('../types').Device | null>(null);
let scene: DispatchScene | undefined;
let detailEpoch = 0;
let autoSelectTask=true;

const chosenVehicle = computed(() => store.vehicles.find(v => v.vehicleId === selectedVehicle.value));
const selectedRoad = computed(() => store.snapshot?.map?.edges.find(edge => edge.id === selectedEdge.value));

const focus=computed(()=>trafficFocus(store.snapshot,detail.value,store.now,store.demoMode));
function draw() { scene?.update(store.snapshot, selectedVehicle.value, selectedEdge.value,focus.value,store.now); }
watch([focus,()=>store.now],draw);

watch(() => store.snapshot, () => {
  if (!selectedVehicle.value && store.vehicles.length) selectedVehicle.value = store.vehicles[0].vehicleId;
  if (autoSelectTask && store.allTasks.length) selectTask(store.allTasks[0].taskId);
  else if (selectedTask.value) void loadDetail(selectedTask.value);
  draw();
});
watch([selectedVehicle, selectedEdge], draw);
watch(()=>store.demoMode,()=>{++detailEpoch;detail.value=null;selectedTask.value='';autoSelectTask=true;});

async function loadDetail(id: string) {
  const selection = ++detailEpoch;
  if(store.demoMode){detail.value=store.demoTaskDetail(id);return;}
  try {
    const result = await store.api<import('../types').TaskDetail | null>(`/api/tasks/${encodeURIComponent(id)}`);
    if (selection === detailEpoch && selectedTask.value === id && store.fresh && !store.demoMode) detail.value = result;
  } catch { if(selection===detailEpoch)detail.value = null; }
}

function selectTask(id: string) { autoSelectTask=false; ++detailEpoch;selectedTask.value = id; detail.value = null; if(id)void loadDetail(id); }
function openTask(id: string) { showTaskDialog.value = true; selectTask(id); }
function setMapView(mode: 'top' | 'isometric') { mapView.value = mode; scene?.view(mode); }
function clearLocation(){locatedResource.value='';locationMessage.value='';scene?.clearResourceFocus();}
function locateResource(id:string){if(!store.fresh){clearLocation();return;}const view=resourceView(store.snapshot?.map,id);if(!view){clearLocation();locationMessage.value='资源 '+id+' 无有效边界，不能定位；不猜测位置。';return;}scene?.focusResource(id,view);mapElement.value?.scrollIntoView({block:'center'});locatedResource.value=id;locationMessage.value='已定位资源 '+id+'；仅调整视角，不改变资源归属或放行。';}
watch(()=>[store.demoMode,store.snapshot?.map?.id,store.snapshot?.map?.version,store.snapshot?.map?.definitionHash,store.fresh].join('|'),clearLocation);
function fullscreen() { void mapElement.value?.parentElement?.requestFullscreen().catch(() => { store.sceneError = '当前浏览器未允许全屏'; }); }

onMounted(() => {
  try {
    scene = new DispatchScene(mapElement.value!, id => { selectedVehicle.value = id; });
    draw();
  } catch (ex) { store.sceneError = `三维视图初始化失败：${String(ex)}`; }
});
onUnmounted(() => { ++detailEpoch;scene?.dispose(); });
</script>

<template>
<div class="overview-page">
  <TrafficRehearsal v-if="store.demoMode"/>
  <div class="control-grid">
    <aside class="left-column">
      <section class="control-panel statistics-panel">
        <header class="section-heading"><h2>车辆状态统计 <small>FLEET STATUS</small></h2><span class="outline-badge">{{ store.snapshot?.status ? `已观察 ${store.vehicles.length} 辆` : '待采集' }}</span></header>
        <div class="fleet-counts">
          <button v-for="stat in store.vehicleStats.filter(s => s.key !== 'unknown' || s.count > 0)" :key="stat.key" :class="stat.key" @click="router.push({ name: 'vehicles', query: { filter: stat.key } })">
            <i class="status-dot" :class="stat.key"></i><b>{{ stat.count }}</b><span>{{ stat.label }}</span>
          </button>
        </div>
      </section>

      <section class="control-panel vehicle-focus">
        <header class="section-heading"><h2>车辆详情 <small>VEHICLE</small></h2><button class="text-button" @click="router.push('/vehicles')">列表</button></header>
        <template v-if="chosenVehicle">
          <div class="focus-vehicle"><IconRobot :size="35" stroke="1.2" /><div><b>{{ chosenVehicle.vehicleId }}</b><small>{{ chosenVehicle.displayName }}</small></div><span class="state-label" :class="vehicleCategory(chosenVehicle, store.allTasks, store.fresh)">{{ categoryLabels[vehicleCategory(chosenVehicle, store.allTasks, store.fresh)] }}</span></div>
          <div class="battery-line"><span>电量</span><progress max="100" :value="chosenVehicle.batteryPercent ?? 0"></progress><b>{{ chosenVehicle.batteryPercent ?? '—' }}%</b></div>
          <dl class="property-list">
            <dt>当前站点</dt><dd>{{ chosenVehicle.currentStation ?? '未知' }}</dd>
            <dt>导航状态</dt><dd>{{ chosenVehicle.navigationState ?? '未知' }}</dd>
            <dt>原始坐标</dt><dd>{{ chosenVehicle.x?.toFixed(2) ?? '—' }} / {{ chosenVehicle.y?.toFixed(2) ?? '—' }}</dd>
            <dt>定位质量</dt><dd>{{ label(chosenVehicle.quality) }} / {{ chosenVehicle.localizationConfidence ?? '—' }}</dd>
            <dt>地图叠加</dt><dd>{{ store.snapshot && mayPlot(store.snapshot, chosenVehicle, store.now) ? '已核定且有效' : '待核对 / 不显示' }}</dd>
          </dl>
        </template>
        <p v-else class="empty-panel">连接后台后选择车辆</p>
        <div class="vehicle-picker"><button v-for="v in store.vehicles" :key="v.vehicleId" :class="{ selected: v.vehicleId === selectedVehicle }" @click="selectedVehicle = v.vehicleId">{{ v.vehicleId }}</button></div>
      </section>

      <section class="control-panel alert-preview">
        <header class="section-heading"><h2>异常告警 <small>ALARM CENTER</small></h2><button class="text-button" @click="router.push('/alarms')">更多 {{ store.openAlarms.length }}</button></header>
        <div class="mini-table-head"><span>级别 / 对象</span><span>告警信息</span></div>
        <button v-for="alarm in store.openAlarms.slice(0, 5)" :key="alarm.alarmId" class="alarm-preview-row" @click="selectedAlarm = alarm">
          <div><span class="severity" :class="alarm.severity.toLowerCase()">{{ ({ Info:'信息',Warning:'警告',Error:'错误',Critical:'严重' })[alarm.severity] ?? alarm.severity }}</span><small>{{ alarm.sourceId }}</small></div>
          <span>{{ alarm.title }}<small>{{ shortTime(alarm.lastObservedAt) }}</small></span>
        </button>
        <p v-if="!store.openAlarms.length" class="empty-panel"><IconCircleCheck :size="25" />{{ store.fresh ? '暂无未确认告警' : '等待有效告警数据' }}</p>
        <div class="panel-footnote">确认与恢复是不同状态，不在此自动清除告警</div>
      </section>
    </aside>

    <section class="control-panel map-control">
      <header class="section-heading map-heading">
        <div><h2>{{ store.snapshot?.map?.name ?? '当前运行地图' }}</h2><small>{{ store.snapshot?.map?.id ?? '未连接' }} · {{ store.snapshot?.map?.version ?? '—' }}</small></div>
        <div class="map-toolbar"><button :class="{ active: mapView === 'top' }" @click="setMapView('top')">2D</button><button :class="{ active: mapView === 'isometric' }" @click="setMapView('isometric')">3D</button><button class="icon-button" aria-label="全屏地图" @click="fullscreen"><IconArrowsMaximize :size="15" /></button></div>
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
        <label>路段定位<select v-model="selectedEdge" aria-label="选择地图路段"><option value="">选择道路</option><option v-for="edge in store.snapshot?.map?.edges ?? []" :key="edge.id" :value="edge.id">{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</option></select></label>
        <span v-if="selectedRoad">{{ selectedRoad.from }} {{ selectedRoad.bidirectional ? '↔' : '→' }} {{ selectedRoad.to }}</span>
        <span v-else>米制导航图 / {{ store.snapshot?.map?.coordinateSystem ?? '待核定' }}</span>
      </div>
      <section class="traffic-focus" aria-label="当前任务地图联动"><label>联动任务 <select v-model="selectedTask" @change="selectTask(selectedTask)"><option value="">取消联动</option><option v-for="t in store.allTasks" :key="t.taskId" :value="t.taskId">{{t.taskId}} · {{label(t.stage)}}</option></select></label><button :disabled="!selectedTask" @click="showTaskDialog=true">查看步骤与处置</button>
      <template v-if="focus.taskId"><p>任务车辆 {{focus.vehicleId||'未分配'}} · 原命令路线（绿） {{focus.edgeIds.join('、')||'未提供'}}</p><p>持有资源（橙） {{focus.held.join('、')||'无同图返回'}} · 阻塞资源（红） <button v-for="id in focus.blocked" :key="id" @click="locateResource(id)">定位 {{id}}</button>{{focus.blocked.length?'':'无当前阻塞返回'}}</p><p>阻塞车辆 <button v-for="id in focus.blockers" :key="id" @click="selectedVehicle=id">{{id}}</button>{{focus.blockers.length?'':'无当前阻塞返回'}}</p><p v-if="focus.unplotted.length">未画车体（位置未核定或无效）：{{focus.unplotted.join('、')}}；不使用站点或路线端点代替。</p><p>{{focus.reason}}</p><p v-if="focus.truncated">路权记录已截断，当前列表不代表全场。</p></template><p v-else>{{focus.reason}}</p></section>
      <p class="resource-location" role="status">{{locationMessage}} <button v-if="locatedResource" @click="clearLocation">清除资源定位</button></p>
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
        <header class="section-heading"><h2>当前任务 <small>LIVE TASKS</small></h2><button class="text-button" @click="router.push('/tasks')">全部</button></header>
        <div class="current-task-counts"><span>执行 <b>{{ store.ringSegments[0].value }}</b></span><span>等待 <b>{{ store.ringSegments[1].value }}</b></span></div>
        <button v-for="task in store.allTasks.slice(0, 6)" :key="task.taskId" class="live-task" @click="openTask(task.taskId)">
          <div><b>{{ task.taskId }}</b><span class="state-label" :class="task.stage === 'Faulted' ? 'fault' : isRunningStage(task.stage) ? 'running' : 'waiting'">{{ label(task.stage) }}</span></div>
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

  <TrafficWaitingPanel @locate="locateResource" @task="openTask"/>
  <ConsoleDialog v-if="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" wide @close="showTaskDialog = false">
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <p v-else class="empty-panel">{{ store.error || '正在读取任务详情…' }}</p>
  </ConsoleDialog>

  <ConsoleDialog v-if="selectedAlarm" title="告警详情" @close="selectedAlarm = null">
    <div class="detail-summary">
      <div><span>告警编号</span><b>{{ selectedAlarm.alarmId }}</b></div>
      <div><span>关联对象</span><b>{{ selectedAlarm.sourceId }}</b></div>
      <div><span>级别 / 状态</span><b>{{ ({ Info:'信息',Warning:'警告',Error:'错误',Critical:'严重' })[selectedAlarm.severity] ?? selectedAlarm.severity }} / {{ ({ Open:'未确认',Acknowledged:'已确认',Recovered:'已恢复' })[selectedAlarm.status] ?? selectedAlarm.status }}</b></div>
    </div>
    <h3 class="subheading">{{ selectedAlarm.title }}</h3>
    <p class="long-message">{{ selectedAlarm.message }}</p>
    <p class="muted-note">首次发生：{{ shortTime(selectedAlarm.firstOccurredAt) }}<br>最近观察：{{ shortTime(selectedAlarm.lastObservedAt) }}</p>
    <div class="inline-warning">此处只查看，确认及恢复按现有WPF流程处理。</div>
  </ConsoleDialog>

  <ConsoleDialog v-if="selectedDevice" :title="selectedDevice.name + ' · PLC设备状态'" wide @close="selectedDevice = null">
    <p class="form-explanation">{{ selectedDevice.summary }}</p>
    <table class="dialog-table"><thead><tr><th>信号</th><th>值</th><th>质量</th><th>地址</th><th>更新时间</th></tr></thead>
    <tbody><tr v-for="field in selectedDevice.fields" :key="field.name"><td>{{ field.name }}</td><td>{{ field.value }}</td><td>{{ field.quality }}</td><td>{{ field.address }}</td><td>{{ field.updatedAt }}</td></tr></tbody></table>
  </ConsoleDialog>
</div>
</template>

<style scoped>.overview-page{flex:1;min-height:0;overflow:auto;padding-bottom:12px}.overview-page .control-grid{height:max(780px,calc(100dvh - 205px));flex:none;min-height:780px}.traffic-focus{padding:12px 16px;border-top:1px solid #2c4563;font-size:12px;line-height:1.8}.traffic-focus select{max-width:100%}.traffic-focus button{margin:4px}.resource-location{padding:0 16px;font-size:12px;line-height:1.8;color:#c5dbec}.traffic-focus p{overflow-wrap:anywhere;margin:5px 0}</style>
