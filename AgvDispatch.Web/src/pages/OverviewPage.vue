<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { DispatchScene } from '../DispatchScene';
import { isRunningStage, label, mayPlot } from '../state';
import { vehicleCategory, categoryLabels, shortTime } from '../dashboard';
import { ElCard, ElRow, ElCol, ElButton, ElDescriptions, ElDescriptionsItem, ElTag, ElProgress, ElEmpty, ElDivider, ElSelect, ElOption, ElSpace, ElStatistic, ElIcon, ElTooltip, ElDialog, ElTable, ElTableColumn } from 'element-plus';
import type { Vehicle, Alarm } from '../types';
import { Van, FullScreen, ZoomIn, ZoomOut, Aim } from '@element-plus/icons-vue';
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
const selectedAlarm = ref<import('../types').Alarm | null>(null);
let scene: DispatchScene | undefined;
let detailEpoch = 0;

const chosenVehicle = computed(() => store.vehicles.find(v => v.vehicleId === selectedVehicle.value));
const selectedRoad = computed(() => store.snapshot?.map?.edges.find(edge => edge.id === selectedEdge.value));

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
function fullscreen() { void mapElement.value?.parentElement?.parentElement?.requestFullscreen().catch(() => {}); }

onMounted(() => {
  try { scene = new DispatchScene(mapElement.value!, id => { selectedVehicle.value = id; }); draw(); }
  catch (ex) { store.sceneError = `三维视图初始化失败：${String(ex)}`; }
});
onUnmounted(() => { scene?.dispose(); });

const alarmDialogVisible = computed({
  get: () => selectedAlarm.value !== null,
  set: (v: boolean) => { if (!v) selectedAlarm.value = null; }
});
</script>

