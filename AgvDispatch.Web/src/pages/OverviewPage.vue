<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { DispatchScene } from '../DispatchScene';
import {trafficFocus} from '../traffic-focus';
import TrafficRehearsal from '../TrafficRehearsal.vue';
import TrafficWaitingPanel from '../TrafficWaitingPanel.vue';
import {resourceView} from '../waiting';
import {taskWait,leaseOwners} from '../traffic-display';
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
import {appliesToMap,type DisplayArea,type DisplayAreaContext} from '../display-areas';

const store = useDispatchStore();
const router = useRouter();

const selectedVehicle = ref('');
const selectedTask = ref('');
const selectedEdge = ref('');
const locatedResource=ref(''),locationMessage=ref('');
const detail = ref<import('../types').TaskDetail | null>(null);
const mapElement = ref<HTMLElement>();
const mapView = ref<'top' | 'isometric'>('top');
const mapLabels = ref<'auto'|'all'|'none'>('auto');
const showTaskDialog = ref(false);
const selectedAlarm = ref<import('../types').Alarm | null>(null);
const selectedDevice = ref<import('../types').Device | null>(null);
let scene: DispatchScene | undefined;
let detailEpoch = 0;
let autoSelectTask=true;
const areaContext=ref<DisplayAreaContext|null>(null),areaMessage=ref(''),chosenArea=ref<DisplayArea|null>(null);
let areaTimer:ReturnType<typeof setInterval>|undefined,areaEpoch=0,areaLoading=false;
const shownAreas=computed(()=>areaContext.value&&appliesToMap(areaContext.value,store.snapshot?.map??null)?areaContext.value.document?.areas??[]:[]);
function drawAreas(){scene?.setDisplayAreas(shownAreas.value,JSON.stringify([store.snapshot?.map?.id,store.snapshot?.map?.version,shownAreas.value]));}
async function readAreas(){
 if(areaLoading)return;const map=store.snapshot?.map;if(!map?.id||store.demoMode||store.snapshot?.source!=='backend')return;
 const mine=areaEpoch;areaLoading=true;
 try{const c=await store.api<DisplayAreaContext>(`/api/maps/${encodeURIComponent(map.id)}/display-areas`);if(mine!==areaEpoch)return;areaContext.value=c;areaMessage.value=c.applicable?'':'展示分区与地图站点不一致，请到工程编辑的“展示区域”核对保存。';if(chosenArea.value)chosenArea.value=shownAreas.value.find(a=>a.id===chosenArea.value!.id)??null;drawAreas();}
 catch(ex){if(mine===areaEpoch){areaContext.value=null;chosenArea.value=null;areaMessage.value=`展示分区读取失败：${String(ex)}`;drawAreas();}}finally{areaLoading=false;}
}
watch(()=>[store.demoMode,store.snapshot?.source,store.snapshot?.map?.id,store.snapshot?.map?.version,store.snapshot?.map?.definitionHash].join('|'),()=>{++areaEpoch;areaContext.value=null;chosenArea.value=null;areaMessage.value='';drawAreas();void readAreas();});

