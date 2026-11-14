<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { DispatchScene } from '../DispatchScene';
import {trafficFocus} from '../traffic-focus';
import TrafficRehearsal from '../TrafficRehearsal.vue';
import TrafficWaitingPanel from '../TrafficWaitingPanel.vue';
import TrafficClosuresPanel from '../TrafficClosuresPanel.vue';
import {resourceView} from '../waiting';
import {taskWait,leaseOwners} from '../traffic-display';
import { isFresh, isWaitingStage, label, mayPlot } from '../state';
import { vehicleCategory, categoryLabels, shortTime, navigationStateLabel, alarmStatus, alarmSeverity } from '../dashboard';
import { alarmMessageText, alarmSourceLabel, alarmTitleText, qualityLabel, signalLabel } from '../operator-labels';
import { vehicleObstacle } from '../obstacles';
import VehicleObstacleDetails from '../components/VehicleObstacleDetails.vue';
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
const showObstacleDialog = ref(false);
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
const chosenObstacle = computed(() => chosenVehicle.value ? vehicleObstacle(chosenVehicle.value, store.fresh, store.now) : null);
const selectedRoad = computed(() => store.snapshot?.map?.edges.find(edge => edge.id === selectedEdge.value));

const focus=computed(()=>trafficFocus(store.snapshot,detail.value,store.now,store.demoMode));
const mapContextText=computed(()=>{
  if(selectedRoad.value)return `${selectedRoad.value.from} ${selectedRoad.value.bidirectional?'↔':'→'} ${selectedRoad.value.to}`;
  if(focus.value.taskId)return `${focus.value.vehicleId||'车辆待分配'} · ${focus.value.edgeIds.length} 段原命令路线 · 持有 ${focus.value.held.length} · 阻塞 ${focus.value.blocked.length}`;
  return '点击右侧当前任务可联动原命令路线';
});
const taskStatusTone=(stage:string,waiting=false)=>stage==='Faulted'?'fault':['Cancelled','CancellationPending','CancellationReview'].includes(stage)?'cancelled':stage==='Completed'?'completed':waiting||isWaitingStage(stage)?'waiting':'running';
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
        <header class="section-heading"><h2>车辆状态统计</h2><span class="outline-badge">{{ store.snapshot?.status ? `已观察 ${store.vehicles.length} 辆` : '待采集' }}</span></header>
        <div class="fleet-counts">
          <button v-for="stat in store.vehicleStats.filter(s => s.key !== 'unknown' || s.count > 0)" :key="stat.key" :class="stat.key" @click="router.push({ name: 'vehicles', query: { filter: stat.key } })">
            <i class="status-dot" :class="stat.key"></i><b>{{ stat.count }}</b><span>{{ stat.label }}</span>
          </button>
        </div>
      </section>

      <section class="control-panel vehicle-focus">
        <header class="section-heading"><h2>车辆详情</h2><button class="text-button" @click="router.push('/vehicles')"><ActionIcon name="list"/>列表</button></header>
        <template v-if="chosenVehicle">
          <div class="focus-vehicle"><IconRobot :size="35" stroke="1.2" /><div><b>{{ chosenVehicle.vehicleId }}</b><small>{{ chosenVehicle.displayName }}</small></div><span class="state-label" :class="vehicleCategory(chosenVehicle, store.allTasks, store.fresh, store.now)">{{ categoryLabels[vehicleCategory(chosenVehicle, store.allTasks, store.fresh, store.now)] }}</span></div>
          <div class="battery-line"><span>电量</span><progress max="100" :value="chosenVehicle.batteryPercent ?? 0"></progress><b>{{ chosenVehicle.batteryPercent ?? '—' }}%</b></div>
          <dl class="property-list">
            <dt>当前站点</dt><dd>{{ chosenVehicle.currentStation ?? '未知' }}</dd>
            <dt>导航状态</dt><dd>{{ navigationStateLabel(chosenVehicle.navigationState) }}</dd>
            <dt>障碍状态</dt><dd>{{ chosenObstacle?.label }}<template v-if="chosenObstacle?.tone === 'blocked'"> · {{ chosenObstacle.duration }}</template></dd>
            <dt>原始坐标</dt><dd>{{ chosenVehicle.x?.toFixed(2) ?? '—' }} / {{ chosenVehicle.y?.toFixed(2) ?? '—' }}</dd>
            <dt>定位质量</dt><dd>{{ label(chosenVehicle.quality) }} / {{ chosenVehicle.localizationConfidence ?? '—' }}</dd>
            <dt>地图叠加</dt><dd>{{ store.snapshot && mayPlot(store.snapshot, chosenVehicle, store.now) ? '已核定且有效' : '待核对 / 不显示' }}</dd>
          </dl>
          <button class="text-button obstacle-link" @click="showObstacleDialog = true">查看障碍处置与绕行证据</button>
        </template>
        <p v-else class="empty-panel">连接后台后选择车辆</p>
        <div class="vehicle-picker"><button v-for="v in store.vehicles" :key="v.vehicleId" :class="{ selected: v.vehicleId === selectedVehicle }" @click="selectedVehicle = v.vehicleId">{{ v.vehicleId }}</button></div>
      </section>

      <section class="control-panel alert-preview">
        <header class="section-heading"><h2>异常告警</h2><button class="text-button" title="确认与恢复是不同状态，请进入告警页核对" @click="router.push('/alarms')">更多 {{ store.openAlarms.length }}</button></header>
        <div class="mini-table-head"><span>级别 / 对象</span><span>告警信息</span></div>
        <button v-for="alarm in store.openAlarms.slice(0, 2)" :key="alarm.alarmId" class="alarm-preview-row" @click="selectedAlarm = alarm">
          <div><span class="severity" :class="alarm.severity.toLowerCase()">{{ ({ Info:'信息',Warning:'警告',Error:'错误',Critical:'严重' })[alarm.severity] ?? alarm.severity }}</span><small>{{ alarm.sourceId }}</small></div>
          <span>{{ alarmTitleText(alarm.title) }}<small>{{ shortTime(alarm.lastObservedAt) }}</small></span>
        </button>
        <p v-if="!store.openAlarms.length" class="empty-panel"><IconCircleCheck :size="25" />{{ store.fresh ? '暂无未确认告警' : '等待有效告警数据' }}</p>
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
        <div class="map-status-legend"><span><i class="status-dot running"></i>执行</span><span><i class="status-dot waiting"></i>等待</span><span><i class="status-dot blocked"></i>受阻</span><span><i class="status-dot fault"></i>故障</span><span><i class="status-dot offline"></i>离线</span></div>
        <div class="map-navigation"><IconRoute :size="15" /><span>厂家站点与导航道路</span><small>显示不作为执行放行依据</small></div>
        <div class="map-zoom"><button aria-label="缩小" @click="scene?.zoom(1.2)"><IconZoomOut :size="16" /></button><button @click="scene?.view(mapView)"><ActionIcon name="fit"/>复位</button><button aria-label="放大" @click="scene?.zoom(0.8)"><IconZoomIn :size="16" /></button></div>
        <div class="map-caption-bottom"><span>{{ store.snapshot?.map?.nodes.length ?? 0 }} 节点 / {{ store.snapshot?.map?.edges.length ?? 0 }} 道路</span><span>{{ store.mapVehicleCount }} 辆可定位 · {{ mapView === 'top' ? '右键平移 · 滚轮缩放' : '左键旋转 · 右键平移' }}</span></div>
      </div>
      <div class="map-bottom-tools">
        <label>展示区域<select aria-label="查看展示区域" :value="chosenArea?.id??''" @change="chosenArea=shownAreas.find(a=>a.id===($event.target as HTMLSelectElement).value)??null"><option value="">选择区域查看站点</option><option v-for="a in shownAreas" :key="a.id" :value="a.id">{{a.name}} · {{a.stationIds.length}}站点</option></select></label>
        <label>路段定位<select v-model="selectedEdge" aria-label="选择地图路段"><option value="">选择道路</option><option v-for="edge in store.snapshot?.map?.edges ?? []" :key="edge.id" :value="edge.id">{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</option></select></label>
        <span class="map-context-status" :title="mapContextText">{{ mapContextText }}</span>
      </div>
      <p v-if="chosenArea" class="resource-location"><strong :style="{color:chosenArea.color}">{{chosenArea.name}}</strong> · 包含站点：{{chosenArea.stationIds.map(id=>store.snapshot?.map?.nodes.find(n=>n.id===id)?.agvSiteId||id).join('、')||'未分配站点'}} <button @click="chosenArea=null">关闭区域详情</button></p>
      <p v-if="areaMessage" role="status" class="inline-warning">{{areaMessage}}</p>
      <p class="resource-location" role="status">{{locationMessage}} <button v-if="locatedResource" @click="clearLocation"><ActionIcon name="close"/>清除资源定位</button></p>
      <div v-for="warning in store.snapshot?.map?.warnings ?? []" :key="warning" class="inline-warning">{{ warning }}</div>
    </section>

    <aside class="right-column">
      <section class="control-panel distribution-panel">
        <header class="section-heading"><h2>任务业务阶段分布</h2><button class="chart-history" @click="router.push('/tasks')">查询历史</button></header>
        <RingChart :segments="store.ringSegments" :total="store.taskSummary?.total ?? 0" :available="!!store.taskSummary" caption="任务总数" />
        <div class="ring-legend"><span v-for="segment in store.ringSegments" :key="segment.label"><i :style="{ background: segment.color }"></i><em>{{ segment.label }}</em><b>{{ segment.value }}</b></span></div>
      </section>

      <section class="control-panel live-tasks">
        <header class="section-heading"><h2>当前任务</h2><button class="text-button" @click="router.push('/tasks')"><ActionIcon name="list"/>全部</button></header>
        <div class="current-task-counts"><span>执行阶段 <b>{{ store.ringSegments[0].value }}</b></span><span>待派车 <b>{{ store.ringSegments[1].value }}</b></span></div>
        <button v-for="{task,wait} in liveTasks.slice(0, 6)" :key="task.taskId" class="live-task" @click="openTask(task.taskId)">
          <div><b>{{ task.taskId }}</b><span class="state-label" :class="taskStatusTone(task.stage, !!wait)">{{ wait?.label ?? label(task.stage) }}</span></div><small v-if="wait">业务阶段：{{label(task.stage)}} · {{wait.count}}项等待{{wait.truncated?'（观察已截断）':''}}</small>
          <p>{{ task.stationId }}<span>{{ task.vehicleId ?? '待分配' }}</span></p>
          <small>{{ wait?.reason || task.errorMessage || '点击查看步骤及PLC条件' }}</small>
        </button>
        <p v-if="!liveTasks.length" class="empty-panel">当前没有未结束任务，历史任务可按时间查询。</p>
      </section>

      <section class="control-panel shared-panel">
        <header class="section-heading"><h2>共享区域</h2></header>
        <div v-for="resource in store.resources" :key="resource.resourceId" class="shared-resource">
          <span><i class="status-dot" :class="resource.occupiedCount ? 'amber' : 'green'"></i>{{ resource.resourceId }}</span>
          <b>{{ resource.occupiedCount }}/{{ resource.capacity }}</b>
          <small v-if="!resource.leases.length">{{resource.occupiedCount?'已占用，归属待核对':'空闲'}}</small><details v-for="owner in leaseOwners(resource.leases)" :key="JSON.stringify([owner.taskId,owner.vehicleId])"><summary>{{owner.vehicleId||'车辆未知'}} · {{owner.leaseCount}}条租约</summary><small>任务 {{owner.taskId}}；同任务多步骤合并展示，容量仍按后台统计。逐步骤归属见等待队列的阻塞链。</small></details>
        </div>
        <p v-if="!store.resources.length" class="empty-panel">暂无资源数据</p>
      </section>
    </aside>
  </div>

  <section class="overview-lower-deck" aria-label="运行保障详情">
    <header><div><small>运行保障详情</small><h2>道路封锁 · 交通等待 · 资源占用</h2></div><span>向下滚动查看完整记录</span></header>
    <TrafficClosuresPanel/>
    <TrafficWaitingPanel @locate="locateResource" @task="openTask"/>
  </section>
  <ConsoleDialog v-if="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" wide @close="showTaskDialog = false">
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <p v-else class="empty-panel">{{ store.error || '正在读取任务详情…' }}</p>
  </ConsoleDialog>

  <ConsoleDialog v-if="showObstacleDialog && chosenVehicle" :title="chosenVehicle.vehicleId + ' · 障碍状态与处置'" @close="showObstacleDialog = false">
    <VehicleObstacleDetails :vehicle="chosenVehicle" :current="store.fresh" :now="store.now" />
  </ConsoleDialog>

  <ConsoleDialog v-if="selectedAlarm" title="告警详情" @close="selectedAlarm = null">
    <div class="detail-summary">
      <div><span>告警编号</span><b>{{ selectedAlarm.alarmId }}</b></div>
      <div><span>关联对象</span><b>{{ alarmSourceLabel(selectedAlarm.sourceType) }} · {{ selectedAlarm.sourceId }}</b></div>
      <div><span>级别 / 状态</span><b>{{ alarmSeverity(selectedAlarm.severity) }} / {{ alarmStatus(selectedAlarm.status) }}</b></div>
    </div>
    <h3 class="subheading">{{ alarmTitleText(selectedAlarm.title) }}</h3>
    <p class="long-message">{{ alarmMessageText(selectedAlarm.message) }}</p>
    <details v-if="alarmTitleText(selectedAlarm.title) !== selectedAlarm.title || alarmMessageText(selectedAlarm.message) !== selectedAlarm.message"><summary>查看厂家原始提示</summary><p class="long-message">{{ selectedAlarm.title }}</p><p class="long-message">{{ selectedAlarm.message }}</p></details>
    <p class="muted-note">首次发生：{{ shortTime(selectedAlarm.firstOccurredAt) }}<br>最近观察：{{ shortTime(selectedAlarm.lastObservedAt) }}</p>
    <div class="inline-warning">此处只查看，确认及恢复按现有WPF流程处理。</div>
  </ConsoleDialog>

  <ConsoleDialog v-if="selectedDevice" :title="selectedDevice.name + ' · PLC设备状态'" wide @close="selectedDevice = null">
    <p class="form-explanation">{{ selectedDevice.summary }}</p>
    <table class="dialog-table"><thead><tr><th>信号</th><th>值</th><th>质量</th><th>地址</th><th>更新时间</th></tr></thead>
    <tbody><tr v-for="field in selectedDevice.fields" :key="field.name"><td>{{ signalLabel(field.name, field.name) }}</td><td>{{ field.value }}</td><td>{{ qualityLabel(field.quality) }}</td><td>{{ field.address }}</td><td>{{ field.updatedAt }}</td></tr></tbody></table>
  </ConsoleDialog>
</div>
</template>

<style scoped>.overview-page{flex:1;min-height:0;overflow:auto;padding-bottom:14px;display:flex;flex-direction:column;gap:12px}.overview-page .control-grid{height:max(620px,calc(100dvh - 150px));flex:none;min-height:620px}.left-column{grid-template-rows:minmax(230px,.8fr) minmax(260px,1.2fr) minmax(230px,1fr)}.live-tasks,.shared-panel{overflow:auto}.live-tasks{min-height:0}.right-column{min-height:0}.distribution-panel>.panel-footnote{flex-shrink:0}.live-tasks>header,.live-tasks>.current-task-counts,.live-task{flex-shrink:0}.live-task>div{flex-wrap:wrap;gap:6px}.live-task b{overflow-wrap:anywhere;min-width:0;white-space:normal;flex:1 1 120px}.live-task small{white-space:normal;overflow-wrap:anywhere;display:block;-webkit-line-clamp:unset;overflow:visible}.shared-resource details{grid-column:1/-1;min-width:0;font-size:11px;overflow-wrap:anywhere}.shared-resource summary{cursor:pointer}.map-context-status{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--ui-text-3)}.resource-location{padding:0 16px;font-size:12px;line-height:1.8;color:var(--ui-text-3)}.overview-lower-deck{display:grid;gap:10px;flex:none;padding-top:4px}.overview-lower-deck>header{display:flex;align-items:end;justify-content:space-between;gap:16px;padding:14px 3px 6px;border-top:1px solid var(--ui-line)}.overview-lower-deck>header small{display:block;margin-bottom:4px;color:var(--ui-text-3);font-size:9px;letter-spacing:1.4px}.overview-lower-deck>header h2{font-size:15px;color:var(--ui-text)}.overview-lower-deck>header>span{color:var(--ui-text-3);font-size:10px}.overview-lower-deck :deep(.closures-panel),.overview-lower-deck :deep(.waiting-panel){margin-top:0}@media(max-width:900px){.overview-page .control-grid{height:auto;min-height:0}.left-column{grid-template-rows:auto auto auto}.overview-lower-deck>header{align-items:flex-start;flex-direction:column;gap:4px}}</style>
<style scoped>
.obstacle-link{align-self:flex-start;margin:0 14px 12px}
.right-column{grid-template-rows:minmax(250px,.85fr) minmax(210px,1.15fr) minmax(188px,.8fr);gap:12px}
.alert-preview{min-height:0;overflow:auto}
.alert-preview .mini-table-head{margin:13px 16px 0;padding-bottom:10px;line-height:1.5}
.alert-preview .alarm-preview-row{padding:14px 16px;line-height:1.65}
.alert-preview .alarm-preview-row small{margin-top:6px;line-height:1.45}
.alert-preview .panel-footnote{padding:10px 16px;line-height:1.7}
.shared-panel{max-height:none;min-height:0}
.shared-panel .shared-resource{padding:13px 16px;gap:7px 12px;line-height:1.55}
.shared-panel .shared-resource>span{min-width:0;overflow-wrap:anywhere}
.shared-panel .shared-resource small{padding-top:1px;line-height:1.55}
.shared-panel .shared-resource details{line-height:1.6}
.distribution-panel{position:relative;align-items:stretch}
.distribution-panel::after{content:"";position:absolute;left:46px;right:46px;bottom:5px;height:1px;background:linear-gradient(90deg,transparent,rgba(63,133,186,.32),transparent);pointer-events:none}
.chart-history{min-height:26px;padding:3px 9px;font-size:11px;color:#77d8ee;border-color:#285a7d;background:#102a43}
.ring-legend{grid-template-columns:repeat(3,minmax(0,1fr));gap:7px 6px;padding:10px 12px 0}
.ring-legend>span{min-width:0;justify-content:flex-start;padding:6px;border:1px solid rgba(66,91,132,.34);border-radius:6px;background:rgba(17,31,61,.64);font-size:10px;color:#a9bdd8}
.ring-legend>span i{width:7px;height:7px;border-radius:50%;box-shadow:0 0 8px currentColor}
.ring-legend>span em{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-style:normal}
.ring-legend>span b{margin-left:auto;color:#eef7ff;font:600 12px Consolas,monospace}
.map-toolbar{flex-shrink:0;align-items:center}.map-toolbar select{max-width:110px;padding:4px 6px;font-size:11px;min-height:27px}
@media(max-width:900px){.right-column{grid-template-rows:auto auto auto}.shared-panel{max-height:320px}}
</style>
