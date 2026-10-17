<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { DispatchScene } from '../DispatchScene';
import { isRunningStage, label, mayPlot } from '../state';
import { vehicleCategory, categoryLabels, shortTime } from '../dashboard';
import { ElButton, ElTag, ElProgress, ElEmpty, ElSelect, ElOption, ElIcon, ElTooltip, ElDialog, ElDescriptions, ElDescriptionsItem, ElCollapse, ElCollapseItem, ElBadge, ElScrollbar } from 'element-plus';
import type { Vehicle, Alarm } from '../types';
import { Van, FullScreen, ZoomIn, ZoomOut, Aim, ArrowLeft, ArrowRight } from '@element-plus/icons-vue';
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
const leftCollapsed = ref(false);
const rightCollapsed = ref(false);
let scene: DispatchScene | undefined;
let detailEpoch = 0;

const chosenVehicle = computed(() => store.vehicles.find(v => v.vehicleId === selectedVehicle.value));
const selectedRoad = computed(() => store.snapshot?.map?.edges.find(edge => edge.id === selectedEdge.value));
const alarmDialogVisible = computed({ get: () => selectedAlarm.value !== null, set: (v: boolean) => { if (!v) selectedAlarm.value = null; } });

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
function fullscreen() { void document.querySelector('.map-main')?.requestFullscreen().catch(() => {}); }

onMounted(() => {
  try { scene = new DispatchScene(mapElement.value!, id => { selectedVehicle.value = id; }); draw(); }
  catch (ex) { store.sceneError = `三维视图初始化失败：${String(ex)}`; }
});
onUnmounted(() => { scene?.dispose(); });
</script>

