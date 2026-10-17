<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { shortTime } from '../dashboard';
import { isMapSwitchPhase } from '../map-lifecycle';
import type { MapSummary } from '../types';
import { IconMap, IconArrowLeft, IconRefresh, IconEye, IconPlayerPlay, IconEdit, IconAlertTriangle, IconCircleCheck, IconX } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';

const store = useDispatchStore();
const toast = useToastStore();
const router = useRouter();
const catalog = ref<MapSummary[]>([]);
const catalogError = ref('');
const selectedMap = ref<MapSummary | null>(null);
const busy = ref(false);

interface MapSwitch { mode: string; current: { releaseId: string; mapId: string; versionId: string; previousMapId: string | null; phase: number | string; phaseText: string; evidence: string; startedAt: string; updatedAt: string } | null; blockers: string[]; history: any[] }
const switchStatus = ref<MapSwitch | null>(null);
const switchOpen = ref(false);
const switchEvidence = ref('');
const switchTargetMapId=ref('');
const switchSafetyConfirmed = ref(false);
const switchVehicleConfirmed = ref(false);
const switchWorkflowConfirmed = ref(false);

const activateOpen = ref(false);
const activateMap = ref<MapSummary | null>(null);
const activateConfirmation = ref('');
const activateEvidence=ref('');
const activateSafetyConfirmed = ref(false);
const activateVehicleConfirmed = ref(false);

const versionsOpen = ref(false);
const versionsMapId = ref('');
const versions = ref<{ versionId: string; versionNumber: number; status: string; nodeCount: number; edgeCount: number; updatedAt: string }[]>([]);

async function loadCatalog() {
  catalogError.value = '';
  try { catalog.value = await store.api<MapSummary[]>('/api/maps'); }
  catch (ex) { catalogError.value = String(ex); }
}
void loadCatalog();

async function loadVersions(mapId: string) {
  versionsMapId.value = mapId; versions.value = []; versionsOpen.value = true;
  try { versions.value = await store.api<any[]>(`/api/maps/${encodeURIComponent(mapId)}/versions`); }
  catch (ex) { toast.error(`版本历史加载失败：${String(ex)}`); }
}

function openEditDraft(mapId: string) {
  router.push({ name: 'planning', query: { tab: 'map', mapId } });
}

function openActivate(map: MapSummary) {
  activateMap.value = map; activateOpen.value = true;
  activateConfirmation.value = ''; activateEvidence.value=''; activateSafetyConfirmed.value = false; activateVehicleConfirmed.value = false;
}

async function doActivate() {
  if (!activateMap.value || !activateConfirmation.value.trim()) return;
  busy.value = true;
  try {
    const pending=await store.api<MapSwitch>('/api/maps/switch');
    if(pending.current&&(pending.current.mapId!==activateMap.value.mapId||pending.current.versionId!==activateMap.value.publishedVersionId||!isMapSwitchPhase(pending.current.phase,10))){toast.warning('当前已有其他切换或已激活待放行，请先在安全切换中处理原批次。');return;}
    if(!pending.current){const begun=await store.api<{accepted:boolean;message:string}>('/api/maps/switch/begin',{mapId:activateMap.value.mapId,versionId:activateMap.value.publishedVersionId});if(!begun.accepted){toast.warning(begun.message);return;}}
    const result = await store.api<{ accepted: boolean; message: string }>('/api/maps/activate', {
      mapId: activateMap.value.mapId,
      versionId: activateMap.value.publishedVersionId,
      confirmationText: activateConfirmation.value.trim(),
      siteSafetyConfirmed: activateSafetyConfirmed.value,
      vehicleMapCompatibilityConfirmed: activateVehicleConfirmed.value,
      evidence: activateEvidence.value.trim()
    });
    if (result.accepted) { toast.success('地图已激活待放行，请核对安全切换状态后确认放行。'); activateOpen.value = false; void loadCatalog(); await loadSwitchStatus(); }
    else toast.warning(result.message || '激活未被接受');
  } catch (ex) { toast.error(`激活失败：${String(ex)}`); }
  finally { busy.value = false; }
}