<template>
  <div class="overview-grid">
    <!-- Left Column -->
    <div class="left-col">
      <ElCard shadow="never" class="dark-card">
        <template #header><div class="card-title"><span class="dot green"></span>车辆状态统计 <ElTag size="small" type="info" effect="plain">{{ store.vehicles.length }} 辆</ElTag></div></template>
        <div class="fleet-grid">
          <div v-for="stat in store.vehicleStats.filter(s => s.key !== 'unknown' || s.count > 0)" :key="stat.key" class="fleet-item" @click="router.push({ path: '/vehicles', query: { filter: stat.key } })">
            <span class="dot" :class="stat.key"></span>
            <ElStatistic :value="stat.count" :value-style="{ fontSize: '22px', fontWeight: 600, fontFamily: 'Consolas, monospace' }" />
            <small>{{ stat.label }}</small>
          </div>
        </div>
      </ElCard>

      <ElCard shadow="never" class="dark-card">
        <template #header>
          <div class="card-title"><ElIcon><Van /></ElIcon>车辆详情
            <ElButton text size="small" @click="router.push('/vehicles')">列表</ElButton>
          </div>
        </template>
        <template v-if="chosenVehicle">
          <div class="vehicle-head">
            <div><b class="text-cyan mono">{{ chosenVehicle.vehicleId }}</b><br><small>{{ chosenVehicle.displayName }}</small></div>
            <ElTag :type="(({ running:'success', waiting:'warning', fault:'danger', offline:'info', idle:'', charging:'primary', unknown:'info' } as Record<string,string>)[vehicleCategory(chosenVehicle, store.allTasks, store.fresh)] || 'info') as any" size="small" effect="dark">
              {{ categoryLabels[vehicleCategory(chosenVehicle, store.allTasks, store.fresh)] }}
            </ElTag>
          </div>
          <div class="battery-row"><span>电量</span><ElProgress :percentage="chosenVehicle.batteryPercent ?? 0" :stroke-width="8" :color="(chosenVehicle.batteryPercent ?? 0) > 20 ? '#67e8f9' : '#f56c6c'" /></div>
          <ElDescriptions :column="1" size="small" border>
            <ElDescriptionsItem label="当前站点">{{ chosenVehicle.currentStation ?? '未知' }}</ElDescriptionsItem>
            <ElDescriptionsItem label="导航状态">{{ chosenVehicle.navigationState ?? '未知' }}</ElDescriptionsItem>
            <ElDescriptionsItem label="原始坐标">{{ chosenVehicle.x?.toFixed(2) ?? '—' }} / {{ chosenVehicle.y?.toFixed(2) ?? '—' }}</ElDescriptionsItem>
            <ElDescriptionsItem label="定位质量">{{ label(chosenVehicle.quality) }} / {{ chosenVehicle.localizationConfidence ?? '—' }}</ElDescriptionsItem>
            <ElDescriptionsItem label="地图叠加">{{ store.snapshot && mayPlot(store.snapshot, chosenVehicle, store.now) ? '已核定且有效' : '待核对' }}</ElDescriptionsItem>
          </ElDescriptions>
        </template>
        <ElEmpty v-else description="连接后台后选择车辆" :image-size="50" />
        <div class="vehicle-picker">
          <ElButton v-for="v in store.vehicles" :key="v.vehicleId" :type="v.vehicleId === selectedVehicle ? 'primary' : 'default'" size="small" @click="selectedVehicle = v.vehicleId">{{ v.vehicleId }}</ElButton>
        </div>
      </ElCard>

      <ElCard shadow="never" class="dark-card alarm-card">
        <template #header><div class="card-title"><span class="dot amber"></span>异常告警 <ElButton text size="small" @click="router.push('/alarms')">更多 {{ store.openAlarms.length }}</ElButton></div></template>
        <div v-for="alarm in store.openAlarms.slice(0, 5)" :key="alarm.alarmId" class="alarm-row" @click="selectedAlarm = alarm">
          <ElTag :type="alarm.severity === 'Error' || alarm.severity === 'Critical' ? 'danger' : 'warning'" size="small" effect="dark">{{ ({ Info:'信息',Warning:'警告',Error:'错误',Critical:'严重' })[alarm.severity] ?? alarm.severity }}</ElTag>
          <span class="alarm-text">{{ alarm.title }}</span>
          <small>{{ shortTime(alarm.lastObservedAt) }}</small>
        </div>
        <ElEmpty v-if="!store.openAlarms.length" :description="store.fresh ? '暂无未确认告警' : '等待告警数据'" :image-size="40" />
      </ElCard>
    </div>

    <!-- Center: Map -->
    <ElCard shadow="never" class="dark-card map-card">
      <template #header>
        <div class="card-title map-title">
          <div><b>{{ store.snapshot?.map?.name ?? '当前运行地图' }}</b><br><small class="mono">{{ store.snapshot?.map?.id ?? '未连接' }} · {{ store.snapshot?.map?.version ?? '—' }}</small></div>
          <ElSpace>
            <ElButton :type="mapView === 'top' ? 'primary' : 'default'" size="small" @click="setMapView('top')">2D</ElButton>
            <ElButton :type="mapView === 'isometric' ? 'primary' : 'default'" size="small" @click="setMapView('isometric')">3D</ElButton>
            <ElTooltip content="全屏"><ElButton size="small" circle @click="fullscreen"><ElIcon><FullScreen /></ElIcon></ElButton></ElTooltip>
          </ElSpace>
        </div>
      </template>
      <div class="map-viewport">
        <div ref="mapElement" class="scene-container"></div>
        <div v-if="store.sceneError || !store.snapshot?.map" class="scene-empty">{{ store.sceneError || '尚未取得后台运行地图' }}</div>
        <div class="map-legend"><span><i class="dot running"></i>执行</span><span><i class="dot waiting"></i>等待</span><span><i class="dot fault"></i>故障</span><span><i class="dot offline"></i>离线</span></div>
        <div class="map-zoom">
          <ElButton size="small" @click="scene?.zoom(1.2)"><ElIcon><ZoomOut /></ElIcon></ElButton>
          <ElButton size="small" @click="scene?.view(mapView)"><ElIcon><Aim /></ElIcon></ElButton>
          <ElButton size="small" @click="scene?.zoom(0.8)"><ElIcon><ZoomIn /></ElIcon></ElButton>
        </div>
        <div class="map-info"><span>{{ store.snapshot?.map?.nodes.length ?? 0 }} 节点 / {{ store.snapshot?.map?.edges.length ?? 0 }} 道路</span><span>{{ store.mapVehicleCount }} 辆可定位</span></div>
      </div>
      <div class="map-tools">
        <ElSelect v-model="selectedEdge" placeholder="选择道路" size="small" clearable filterable style="width:260px">
          <ElOption v-for="edge in store.snapshot?.map?.edges ?? []" :key="edge.id" :label="`${edge.id} · ${edge.from} → ${edge.to}`" :value="edge.id" />
        </ElSelect>
        <span v-if="selectedRoad" class="mono">{{ selectedRoad.from }} {{ selectedRoad.bidirectional ? '↔' : '→' }} {{ selectedRoad.to }}</span>
        <span v-else>{{ store.snapshot?.map?.coordinateSystem ?? '待核定' }}</span>
      </div>
    </ElCard>

    <!-- Right Column -->
    <div class="right-col">
      <ElCard shadow="never" class="dark-card">
        <template #header><div class="card-title">任务状态分布</div></template>
        <div style="display:flex;flex-direction:column;align-items:center">
          <RingChart :segments="store.ringSegments" :total="store.allTasks.length" />
          <div class="ring-legend">
            <span v-for="s in store.ringSegments" :key="s.label"><i class="dot" :style="{ background: s.color }"></i>{{ s.label }} <b>{{ s.value }}</b></span>
          </div>
        </div>
      </ElCard>

      <ElCard shadow="never" class="dark-card">
        <template #header><div class="card-title">当前任务 <ElButton text size="small" @click="router.push('/tasks')">全部</ElButton></div></template>
        <div v-for="task in store.allTasks.slice(0, 6)" :key="task.taskId" class="task-row" @click="openTask(task.taskId)">
          <div class="task-head"><b class="mono text-cyan">{{ task.taskId }}</b>
            <ElTag :type="task.stage === 'Faulted' ? 'danger' : isRunningStage(task.stage) ? 'success' : 'warning'" size="small" effect="dark">{{ label(task.stage) }}</ElTag>
          </div>
          <div class="task-sub">{{ task.stationId }} · {{ task.vehicleId ?? '待分配' }}</div>
          <small>{{ task.errorMessage || '点击查看步骤' }}</small>
        </div>
        <ElEmpty v-if="!store.allTasks.length" description="暂无任务" :image-size="40" />
      </ElCard>

      <ElCard shadow="never" class="dark-card">
        <template #header><div class="card-title">共享区域</div></template>
        <div v-for="resource in store.resources" :key="resource.resourceId" class="resource-row">
          <span><i class="dot" :class="resource.occupiedCount ? 'amber' : 'green'"></i>{{ resource.resourceId }}</span>
          <b class="mono">{{ resource.occupiedCount }}/{{ resource.capacity }}</b>
          <small>{{ resource.leases.map(l => l.vehicleId).join('、') || (resource.occupiedCount ? '已占用' : '空闲') }}</small>
        </div>
        <ElEmpty v-if="!store.resources.length" description="暂无资源" :image-size="30" />
      </ElCard>
    </div>
  </div>

  <!-- Task Detail Dialog -->
  <ElDialog v-model="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" width="1000px" destroy-on-close>
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <ElEmpty v-else :description="store.error || '正在读取任务详情…'" />
  </ElDialog>

  <!-- Alarm Detail Dialog -->
  <ElDialog v-model="alarmDialogVisible" title="告警详情" width="600px">
    <template v-if="selectedAlarm">
      <ElDescriptions :column="2" border size="small">
        <ElDescriptionsItem label="告警编号">{{ selectedAlarm.alarmId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="关联对象">{{ selectedAlarm.sourceId }}</ElDescriptionsItem>
        <ElDescriptionsItem label="级别">{{ ({ Info:'信息',Warning:'警告',Error:'错误',Critical:'严重' } as Record<string,string>)[selectedAlarm.severity] ?? selectedAlarm.severity }}</ElDescriptionsItem>
        <ElDescriptionsItem label="状态">{{ ({ Open:'未确认',Acknowledged:'已确认',Recovered:'已恢复' } as Record<string,string>)[selectedAlarm.status] ?? selectedAlarm.status }}</ElDescriptionsItem>
        <ElDescriptionsItem label="代码" :span="2">{{ selectedAlarm.code }}</ElDescriptionsItem>
      </ElDescriptions>
      <h4 style="margin:16px 0 8px">{{ selectedAlarm.title }}</h4>
      <p style="line-height:1.9">{{ selectedAlarm.message }}</p>
      <p style="color:#8198b8;font-size:11px;margin-top:12px">首次：{{ shortTime(selectedAlarm.firstOccurredAt) }} · 最近：{{ shortTime(selectedAlarm.lastObservedAt) }}</p>
    </template>
  </ElDialog>
</template>

<style scoped>
.overview-grid{display:grid;grid-template-columns:280px minmax(0,1fr) 290px;gap:12px;min-height:calc(100dvh - 180px)}
.left-col,.right-col{display:flex;flex-direction:column;gap:12px;min-height:0;overflow:auto}
.dark-card{background:#0f1b30;border-color:#1e3352;color:#c8daf0}
.dark-card :deep(.el-card__header){padding:12px 16px;border-color:#1e3352;background:#0a1525}
.dark-card :deep(.el-card__body){padding:14px 16px}
.dark-card :deep(.el-descriptions){--el-descriptions-table-border:1px solid #1e3352}
.dark-card :deep(.el-descriptions__label){background:#0a1525;color:#8da3c6}
.dark-card :deep(.el-descriptions__content){background:#0f1b30;color:#c8daf0}
.dark-card :deep(.el-empty__description p){color:#6b82a5}
.card-title{display:flex;align-items:center;gap:8px;font-size:13px;font-weight:600;color:#d0e2f4}
.card-title .el-button{margin-left:auto}
.dot{display:inline-block;width:7px;height:7px;border-radius:50%;flex-shrink:0}
.dot.green,.dot.running{background:#25d3a4}.dot.amber,.dot.waiting{background:#e9b34f}
.dot.red,.dot.fault{background:#ee668b}.dot.idle{background:#b48ae8}
.dot.charging{background:#4db2ed}.dot.offline,.dot.unknown{background:#677b9b}
.fleet-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:8px}
.fleet-item{cursor:pointer;padding:10px 8px;background:#0a1525;border:1px solid #1e3352;border-radius:6px;text-align:center;transition:border-color .2s}
.fleet-item:hover{border-color:#67e8f9}
.fleet-item small{color:#8da3c6;font-size:10px}
.text-cyan{color:#67e8f9!important}.mono{font-family:Consolas,monospace}
.vehicle-head{display:flex;justify-content:space-between;align-items:center;margin-bottom:14px}
.battery-row{display:flex;align-items:center;gap:10px;margin-bottom:14px;font-size:11px;color:#8da6ca}
.battery-row .el-progress{flex:1}
.vehicle-picker{display:flex;flex-wrap:wrap;gap:6px;margin-top:12px}
.alarm-card :deep(.el-card__body){padding:0}
.alarm-row{display:flex;align-items:center;gap:10px;padding:12px 16px;border-bottom:1px solid #1e3352;cursor:pointer;transition:background .15s}
.alarm-row:hover{background:#142035}
.alarm-text{flex:1;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.alarm-row small{color:#6b82a5;font-size:10px;white-space:nowrap}
.map-card{grid-row:1/-1;display:flex;flex-direction:column;min-height:0}
.map-card :deep(.el-card__body){flex:1;display:flex;flex-direction:column;padding:0;min-height:0}
.map-title{width:100%}
.map-title>div{min-width:0}
.map-title>div b{font-size:14px}.map-title>div small{color:#6b82a5}
.map-viewport{position:relative;flex:1;min-height:350px;background:#0b1229;overflow:hidden}
.scene-container{position:absolute;inset:0}.scene-container canvas{display:block;width:100%;height:100%;touch-action:none}
.scene-empty{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:#6b82a5;text-align:center;padding:30px;pointer-events:none}
.map-legend{position:absolute;right:14px;top:12px;display:flex;gap:12px;color:#7990b2;font-size:10px;pointer-events:none}
.map-legend>span{display:flex;align-items:center;gap:5px}
.map-zoom{position:absolute;right:12px;bottom:40px;display:flex;flex-direction:column;gap:4px}
.map-info{position:absolute;bottom:10px;left:14px;right:14px;display:flex;justify-content:space-between;font-size:10px;color:#5d7a9e;pointer-events:none}
.map-tools{display:flex;align-items:center;gap:12px;padding:8px 14px;border-top:1px solid #1e3352;font-size:11px;color:#7995b8}
.ring-legend{display:flex;gap:12px;margin-top:8px}.ring-legend>span{display:flex;align-items:center;gap:4px;font-size:10px;color:#8da2c3}
.ring-legend b{font:12px Consolas,monospace;color:#d5e7f9}
.task-row{padding:12px 0;border-bottom:1px solid #1a2d4a;cursor:pointer;transition:background .15s}
.task-row:hover{background:#0a1525}
.task-head{display:flex;justify-content:space-between;align-items:center}
.task-sub{font-size:11px;color:#b3c5e1;margin-top:6px}
.task-row small{display:block;margin-top:4px;font-size:10px;color:#7c96b7;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.resource-row{display:grid;grid-template-columns:1fr auto;gap:4px;padding:10px 0;border-bottom:1px solid #1a2d4a;font-size:11px}
.resource-row>span{display:flex;align-items:center;gap:6px;color:#adbfdb}
.resource-row>b{color:#d2e4fa}
.resource-row small{grid-column:1/-1;color:#7390b1;font-size:10px}
@media(max-width:1100px){.overview-grid{grid-template-columns:1fr 1fr}.map-card{grid-column:1/-1;grid-row:auto;height:500px}.left-col,.right-col{overflow:visible}}
@media(max-width:700px){.overview-grid{grid-template-columns:1fr}.map-card{height:400px}}
</style>