<template>
  <div class="dashboard">
    <!-- Top Stats Bar -->
    <div class="stats-bar">
      <div v-for="stat in store.vehicleStats.filter(s => s.key !== 'unknown' || s.count > 0)" :key="stat.key" class="stat-chip" @click="router.push({ path: '/vehicles', query: { filter: stat.key } })">
        <i class="dot" :class="stat.key"></i>
        <b>{{ stat.count }}</b>
        <span>{{ stat.label }}</span>
      </div>
      <div class="stat-divider"></div>
      <div class="stat-chip"><i class="dot green"></i><b>{{ store.ringSegments[0].value }}</b><span>执行任务</span></div>
      <div class="stat-chip"><i class="dot amber"></i><b>{{ store.ringSegments[1].value }}</b><span>等待任务</span></div>
      <div class="stat-chip" @click="router.push('/alarms')"><i class="dot" :class="store.openAlarms.length ? 'red' : 'green'"></i><b>{{ store.openAlarms.length }}</b><span>未确认告警</span></div>
      <div class="stat-chip"><b>{{ store.allTasks.length }}</b><span>已加载任务</span></div>
      <div class="stat-divider"></div>
      <RingChart :segments="store.ringSegments" :total="store.allTasks.length" style="width:52px;height:52px" />
    </div>

    <!-- Main Area: Left Panel + Map + Right Panel -->
    <div class="main-area">
      <!-- Left Panel: Vehicle -->
      <aside class="side-panel left-panel" :class="{ collapsed: leftCollapsed }">
        <button class="panel-toggle" @click="leftCollapsed = !leftCollapsed"><ElIcon><ArrowLeft v-if="!leftCollapsed" /><ArrowRight v-else /></ElIcon></button>
        <div v-show="!leftCollapsed" class="panel-scroll">
          <div class="panel-section">
            <div class="panel-title"><ElIcon :size="16"><Van /></ElIcon> 车辆详情 <ElButton text size="small" @click="router.push('/vehicles')">全部</ElButton></div>
            <div class="vehicle-picker">
              <ElButton v-for="v in store.vehicles" :key="v.vehicleId" :type="v.vehicleId === selectedVehicle ? 'primary' : 'default'" size="small" @click="selectedVehicle = v.vehicleId">
                <i class="dot" :class="vehicleCategory(v, store.allTasks, store.fresh)"></i>{{ v.vehicleId }}
              </ElButton>
            </div>
            <template v-if="chosenVehicle">
              <div class="vehicle-head">
                <div><b class="text-cyan mono">{{ chosenVehicle.vehicleId }}</b><small>{{ chosenVehicle.displayName }}</small></div>
                <ElTag :type="({ running:'success', waiting:'warning', fault:'danger', offline:'info', idle:'primary', charging:'primary', unknown:'info' } as Record<string,any>)[vehicleCategory(chosenVehicle, store.allTasks, store.fresh)] ?? 'info'" size="small" effect="dark">
                  {{ categoryLabels[vehicleCategory(chosenVehicle, store.allTasks, store.fresh)] }}
                </ElTag>
              </div>
              <div class="battery-row"><span>电量</span><ElProgress :percentage="chosenVehicle.batteryPercent ?? 0" :stroke-width="6" :color="(chosenVehicle.batteryPercent ?? 0) > 20 ? '#67e8f9' : '#f56c6c'" style="flex:1" /><b class="mono">{{ chosenVehicle.batteryPercent ?? 0 }}%</b></div>
              <ElDescriptions :column="1" size="small" border class="dark-desc">
                <ElDescriptionsItem label="站点">{{ chosenVehicle.currentStation ?? '未知' }}</ElDescriptionsItem>
                <ElDescriptionsItem label="导航">{{ chosenVehicle.navigationState ?? '未知' }}</ElDescriptionsItem>
                <ElDescriptionsItem label="坐标">{{ chosenVehicle.x?.toFixed(2) ?? '—' }}, {{ chosenVehicle.y?.toFixed(2) ?? '—' }}</ElDescriptionsItem>
                <ElDescriptionsItem label="定位">{{ label(chosenVehicle.quality) }} / {{ chosenVehicle.localizationConfidence ?? '—' }}</ElDescriptionsItem>
                <ElDescriptionsItem label="叠加">{{ store.snapshot && mayPlot(store.snapshot, chosenVehicle, store.now) ? '有效' : '待核对' }}</ElDescriptionsItem>
              </ElDescriptions>
            </template>
            <ElEmpty v-else description="选择车辆" :image-size="36" />
          </div>

          <div class="panel-section">
            <div class="panel-title"><i class="dot amber"></i> 告警
              <ElBadge :value="store.openAlarms.length" :hidden="!store.openAlarms.length" :max="99">
                <ElButton text size="small" @click="router.push('/alarms')">查看</ElButton>
              </ElBadge>
            </div>
            <div v-for="alarm in store.openAlarms.slice(0, 4)" :key="alarm.alarmId" class="alarm-item" @click="selectedAlarm = alarm">
              <ElTag :type="alarm.severity === 'Error' || alarm.severity === 'Critical' ? 'danger' : 'warning'" size="small" effect="dark">{{ ({ Info:'信息',Warning:'警告',Error:'错误',Critical:'严重' } as Record<string,string>)[alarm.severity] ?? alarm.severity }}</ElTag>
              <span>{{ alarm.title }}</span>
            </div>
            <div v-if="!store.openAlarms.length" class="no-data">暂无告警</div>
          </div>
        </div>
      </aside>

      <!-- Center: Map (takes all available space) -->
      <div class="map-main">
        <div class="map-header">
          <div class="map-title">
            <b>{{ store.snapshot?.map?.name ?? '当前运行地图' }}</b>
            <small class="mono">{{ store.snapshot?.map?.id ?? '未连接' }} · {{ store.snapshot?.map?.version ?? '—' }} · {{ store.snapshot?.map?.nodes.length ?? 0 }} 节点 / {{ store.snapshot?.map?.edges.length ?? 0 }} 道路 · {{ store.mapVehicleCount }} 辆可定位</small>
          </div>
          <div class="map-controls">
            <ElSelect v-model="selectedEdge" placeholder="选择道路" size="small" clearable filterable style="width:220px">
              <ElOption v-for="edge in store.snapshot?.map?.edges ?? []" :key="edge.id" :label="`${edge.id} · ${edge.from}→${edge.to}`" :value="edge.id" />
            </ElSelect>
            <ElButton :type="mapView === 'top' ? 'primary' : 'default'" size="small" @click="setMapView('top')">2D</ElButton>
            <ElButton :type="mapView === 'isometric' ? 'primary' : 'default'" size="small" @click="setMapView('isometric')">3D</ElButton>
            <ElTooltip content="全屏"><ElButton size="small" circle @click="fullscreen"><ElIcon><FullScreen /></ElIcon></ElButton></ElTooltip>
          </div>
        </div>
        <div class="map-viewport">
          <div ref="mapElement" class="scene-container"></div>
          <div v-if="store.sceneError || !store.snapshot?.map" class="scene-empty">{{ store.sceneError || '尚未取得后台运行地图。请确认C#主服务已启动且已激活运行地图。' }}</div>
          <div class="map-legend">
            <span><i class="dot running"></i>执行</span><span><i class="dot waiting"></i>等待</span><span><i class="dot fault"></i>故障</span><span><i class="dot offline"></i>离线</span>
          </div>
          <div class="map-zoom">
            <ElButton size="small" @click="scene?.zoom(1.2)"><ElIcon><ZoomOut /></ElIcon></ElButton>
            <ElButton size="small" @click="scene?.view(mapView)"><ElIcon><Aim /></ElIcon></ElButton>
            <ElButton size="small" @click="scene?.zoom(0.8)"><ElIcon><ZoomIn /></ElIcon></ElButton>
          </div>
          <div class="map-bottom-info">
            <span v-if="selectedRoad" class="mono">{{ selectedRoad.from }} {{ selectedRoad.bidirectional ? '↔' : '→' }} {{ selectedRoad.to }}</span>
            <span v-else>{{ store.snapshot?.map?.coordinateSystem ?? '待核定' }} · {{ mapView === 'top' ? '右键平移 · 滚轮缩放' : '左键旋转 · 右键平移' }}</span>
          </div>
          <div v-for="warning in store.snapshot?.map?.warnings ?? []" :key="warning" class="map-warning"><ElTag type="warning" size="small" effect="dark">{{ warning }}</ElTag></div>
        </div>
      </div>

      <!-- Right Panel: Tasks & Resources -->
      <aside class="side-panel right-panel" :class="{ collapsed: rightCollapsed }">
        <button class="panel-toggle right" @click="rightCollapsed = !rightCollapsed"><ElIcon><ArrowRight v-if="!rightCollapsed" /><ArrowLeft v-else /></ElIcon></button>
        <div v-show="!rightCollapsed" class="panel-scroll">
          <div class="panel-section">
            <div class="panel-title">当前任务 <ElButton text size="small" @click="router.push('/tasks')">全部 {{ store.allTasks.length }}</ElButton></div>
            <div v-for="task in store.allTasks.slice(0, 8)" :key="task.taskId" class="task-item" @click="openTask(task.taskId)">
              <div class="task-head">
                <b class="mono text-cyan">{{ task.taskId }}</b>
                <ElTag :type="task.stage === 'Faulted' ? 'danger' : isRunningStage(task.stage) ? 'success' : 'warning'" size="small" effect="dark">{{ label(task.stage) }}</ElTag>
              </div>
              <div class="task-sub">{{ task.stationId }} · {{ task.vehicleId ?? '待分配' }}</div>
              <small>{{ task.errorMessage || '点击查看步骤' }}</small>
            </div>
            <div v-if="!store.allTasks.length" class="no-data">暂无任务</div>
          </div>

          <div class="panel-section">
            <div class="panel-title">共享区域</div>
            <div v-for="resource in store.resources" :key="resource.resourceId" class="resource-item">
              <span><i class="dot" :class="resource.occupiedCount ? 'amber' : 'green'"></i>{{ resource.resourceId }}</span>
              <b class="mono">{{ resource.occupiedCount }}/{{ resource.capacity }}</b>
              <small>{{ resource.leases.map(l => l.vehicleId).join('、') || '空闲' }}</small>
            </div>
            <div v-if="!store.resources.length" class="no-data">暂无资源</div>
          </div>
        </div>
      </aside>
    </div>
  </div>

  <!-- Dialogs -->
  <ElDialog v-model="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" width="1000px" destroy-on-close>
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <ElEmpty v-else :description="store.error || '正在读取…'" />
  </ElDialog>

  <ElDialog v-model="alarmDialogVisible" title="告警详情" width="600px">
    <template v-if="selectedAlarm">
      <ElDescriptions :column="2" border size="small">
        <ElDescriptionsItem label="编号">{{ selectedAlarm.alarmId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="对象">{{ selectedAlarm.sourceId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="级别">{{ ({ Info:'信息',Warning:'警告',Error:'错误',Critical:'严重' } as Record<string,string>)[selectedAlarm.severity] ?? selectedAlarm.severity }}</ElDescriptionsItem>
        <ElDescriptionsItem label="状态">{{ ({ Open:'未确认',Acknowledged:'已确认',Recovered:'已恢复' } as Record<string,string>)[selectedAlarm.status] ?? selectedAlarm.status }}</ElDescriptionsItem>
      </ElDescriptions>
      <h4 style="margin:14px 0 6px;color:#cee2f5">{{ selectedAlarm.title }}</h4>
      <p style="line-height:1.9;color:#afc6e3">{{ selectedAlarm.message }}</p>
    </template>
  </ElDialog>
</template>

<style scoped>
.dashboard{display:flex;flex-direction:column;gap:10px;height:calc(100dvh - 160px);min-height:500px}

/* Stats Bar */
.stats-bar{display:flex;align-items:center;gap:10px;padding:8px 14px;background:#0d1b2a;border:1px solid #1e3352;border-radius:6px;overflow-x:auto;flex-shrink:0}
.stat-chip{display:flex;align-items:center;gap:6px;cursor:pointer;padding:4px 10px;border-radius:4px;transition:background .15s;white-space:nowrap}
.stat-chip:hover{background:#142035}
.stat-chip b{font:600 18px/1 Consolas,monospace;color:#e0edf8}
.stat-chip span{font-size:10px;color:#8da3c6}
.stat-divider{width:1px;height:30px;background:#1e3a5f;flex-shrink:0}

/* Main Area */
.main-area{display:flex;gap:0;flex:1;min-height:0;border-radius:6px;overflow:hidden;border:1px solid #1e3352}

/* Side Panels */
.side-panel{width:260px;flex-shrink:0;background:#0a1525;border-right:1px solid #1e3352;position:relative;transition:width .25s ease;display:flex;flex-direction:column}
.side-panel.right-panel{border-right:none;border-left:1px solid #1e3352}
.side-panel.collapsed{width:32px}
.panel-toggle{position:absolute;top:50%;z-index:5;width:24px;height:48px;background:#142035;border:1px solid #1e3a5f;border-radius:0 6px 6px 0;color:#8da3c6;cursor:pointer;display:flex;align-items:center;justify-content:center;padding:0;transform:translateY(-50%);right:-1px;min-height:auto}
.panel-toggle.right{left:-1px;right:auto;border-radius:6px 0 0 6px}
.panel-scroll{overflow-y:auto;flex:1;padding:12px}

.panel-section{margin-bottom:16px}
.panel-title{display:flex;align-items:center;gap:6px;font-size:12px;font-weight:600;color:#c8daf0;margin-bottom:10px;padding-bottom:8px;border-bottom:1px solid #1e3352}
.panel-title .el-button{margin-left:auto}

.vehicle-picker{display:flex;flex-wrap:wrap;gap:4px;margin-bottom:10px}
.vehicle-picker .el-button{font-size:10px;padding:4px 8px}
.vehicle-head{display:flex;justify-content:space-between;align-items:center;margin-bottom:10px}
.vehicle-head div{display:flex;flex-direction:column;gap:2px}
.vehicle-head small{color:#7891b5;font-size:10px}
.battery-row{display:flex;align-items:center;gap:8px;margin-bottom:10px;font-size:10px;color:#8da6ca}
.battery-row b{font-size:11px;color:#67e8f9}

.dark-desc :deep(.el-descriptions__label){background:#0a1525!important;color:#8da3c6!important;padding:6px 10px!important;font-size:11px!important}
.dark-desc :deep(.el-descriptions__content){background:#0f1b30!important;color:#c8daf0!important;padding:6px 10px!important;font-size:11px!important}
.dark-desc :deep(.el-descriptions){--el-descriptions-table-border:1px solid #1e3352}

.alarm-item{display:flex;align-items:center;gap:8px;padding:8px 0;border-bottom:1px solid #162540;cursor:pointer;font-size:11px;color:#b3c5e1}
.alarm-item:hover{color:#67e8f9}
.alarm-item span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1}

.task-item{padding:10px 0;border-bottom:1px solid #162540;cursor:pointer;transition:background .15s}
.task-item:hover{background:#0f1b30}
.task-head{display:flex;justify-content:space-between;align-items:center}
.task-sub{font-size:11px;color:#b3c5e1;margin-top:4px}
.task-item small{display:block;margin-top:3px;font-size:10px;color:#7c96b7;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}

.resource-item{display:grid;grid-template-columns:1fr auto;gap:3px;padding:8px 0;border-bottom:1px solid #162540;font-size:11px}
.resource-item>span{display:flex;align-items:center;gap:5px;color:#adbfdb}
.resource-item>b{color:#d2e4fa;font-size:12px}
.resource-item small{grid-column:1/-1;color:#7390b1;font-size:10px}

.no-data{color:#6b82a5;font-size:11px;text-align:center;padding:16px 0}

/* Map (center, fills all remaining space) */
.map-main{flex:1;display:flex;flex-direction:column;min-width:0;background:#0b1229}
.map-header{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 14px;background:#0d1b2a;border-bottom:1px solid #1e3352;flex-shrink:0;flex-wrap:wrap}
.map-title{min-width:0}
.map-title b{font-size:14px;color:#d0e2f4;display:block}
.map-title small{color:#6b82a5;font-size:10px;display:block;margin-top:2px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.map-controls{display:flex;align-items:center;gap:6px;flex-shrink:0}
.map-viewport{position:relative;flex:1;min-height:300px;overflow:hidden}
.scene-container{position:absolute;inset:0}.scene-container canvas{display:block;width:100%;height:100%;touch-action:none}
.scene-empty{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:#6b82a5;text-align:center;padding:30px;pointer-events:none;font-size:13px}
.map-legend{position:absolute;right:14px;top:12px;display:flex;gap:12px;color:#7990b2;font-size:10px;pointer-events:none}
.map-legend>span{display:flex;align-items:center;gap:5px}
.map-zoom{position:absolute;right:12px;bottom:50px;display:flex;flex-direction:column;gap:4px}
.map-bottom-info{position:absolute;bottom:10px;left:14px;right:14px;display:flex;justify-content:space-between;font-size:10px;color:#5d7a9e;pointer-events:none}
.map-warning{position:absolute;top:12px;left:14px}

/* Common */
.dot{display:inline-block;width:7px;height:7px;border-radius:50%;flex-shrink:0}
.dot.green,.dot.running{background:#25d3a4}.dot.amber,.dot.waiting{background:#e9b34f}
.dot.red,.dot.fault{background:#ee668b}.dot.idle{background:#b48ae8}
.dot.charging{background:#4db2ed}.dot.offline,.dot.unknown{background:#677b9b}
.text-cyan{color:#67e8f9!important}.mono{font-family:Consolas,monospace}

/* Responsive */
@media(max-width:1200px){.side-panel{width:220px}}
@media(max-width:900px){.side-panel{display:none}.stats-bar{flex-wrap:wrap}}
</style>