async function loadSwitchStatus() {
  switchOpen.value = true; switchEvidence.value = ''; switchSafetyConfirmed.value = false;
  switchVehicleConfirmed.value = false; switchWorkflowConfirmed.value = false;
  try { switchStatus.value = await store.api<MapSwitch>('/api/maps/switch'); }
  catch (ex) { toast.error(`安全切换状态加载失败：${String(ex)}`); }
}

async function switchAction(action: 'begin' | 'drain' | 'resume' | 'cancel') {
  if (!switchEvidence.value.trim()) { toast.warning('请填写操作依据'); return; }
  busy.value = true;
  try {
    let result: { accepted: boolean; message: string };
    if (action === 'begin') {
      const target = catalog.value.find(m => m.mapId===switchTargetMapId.value && m.publishedVersionId);
      if (!target) { toast.warning('没有可切换的已发布地图'); return; }
      result = await store.api<any>('/api/maps/switch/begin', { mapId: target.mapId, versionId: target.publishedVersionId });
    } else {
      const current = switchStatus.value?.current;
      if (!current) { toast.warning('没有进行中的切换'); return; }
      const body = { releaseId: current.releaseId, siteSafetyConfirmed: switchSafetyConfirmed.value, vehicleMapCompatibilityConfirmed: switchVehicleConfirmed.value, evidence: switchEvidence.value, originalWorkflowConfirmed: switchWorkflowConfirmed.value };
      result = await store.api<any>(`/api/maps/switch/${action}`, body);
    }
    if (result.accepted) { toast.success(result.message); void loadSwitchStatus(); }
    else toast.warning(result.message);
  } catch (ex) { toast.error(`安全切换操作失败：${String(ex)}`); }
  finally { busy.value = false; }
}

const currentPhase = computed(() => switchStatus.value?.current?.phaseText ?? '无进行中');
</script>