const liveTasks=computed(()=>store.allTasks.filter(task=>!['Completed','Cancelled'].includes(task.stage)).map(task=>({task,wait:taskWait(store.snapshot,task,store.now)})));
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
    scene = new DispatchScene(mapElement.value!, id => { selectedVehicle.value = id; },area=>{chosenArea.value=area;});
    draw();
    void readAreas();areaTimer=setInterval(()=>{void readAreas();},5000);
  } catch (ex) { store.sceneError = `三维视图初始化失败：${String(ex)}`; }
});
onUnmounted(() => { ++detailEpoch;++areaEpoch;clearInterval(areaTimer);scene?.dispose(); });
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
        <header class="section-heading"><h2>车辆详情 <small>VEHICLE</small></h2><button class="text-button" @click="router.push('/vehicles')"><ActionIcon name="list"/>列表</button></header>
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
        <div><h2>{{ store.snapshot?.map?.name ?? '当前运行地图' }}</h2><small>{{ store.snapshot?.map?.id ?? (store.fresh ? '未激活' : '未连接') }} · {{ store.snapshot?.map?.version ?? '—' }}</small></div>
        <div class="map-toolbar"><select v-model="mapLabels" aria-label="站点标签" title="密集时自动收起部分标签；悬停节点查看" @change="scene?.setLabelMode(mapLabels)"><option value="auto">标签 · 自动</option><option value="all">全部站点</option><option value="none">隐藏站点</option></select><button :class="{ active: mapView === 'top' }" @click="setMapView('top')">2D</button><button :class="{ active: mapView === 'isometric' }" @click="setMapView('isometric')">3D</button><button class="icon-button" aria-label="全屏地图" @click="fullscreen"><IconArrowsMaximize :size="15" /></button></div>
      </header>
      <div class="map-viewport">
        <div ref="mapElement" class="scene-container" aria-label="可交互的运行地图"></div>
        <div v-if="store.sceneError || !store.snapshot?.map" class="scene-empty">{{ store.sceneError || '尚未取得后台运行地图。请确认C#主服务已启动、管道名称一致且已激活运行地图。' }}</div>
        <div class="map-status-legend"><span><i class="status-dot running"></i>执行</span><span><i class="status-dot waiting"></i>等待</span><span><i class="status-dot fault"></i>故障</span><span><i class="status-dot offline"></i>离线</span></div>
        <div class="map-navigation"><IconRoute :size="15" /><span>厂家站点与导航道路</span><small>显示不作为执行放行依据</small></div>
        <div class="map-zoom"><button aria-label="缩小" @click="scene?.zoom(1.2)"><IconZoomOut :size="16" /></button><button @click="scene?.view(mapView)"><ActionIcon name="fit"/>复位</button><button aria-label="放大" @click="scene?.zoom(0.8)"><IconZoomIn :size="16" /></button></div>
        <div class="map-caption-bottom"><span>{{ store.snapshot?.map?.nodes.length ?? 0 }} 节点 / {{ store.snapshot?.map?.edges.length ?? 0 }} 道路</span><span>{{ store.mapVehicleCount }} 辆可定位 · {{ mapView === 'top' ? '右键平移 · 滚轮缩放' : '左键旋转 · 右键平移' }}</span></div>
      </div>
      <div class="map-bottom-tools">
        <label>展示区域<select aria-label="查看展示区域" :value="chosenArea?.id??''" @change="chosenArea=shownAreas.find(a=>a.id===($event.target as HTMLSelectElement).value)??null"><option value="">选择区域查看站点</option><option v-for="a in shownAreas" :key="a.id" :value="a.id">{{a.name}} · {{a.stationIds.length}}站点</option></select></label>
        <label>路段定位<select v-model="selectedEdge" aria-label="选择地图路段"><option value="">选择道路</option><option v-for="edge in store.snapshot?.map?.edges ?? []" :key="edge.id" :value="edge.id">{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</option></select></label>
        <span v-if="selectedRoad">{{ selectedRoad.from }} {{ selectedRoad.bidirectional ? '↔' : '→' }} {{ selectedRoad.to }}</span>
        <span v-else>米制导航图 / {{ store.snapshot?.map?.coordinateSystem ?? '待核定' }}</span>
      </div>
      <p v-if="chosenArea" class="resource-location"><strong :style="{color:chosenArea.color}">{{chosenArea.name}}</strong> · 包含站点：{{chosenArea.stationIds.map(id=>store.snapshot?.map?.nodes.find(n=>n.id===id)?.agvSiteId||id).join('、')||'未分配站点'}} <button @click="chosenArea=null">关闭区域详情</button></p>
      <p v-if="areaMessage" role="status" class="inline-warning">{{areaMessage}}</p>
      <section class="traffic-focus" aria-label="当前任务地图联动"><label>联动任务 <select v-model="selectedTask" @change="selectTask(selectedTask)"><option value="">取消联动</option><option v-for="t in store.allTasks" :key="t.taskId" :value="t.taskId">{{t.taskId}} · {{label(t.stage)}}</option></select></label><button :disabled="!selectedTask" @click="showTaskDialog=true"><ActionIcon name="list"/>查看步骤与处置</button>
      <template v-if="focus.taskId"><p>任务车辆 {{focus.vehicleId||'未分配'}} · 原命令路线（绿） {{focus.edgeIds.join('、')||'未提供'}}</p><p>持有资源（橙） {{focus.held.join('、')||'无同图返回'}} · 阻塞资源（红） <button v-for="id in focus.blocked" :key="id" @click="locateResource(id)">定位 {{id}}</button>{{focus.blocked.length?'':'无当前阻塞返回'}}</p><p>阻塞车辆 <button v-for="id in focus.blockers" :key="id" @click="selectedVehicle=id">{{id}}</button>{{focus.blockers.length?'':'无当前阻塞返回'}}</p><p v-if="focus.unplotted.length">未画车体（位置未核定或无效）：{{focus.unplotted.join('、')}}；不使用站点或路线端点代替。</p><p>{{focus.reason}}</p><p v-if="focus.truncated">路权记录已截断，当前列表不代表全场。</p></template><p v-else>{{focus.reason}}</p></section>
      <p class="resource-location" role="status">{{locationMessage}} <button v-if="locatedResource" @click="clearLocation"><ActionIcon name="close"/>清除资源定位</button></p>
      <div v-for="warning in store.snapshot?.map?.warnings ?? []" :key="warning" class="inline-warning">{{ warning }}</div>
    </section>

    <aside class="right-column">
      <section class="control-panel distribution-panel">
        <header class="section-heading"><h2>任务业务阶段分布 <small>TASK STAGES</small></h2></header>
        <RingChart :segments="store.ringSegments" :total="store.taskSummary?.total ?? 0" :available="!!store.taskSummary" :caption="store.demoMode ? '示例任务总数' : '全部任务总数'" />
        <div class="ring-legend"><span v-for="segment in store.ringSegments" :key="segment.label"><i class="status-dot" :style="{ background: segment.color }"></i>{{ segment.label }}<b>{{ segment.value }}</b></span></div>
        <p class="panel-footnote">{{ store.taskSummary ? (store.demoMode ? '浏览器示例统计。' : '后台全量统计，包含历史任务；已取消单独统计。') : '后台全量统计暂不可用；当前加载记录不代表全部任务。' }}<RouterLink to="/tasks">按时间查询历史</RouterLink></p>
      </section>

      <section class="control-panel live-tasks">
        <header class="section-heading"><h2>当前任务 <small>LIVE TASKS</small></h2><button class="text-button" @click="router.push('/tasks')"><ActionIcon name="list"/>全部</button></header>
        <div class="current-task-counts"><span>执行阶段 <b>{{ store.ringSegments[0].value }}</b></span><span>待派车 <b>{{ store.ringSegments[1].value }}</b></span></div>
        <button v-for="{task,wait} in liveTasks.slice(0, 6)" :key="task.taskId" class="live-task" @click="openTask(task.taskId)">
          <div><b>{{ task.taskId }}</b><span class="state-label" :class="task.stage === 'Faulted' ? 'fault' : wait ? 'waiting' : isRunningStage(task.stage) ? 'running' : 'waiting'">{{ wait?.label ?? label(task.stage) }}</span></div><small v-if="wait">业务阶段：{{label(task.stage)}} · {{wait.count}}项等待{{wait.truncated?'（观察已截断）':''}}</small>
          <p>{{ task.stationId }}<span>{{ task.vehicleId ?? '待分配' }}</span></p>
          <small>{{ wait?.reason || task.errorMessage || '点击查看步骤及PLC条件' }}</small>
        </button>
        <p v-if="!liveTasks.length" class="empty-panel">当前没有未结束任务，历史任务可按时间查询。</p>
      </section>

      <section class="control-panel shared-panel">
        <header class="section-heading"><h2>共享区域 <small>TRAFFIC</small></h2></header>
        <div v-for="resource in store.resources" :key="resource.resourceId" class="shared-resource">
          <span><i class="status-dot" :class="resource.occupiedCount ? 'amber' : 'green'"></i>{{ resource.resourceId }}</span>
          <b>{{ resource.occupiedCount }}/{{ resource.capacity }}</b>
          <small v-if="!resource.leases.length">{{resource.occupiedCount?'已占用，归属待核对':'空闲'}}</small><details v-for="owner in leaseOwners(resource.leases)" :key="JSON.stringify([owner.taskId,owner.vehicleId])"><summary>{{owner.vehicleId||'车辆未知'}} · {{owner.leaseCount}}条租约</summary><small>任务 {{owner.taskId}}；同任务多步骤合并展示，容量仍按后台统计。逐步骤归属见等待队列的阻塞链。</small></details>
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

<style scoped>.overview-page{flex:1;min-height:0;overflow:auto;padding-bottom:12px}.overview-page .control-grid{height:max(780px,calc(100dvh - 205px));flex:none;min-height:780px}.live-tasks,.shared-panel{overflow:auto}.live-tasks{min-height:290px}.right-column{min-height:760px}.distribution-panel>.panel-footnote{flex-shrink:0}.live-tasks>header,.live-tasks>.current-task-counts,.live-task{flex-shrink:0}.live-task>div{flex-wrap:wrap;gap:6px}.live-task b{overflow-wrap:anywhere;min-width:0;white-space:normal;flex:1 1 120px}.live-task small{white-space:normal;overflow-wrap:anywhere;display:block;-webkit-line-clamp:unset;overflow:visible}.shared-resource details{grid-column:1/-1;min-width:0;font-size:11px;overflow-wrap:anywhere}.shared-resource summary{cursor:pointer}.traffic-focus{padding:12px 16px;border-top:1px solid #2c4563;font-size:12px;line-height:1.8}.traffic-focus select{max-width:100%}.traffic-focus button{margin:4px}.resource-location{padding:0 16px;font-size:12px;line-height:1.8;color:#c5dbec}.traffic-focus p{overflow-wrap:anywhere;margin:5px 0}@media(min-width:901px){.overview-page .control-grid{height:max(820px,calc(100dvh - 205px));min-height:820px}}</style>
<style scoped>
.right-column{grid-template-rows:310px minmax(180px,1fr) auto}
.ring-legend{grid-template-columns:repeat(3,minmax(0,1fr));gap:8px 5px}
.distribution-panel>.panel-footnote{line-height:1.6;font-size:10px;width:100%}
.distribution-panel>.panel-footnote a{display:inline-block;margin-left:6px;color:var(--cyan,#3dcce9)}
.map-toolbar{flex-shrink:0;align-items:center}.map-toolbar select{max-width:110px;padding:4px 6px;font-size:11px;min-height:27px}
@media(max-width:900px){.right-column{grid-template-rows:auto auto auto}}
</style>