<template>
  <section class="management-page">
    <header class="management-title">
      <h2><IconMap :size="23" />地图管理</h2>
      <span>共 {{ catalog.length }} 张</span>
      <button class="accent-button" @click="router.push({ name: 'planning', query: { tab: 'map' } })"><IconEdit :size="14" />地图编辑</button>
      <button @click="router.push({ name: 'planning', query: { tab: 'plans' } })">方案管理</button>
      <button @click="loadSwitchStatus"><IconAlertTriangle :size="14" />安全切换</button>
      <button @click="loadCatalog"><IconRefresh :size="15" />刷新</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <div class="map-catalog-note">复用厂家建好的地图。此页管理地图版本与激活状态。正式运行切换需使用安全切换流程。</div>
    <p v-if="catalogError" class="inline-warning">{{ catalogError }}</p>
    <div class="map-catalog">
      <article v-for="map in catalog" :key="map.mapId" class="map-card">
        <div class="map-card-art"><IconMap :size="64" stroke="1" /><span class="outline-badge" :class="{ cyan: map.isActive }">{{ map.isActive ? '当前运行' : '未运行' }}</span><b>{{ map.mapId }}</b><small>{{ map.unit }} / {{ map.coordinateSystem }}</small></div>
        <h3>{{ map.name }}</h3>
        <p>{{ map.description || '未填写说明' }}</p>
        <dl><dt>运行版本</dt><dd>{{ map.activeVersionId ?? '—' }}</dd><dt>发布版本</dt><dd>{{ map.publishedVersionId ?? '—' }}</dd></dl>
        <footer>
          <button @click="selectedMap = map"><IconEye :size="14" />详情</button>
          <button @click="loadVersions(map.mapId)">版本历史</button>
          <button @click="openEditDraft(map.mapId)"><IconEdit :size="14" />编辑草稿</button>
          <button v-if="map.publishedVersionId && (!map.isActive || map.publishedVersionId !== map.activeVersionId)" class="accent-button" :disabled="busy" @click="openActivate(map)"><IconPlayerPlay :size="14" />激活</button>
          <button v-if="map.isActive && map.mapId === store.snapshot?.map?.id" @click="router.push('/')">查看运行图</button>
        </footer>
      </article>
      <p v-if="!catalog.length && !catalogError" class="empty-panel">没有可查看的地图。连接后台后刷新，或在工程编辑中新建。</p>
    </div>
  </section>

  <!-- Map Detail -->
  <ConsoleDialog v-if="selectedMap" title="地图版本信息" @close="selectedMap = null">
    <dl class="property-list map-properties">
      <dt>地图编号</dt><dd>{{ selectedMap.mapId }}</dd>
      <dt>地图名称</dt><dd>{{ selectedMap.name }}</dd>
      <dt>坐标系</dt><dd>{{ selectedMap.coordinateSystem }}</dd>
      <dt>单位</dt><dd>{{ selectedMap.unit }}</dd>
      <dt>运行版本</dt><dd>{{ selectedMap.activeVersionId ?? '—' }}</dd>
      <dt>发布版本</dt><dd>{{ selectedMap.publishedVersionId ?? '—' }}</dd>
      <dt>更新时间</dt><dd>{{ shortTime(selectedMap.updatedAt) }}</dd>
    </dl>
  </ConsoleDialog>

  <!-- Version History -->
  <ConsoleDialog v-if="versionsOpen" :title="`版本历史 · ${versionsMapId}`" wide @close="versionsOpen = false">
    <div class="management-table" style="max-height:400px">
      <table>
        <thead><tr><th>版本号</th><th>状态</th><th>航点数</th><th>道路数</th><th>更新时间</th></tr></thead>
        <tbody>
          <tr v-for="v in versions" :key="v.versionId">
            <td class="mono cyan">V{{ v.versionNumber }}</td>
            <td>{{ v.status }}</td>
            <td>{{ v.nodeCount }}</td>
            <td>{{ v.edgeCount }}</td>
            <td>{{ shortTime(v.updatedAt) }}</td>
          </tr>
        </tbody>
      </table>
      <p v-if="!versions.length" class="empty-panel">没有版本记录</p>
    </div>
  </ConsoleDialog>

  <!-- Activate Map -->
  <ConsoleDialog v-if="activateOpen" title="激活地图版本" @close="activateOpen = false">
    <template v-if="activateMap">
      <dl class="property-list">
        <dt>地图</dt><dd>{{ activateMap.mapId }} · {{ activateMap.name }}</dd>
        <dt>将激活版本</dt><dd>{{ activateMap.publishedVersionId }}</dd>
      </dl>
      <div class="inline-warning">激活将切换后台运行地图。在途任务使用安全切换流程处理。</div>
      <label style="display:block;margin:14px 0;font-size:12px;color:#8da5c9">
        确认文字（必须与下面的提示完全一致）
        <input v-model="activateConfirmation" style="width:100%;margin-top:6px" :placeholder="`ACTIVATE ${activateMap.name}`" />
      </label>
      <p class="mono">{{ `ACTIVATE ${activateMap.name}` }}</p>
      <label>激活依据<input v-model="activateEvidence" maxlength="1500" placeholder="填写本次现场与地图核对依据" /></label>
      <label style="display:flex;gap:8px;font-size:12px;color:#8da5c9;margin:8px 0"><input v-model="activateSafetyConfirmed" type="checkbox" />已确认现场安全条件</label>
      <label style="display:flex;gap:8px;font-size:12px;color:#8da5c9;margin:8px 0"><input v-model="activateVehicleConfirmed" type="checkbox" />已确认车辆地图兼容</label>
    </template>
    <template #footer>
      <button @click="activateOpen = false">取消</button>
      <button class="accent-button" :disabled="busy || activateConfirmation.trim() !== `ACTIVATE ${activateMap?.name}` || !activateEvidence.trim() || !activateSafetyConfirmed || !activateVehicleConfirmed" @click="doActivate">确认激活</button>
    </template>
  </ConsoleDialog>

  <!-- Safe Map Switch -->
  <ConsoleDialog v-if="switchOpen" title="地图安全切换" wide @close="switchOpen = false">
    <div style="margin-bottom:16px">
      <dl class="property-list">
        <dt>执行模式</dt><dd>{{ switchStatus?.mode ?? '加载中' }}</dd>
        <dt>当前切换</dt><dd>{{ currentPhase }}</dd>
      </dl>
      <div v-if="switchStatus?.blockers?.length" class="inline-warning">
        <b>阻塞项：</b>
        <p v-for="(blocker, i) in switchStatus.blockers" :key="i">{{ blocker }}</p>
      </div>
    </div>

    <div v-if="switchStatus?.current" style="padding:14px;background:#122b44;border:1px solid #285172;border-radius:4px;margin-bottom:16px">
      <dl class="property-list">
        <dt>切换编号</dt><dd class="mono">{{ switchStatus.current.releaseId }}</dd>
        <dt>目标地图</dt><dd>{{ switchStatus.current.mapId }} · {{ switchStatus.current.versionId }}</dd>
        <dt>当前阶段</dt><dd>{{ switchStatus.current.phaseText }}</dd>
        <dt>开始时间</dt><dd>{{ shortTime(switchStatus.current.startedAt) }}</dd>
        <dt>更新时间</dt><dd>{{ shortTime(switchStatus.current.updatedAt) }}</dd>
      </dl>
    </div>

    <label v-if="!switchStatus?.current">目标地图<select v-model="switchTargetMapId"><option value="">请选择目标地图</option><option v-for="m in catalog.filter(m=>m.publishedVersionId && (!m.isActive || m.publishedVersionId!==m.activeVersionId))" :key="m.mapId" :value="m.mapId">{{m.name}} · {{m.publishedVersionId}}</option></select></label>
    <p v-if="isMapSwitchPhase(switchStatus?.current?.phase,10)">原任务收尾后，请关闭此窗口，在目标地图卡片中点击激活并核对确认文字。中断后的继续收尾只恢复原流程，不会激活新图。</p>
    <label style="display:block;margin:12px 0;font-size:12px;color:#8da5c9">操作依据（必填）
      <textarea v-model="switchEvidence" rows="2" style="width:100%;margin-top:6px;background:#0a1530;color:#c2d7ed;border:1px solid #2b4168;border-radius:3px;padding:8px;resize:vertical;font:inherit" placeholder="描述现场状况和切换原因"></textarea>
    </label>
    <label style="display:flex;gap:8px;font-size:12px;color:#8da5c9;margin:6px 0"><input v-model="switchSafetyConfirmed" type="checkbox" />已确认现场安全</label>
    <label style="display:flex;gap:8px;font-size:12px;color:#8da5c9;margin:6px 0"><input v-model="switchVehicleConfirmed" type="checkbox" />已确认车辆地图兼容</label>
    <label style="display:flex;gap:8px;font-size:12px;color:#8da5c9;margin:6px 0"><input v-model="switchWorkflowConfirmed" type="checkbox" />已确认原任务收尾流程</label>

    <template #footer>
      <button @click="loadSwitchStatus"><IconRefresh :size="14" />刷新状态</button>
      <button :disabled="busy || !!switchStatus?.current || !switchTargetMapId" @click="switchAction('begin')">开始安全切换</button>
      <button :disabled="busy || !isMapSwitchPhase(switchStatus?.current?.phase,10) || switchStatus?.mode !== 'Held'" @click="switchAction('drain')">恢复中断的原任务收尾</button>
      <button :disabled="busy || !isMapSwitchPhase(switchStatus?.current?.phase,20)" @click="switchAction('resume')"><IconCircleCheck :size="14" />确认放行</button>
      <button :disabled="busy || !switchStatus?.current || isMapSwitchPhase(switchStatus?.current?.phase,90)" @click="switchAction('cancel')"><IconX :size="14" />取消切换</button>
    </template>
  </ConsoleDialog>
</template>
