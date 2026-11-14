<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, ref, watch, onUnmounted } from 'vue';
import {activationIntentKey,parseActivationIntent,reviewMatches,acknowledgeReview,reviewLabel,type ActivationIntent,type ActivationReview} from '../map-activation-review';
import { useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { shortTime } from '../dashboard';
import { isMapSwitchPhase } from '../map-lifecycle';
import { mapSwitchModeLabel } from '../operator-labels';
import type { MapSummary } from '../types';
import { IconMap, IconArrowLeft, IconRefresh, IconEye, IconPlayerPlay, IconEdit, IconAlertTriangle, IconCircleCheck, IconX, IconSearch, IconChevronDown } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import MapPublishDialog from '../MapPublishDialog.vue';

const store = useDispatchStore();
const unitLabel=(unit:string|null|undefined)=>unit==='m'?'米':unit||'未提供';
const coordinateLabel=(value:string|null|undefined)=>({RobotWorld:'车端世界坐标',SmapWorldTranslated:'厂家平移坐标'} as Record<string,string>)[value??'']??'坐标定义待核对';
const toast = useToastStore();
const router = useRouter();
const route = useRoute();
const catalog = ref<MapSummary[]>([]);
const catalogError = ref('');
const selectedMap = ref<MapSummary | null>(null);
const mapSearch = ref('');
const mapStatus = ref<'all'|'active'|'published'|'draft'>('all');
const filteredCatalog = computed(() => catalog.value
  .filter(map => !mapSearch.value.trim() || `${map.name} ${map.mapId} ${map.description}`.toLowerCase().includes(mapSearch.value.trim().toLowerCase()))
  .filter(map => mapStatus.value === 'all'
    || (mapStatus.value === 'active' && map.isActive)
    || (mapStatus.value === 'published' && !!map.publishedVersionId)
    || (mapStatus.value === 'draft' && !map.publishedVersionId))
  .slice()
  .sort((a,b) => Number(b.isActive)-Number(a.isActive) || Number(!!b.publishedVersionId)-Number(!!a.publishedVersionId) || Date.parse(b.updatedAt)-Date.parse(a.updatedAt)));
const busy = ref(false);
const publishMapId = ref('');
function published() { publishMapId.value = ''; toast.success('地图草稿已发布；需要激活并完成安全流程后才用于运行。'); void loadCatalog(); }
const activationIntent=ref<ActivationIntent|null>(null),review=ref<ActivationReview|null>(null),reviewOpen=ref(false),reviewBusy=ref(false),reviewError=ref(''),storageError=ref('');
try{activationIntent.value=parseActivationIntent(localStorage.getItem(activationIntentKey));reviewOpen.value=!!activationIntent.value;}catch(e){storageError.value=String(e);}
const sourceContext=computed(()=>[store.demoMode,store.adapterIdentity?.sha256,store.snapshot?.map?.id,store.snapshot?.map?.version,store.snapshot?.map?.definitionHash].join('|'));
const reviewCurrent=computed(()=>store.fresh&&!store.demoMode&&reviewMatches(review.value,activationIntent.value,store.now));
const mayAcknowledge=computed(()=>reviewCurrent.value&&acknowledgeReview(review.value,activationIntent.value,store.now));
const activationBlocked=computed(()=>!!activationIntent.value||!!storageError.value||!store.fresh||store.demoMode);
let reviewEpoch=0,switchEpoch=0;
function keepIntent(i:ActivationIntent){const existing=parseActivationIntent(localStorage.getItem(activationIntentKey));if(existing){activationIntent.value=existing;throw new Error('已有原激活待核对，未发送新请求。');}localStorage.setItem(activationIntentKey,JSON.stringify(i));activationIntent.value=i;review.value=null;}
function clearIntent(){const existing=parseActivationIntent(localStorage.getItem(activationIntentKey));if(existing&&existing.releaseId!==activationIntent.value?.releaseId)throw new Error('本地原编号已变化，不能清除其他复核记录。');localStorage.removeItem(activationIntentKey);activationIntent.value=null;review.value=null;}
async function loadActivationReview(){reviewOpen.value=true;review.value=null;reviewError.value='';if(!activationIntent.value||!store.fresh||store.demoMode){reviewError.value='请连接原后台，再点击只读复核。';return;}const mine=++reviewEpoch,ctx=sourceContext.value,i=activationIntent.value;reviewBusy.value=true;try{const r=await store.api<ActivationReview>('/api/maps/activation-review',{releaseId:i.releaseId,expectedMapId:i.mapId,expectedVersionId:i.versionId});if(mine===reviewEpoch&&ctx===sourceContext.value&&i===activationIntent.value)review.value=r;}catch(e){if(mine===reviewEpoch)reviewError.value=String(e);}finally{if(mine===reviewEpoch)reviewBusy.value=false;}}
async function acknowledgeActivation(){if(!mayAcknowledge.value)return;try{clearIntent();reviewOpen.value=false;await loadCatalog();await loadSwitchStatus();}catch(e){storageError.value=String(e);}}
watch(sourceContext,()=>{++reviewEpoch;++switchEpoch;review.value=null;reviewBusy.value=false;switchStatus.value=null;switchSafetyConfirmed.value=false;switchVehicleConfirmed.value=false;switchWorkflowConfirmed.value=false;if(reviewOpen.value&&activationIntent.value&&store.fresh)void loadActivationReview();});
watch(()=>store.fresh,f=>{if(!f){++reviewEpoch;++switchEpoch;review.value=null;reviewBusy.value=false;switchStatus.value=null;switchSafetyConfirmed.value=false;switchVehicleConfirmed.value=false;switchWorkflowConfirmed.value=false;}else if(reviewOpen.value&&activationIntent.value)void loadActivationReview();});


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
const activateGeometryUpdateConfirmed = ref(false);

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

async function restoreVersion(version: { versionId: string; versionNumber: number }) {
  if (!store.fresh || store.demoMode) { toast.warning('请先连接现场后台。'); return; }
  if (!window.confirm(`把地图 V${version.versionNumber} 恢复为新的可编辑草稿？\n\n历史版本不会被覆盖；恢复后仍需核对、发布并经过安全激活，当前运行地图不会立即改变。`)) return;
  busy.value = true;
  try {
    const [source, workspace]: any[] = await Promise.all([
      store.api<any>(`/api/maps/${encodeURIComponent(versionsMapId.value)}/versions/${encodeURIComponent(version.versionId)}`),
      store.api<any>(`/api/maps/${encodeURIComponent(versionsMapId.value)}/workspace`)
    ]);
    if (!source?.definition || !workspace?.draft) throw new Error('历史版本定义或当前草稿不完整。');
    const draft = workspace.draft;
    const result: any = await store.api('/api/maps/draft', {
      mapId: workspace.project.mapId,
      versionId: draft.versionId,
      name: source.definition.name,
      description: workspace.project.description,
      backgroundFilePath: workspace.project.backgroundFilePath,
      coordinateSystem: source.definition.coordinateSystem,
      unit: source.definition.unit,
      coordinateScale: workspace.project.coordinateScale,
      definition: source.definition,
      expectedEditToken: draft.editToken
    });
    if (!result.accepted) throw new Error(result.message ?? '历史地图未能恢复为草稿。');
    toast.success(`地图 V${version.versionNumber} 已恢复为当前草稿；未发布、未激活。`);
    versionsOpen.value = false;
    router.push({ name: 'planning', query: { tab: 'map', mapId: versionsMapId.value } });
  } catch (ex) {
    toast.error(`地图回滚准备失败：${String(ex)}`);
  } finally {
    busy.value = false;
  }
}

function openEditDraft(mapId: string) {
  router.push({ name: 'planning', query: { tab: 'map', mapId } });
}

function openActivate(map: MapSummary) {
  if(activationBlocked.value)return;
  activateMap.value = map; activateOpen.value = true;
  activateConfirmation.value = ''; activateEvidence.value=''; activateSafetyConfirmed.value = false; activateVehicleConfirmed.value = false; activateGeometryUpdateConfirmed.value = false;
}

async function doActivate() {
  if (activationBlocked.value || !activateMap.value || !activateConfirmation.value.trim()) return;
  busy.value = true;const activationSource=sourceContext.value;
  try {
    let pending=await store.api<MapSwitch>('/api/maps/switch');
    if(pending.current&&(pending.current.mapId!==activateMap.value.mapId||pending.current.versionId!==activateMap.value.publishedVersionId||!isMapSwitchPhase(pending.current.phase,10))){toast.warning('当前已有其他切换或已激活待放行，请先在安全切换中处理原批次。');return;}
    if(!pending.current){const begun=await store.api<{accepted:boolean;message:string}>('/api/maps/switch/begin',{mapId:activateMap.value.mapId,versionId:activateMap.value.publishedVersionId});if(!begun.accepted){toast.warning(begun.message);return;}pending=await store.api<MapSwitch>('/api/maps/switch');}
    const original=pending.current;if(!original||original.mapId!==activateMap.value.mapId||original.versionId!==activateMap.value.publishedVersionId||!isMapSwitchPhase(original.phase,10))throw new Error('未取得匹配的原切换编号，未发送激活。');
    if(activationSource!==sourceContext.value||!store.fresh||store.demoMode)throw new Error('原后台上下文已变化，未发送激活。');
    keepIntent({schema:1,releaseId:original.releaseId,mapId:original.mapId,versionId:original.versionId,requestedAt:new Date().toISOString()});
    const result = await store.api<{ accepted: boolean; message: string }>('/api/maps/activate', {
      expectedReleaseId:original.releaseId,
      mapId: activateMap.value.mapId,
      versionId: activateMap.value.publishedVersionId,
      confirmationText: `ACTIVATE ${activateMap.value.name}`,
      siteSafetyConfirmed: activateSafetyConfirmed.value,
      vehicleMapCompatibilityConfirmed: activateVehicleConfirmed.value,
      stoppedOffStationGeometryUpdateConfirmed: activateGeometryUpdateConfirmed.value,
      evidence: activateEvidence.value.trim()
    });
    if(typeof result.accepted!=='boolean')throw new Error('激活回执格式无效，结果待核对。');
    if(result.accepted){activateOpen.value=false;await loadActivationReview();void loadCatalog();}
    else{activateOpen.value=false;toast.warning(result.message||'后台未接受本次请求，仍需按原编号复核。');await loadActivationReview();}
  } catch(ex){if(activationIntent.value){activateOpen.value=false;toast.warning('激活结果未确认，请只读复核原编号；不会自动重发或放行。');await loadActivationReview();}else toast.error(`未完成激活准备：${String(ex)}`);}

  finally { busy.value = false; }
}

async function loadSwitchStatus() {
  switchOpen.value = true; switchEvidence.value = ''; switchSafetyConfirmed.value = false;
  switchVehicleConfirmed.value = false; switchWorkflowConfirmed.value = false;
  switchStatus.value=null;const mine=++switchEpoch,ctx=sourceContext.value;
  try { const r=await store.api<MapSwitch>('/api/maps/switch');if(mine===switchEpoch&&ctx===sourceContext.value&&store.fresh)switchStatus.value=r; }
  catch (ex) { toast.error(`安全切换状态加载失败：${String(ex)}`); }
}

async function switchAction(action: 'begin' | 'drain' | 'resume' | 'cancel') {
  if(!store.fresh||store.demoMode||storageError.value){toast.warning('请先核对原后台连接及本地复核记录。');return;}
  if(action==='cancel'&&activationIntent.value){
    const original=activationIntent.value,ctx=sourceContext.value;
    try{const fresh=await store.api<ActivationReview>('/api/maps/activation-review',{releaseId:original.releaseId,expectedMapId:original.mapId,expectedVersionId:original.versionId});
      if(ctx!==sourceContext.value||original!==activationIntent.value||!store.fresh)return;review.value=fresh;
    }catch(e){toast.warning(`原切换复核失败，未取消：${String(e)}`);return;}
  }
  if(activationIntent.value&&!(action==='cancel'&&reviewCurrent.value&&review.value?.outcome==='OriginalMapObserved'&&switchStatus.value?.current?.releaseId===activationIntent.value.releaseId)){toast.warning('存在原激活待核对，只能只读复核或在有效原图观察下取消原切换。');return;}
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
    if (result.accepted) { toast.success(result.message);if(activationIntent.value)await loadActivationReview();else void loadSwitchStatus(); }
    else toast.warning(result.message);
  } catch (ex) { toast.error(`安全切换操作失败：${String(ex)}`); }
  finally { busy.value = false; }
}

function storageChanged(e:StorageEvent){if(e.key!==activationIntentKey)return;++reviewEpoch;review.value=null;try{activationIntent.value=parseActivationIntent(e.newValue);}catch(ex){storageError.value=String(ex);}}
window.addEventListener('storage',storageChanged);
onUnmounted(()=>{++reviewEpoch;++switchEpoch;window.removeEventListener('storage',storageChanged);});
const currentPhase = computed(() => switchStatus.value?.current?.phaseText ?? '无进行中');
</script>

<template>
  <section class="management-page map-catalog-page">
    <header class="management-title">
      <h2><IconMap :size="23" />地图管理</h2>
      <span>共 {{ catalog.length }} 张</span>
      <button class="accent-button" @click="router.push({ name: 'planning', query: { tab: 'map' } })"><IconEdit :size="14" />地图编辑</button>
      <button @click="router.push({ name: 'planning', query: { tab: 'plans' } })"><ActionIcon name="folder"/>方案管理</button>
      <button @click="loadSwitchStatus"><IconAlertTriangle :size="14" />安全切换</button>
      <button @click="loadCatalog"><IconRefresh :size="15" />刷新</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <div class="map-workflow"><b>地图使用流程</b><span>工程编辑保存草稿 → 本页发布 → 激活与安全切换 → 大屏显示当前运行图</span></div>
    <div class="map-query-toolbar">
      <el-input v-model="mapSearch" clearable :prefix-icon="IconSearch" placeholder="搜索地图名称、编号或说明" aria-label="搜索地图"/>
      <el-select v-model="mapStatus" aria-label="筛选地图状态" style="width:150px">
        <el-option label="全部地图" value="all"/><el-option label="当前运行" value="active"/><el-option label="已发布" value="published"/><el-option label="未发布" value="draft"/>
      </el-select>
      <span>显示 {{filteredCatalog.length}} / {{catalog.length}} 张</span>
      <el-button :icon="IconRefresh" @click="loadCatalog">刷新目录</el-button>
    </div>
    <p v-if="route.query.engineeringVersion" class="inline-warning">工程接入目标：{{route.query.mapId||'请返回预检取得地图编号'}} / {{route.query.mapVersion||'请返回预检取得准确版本'}}。请核对卡片的发布版本；版本不同应重新绑定工程，不能用相似地图代替。激活及安全切换完成后，<RouterLink :to="{path:'/engineering-runtime',query:{version:route.query.engineeringVersion}}">返回工程执行接入重新预检</RouterLink>。</p>
    <div class="map-catalog-note">本页统一管理地图发布、版本与激活。发布是生成后台版本，车辆上传尚未开放。大屏只显示后台当前运行地图；现场条件或地图兼容未核定时，激活保持禁用。</div>
    <p v-if="storageError" class="inline-warning">本地激活复核记录不可读，地图写操作保持禁用：{{storageError}}</p>
    <p v-if="activationIntent" class="inline-warning activation-pending">存在原激活结果待核对：{{activationIntent.releaseId}} · {{activationIntent.mapId}} / {{activationIntent.versionId}}。禁止重复激活和直接放行。<button @click="loadActivationReview"><ActionIcon name="search"/>只读复核原激活</button></p>
    <p v-if="catalogError" class="inline-warning">{{ catalogError }}</p>
    <div class="map-catalog">
      <article v-for="map in filteredCatalog" :key="map.mapId" class="map-card" :class="{active:map.isActive}">
        <div class="map-card-art"><IconMap :size="36" stroke="1" /><el-tag :type="map.isActive ? 'success' : map.publishedVersionId ? 'info' : 'warning'" effect="plain">{{ map.isActive ? '当前运行' : map.publishedVersionId ? '已发布' : '未发布' }}</el-tag><b :title="map.mapId">{{ map.mapId }}</b><small>{{ unitLabel(map.unit) }} · {{ coordinateLabel(map.coordinateSystem) }}</small></div>
        <h3>{{ map.name }}</h3>
        <p>{{ map.description || '未填写说明' }}</p>
        <dl><dt>运行版本</dt><dd>{{ map.activeVersionId ?? '—' }}</dd><dt>发布版本</dt><dd>{{ map.publishedVersionId ?? '—' }}</dd></dl>
        <footer>
          <el-button v-if="map.isActive && map.mapId === store.snapshot?.map?.id" type="primary" @click="router.push('/')"><ActionIcon name="map"/>查看运行图</el-button>
          <el-button v-else-if="map.publishedVersionId && (!map.isActive || map.publishedVersionId !== map.activeVersionId)" type="primary" :disabled="busy||activationBlocked" @click="openActivate(map)"><IconPlayerPlay :size="14" />激活</el-button>
          <el-button v-else type="primary" plain :disabled="busy || activationBlocked" @click="publishMapId = map.mapId"><ActionIcon name="publish"/>发布草稿</el-button>
          <el-dropdown trigger="click">
            <el-button>更多操作<IconChevronDown :size="13"/></el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item :icon="IconEye" @click="selectedMap = map">地图详情</el-dropdown-item>
                <el-dropdown-item @click="loadVersions(map.mapId)"><ActionIcon name="history"/>版本历史</el-dropdown-item>
                <el-dropdown-item :icon="IconEdit" @click="openEditDraft(map.mapId)">编辑草稿</el-dropdown-item>
                <el-dropdown-item :disabled="busy || activationBlocked" @click="publishMapId = map.mapId"><ActionIcon name="publish"/>发布当前草稿</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </footer>
      </article>
      <el-empty v-if="!filteredCatalog.length && !catalogError" class="map-empty" :description="catalog.length ? '没有符合当前筛选条件的地图。' : '没有可查看的地图。连接后台后刷新，或在工程编辑中新建。'" :image-size="84"/>
    </div>
  </section>

  <MapPublishDialog v-if="publishMapId" :map-id="publishMapId" @close="publishMapId = ''" @published="published" />

  <!-- Map Detail -->
  <ConsoleDialog v-if="selectedMap" title="地图版本信息" @close="selectedMap = null">
    <dl class="property-list map-properties">
      <dt>地图编号</dt><dd>{{ selectedMap.mapId }}</dd>
      <dt>地图名称</dt><dd>{{ selectedMap.name }}</dd>
      <dt>坐标系</dt><dd>{{ coordinateLabel(selectedMap.coordinateSystem) }}</dd>
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
        <thead><tr><th>版本号</th><th>状态</th><th>航点数</th><th>道路数</th><th>更新时间</th><th>操作</th></tr></thead>
        <tbody>
          <tr v-for="v in versions" :key="v.versionId">
            <td class="mono cyan">V{{ v.versionNumber }}</td>
            <td>{{ ({Draft:'草稿',Published:'已发布',Active:'当前使用',Retired:'已停用',Archived:'已归档'} as Record<string,string>)[v.status] ?? '状态待核对' }}</td>
            <td>{{ v.nodeCount }}</td>
            <td>{{ v.edgeCount }}</td>
            <td>{{ shortTime(v.updatedAt) }}</td>
            <td><button :disabled="busy || store.demoMode" @click="restoreVersion(v)"><ActionIcon name="undo"/>恢复为新草稿</button></td>
          </tr>
        </tbody>
      </table>
      <p v-if="!versions.length" class="empty-panel">没有版本记录</p>
      <p class="inline-warning">回滚只复制历史内容到当前草稿，不覆盖历史版本，也不会立即改变运行地图。之后仍须发布并完成安全切换。</p>
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
        <input v-model="activateConfirmation" style="width:100%;margin-top:6px" :placeholder="`激活 ${activateMap.name}`" />
      </label>
      <p class="mono">{{ `激活 ${activateMap.name}` }}<small>后台协议会自动转换为 ACTIVATE 指令，现场无需输入英文。</small></p>
      <label>激活依据<input v-model="activateEvidence" maxlength="1500" placeholder="填写本次现场与地图核对依据" /></label>
      <label style="display:flex;gap:8px;font-size:12px;color:#8da5c9;margin:8px 0"><input v-model="activateSafetyConfirmed" type="checkbox" />已确认现场安全条件</label>
      <label style="display:flex;gap:8px;font-size:12px;color:#8da5c9;margin:8px 0"><input v-model="activateVehicleConfirmed" type="checkbox" />已确认车辆地图兼容</label>
      <label style="display:flex;gap:8px;font-size:12px;color:#dfbd83;margin:8px 0"><input v-model="activateGeometryUpdateConfirmed" type="checkbox" />同一地图仅更新几何：车辆已停稳且定位正常，虽不在正式站点，允许本次安全切换</label>
    </template>
    <template #footer>
      <button @click="activateOpen = false"><ActionIcon name="close"/>取消</button>
      <button class="accent-button" :disabled="busy || activationBlocked || activateConfirmation.trim() !== `激活 ${activateMap?.name}` || !activateEvidence.trim() || !activateSafetyConfirmed || !activateVehicleConfirmed" @click="doActivate"><ActionIcon name="activate"/>确认激活</button>
    </template>
  </ConsoleDialog>

  <!-- Safe Map Switch -->
  <ConsoleDialog v-if="switchOpen" title="地图安全切换" wide @close="switchOpen = false">
    <div style="margin-bottom:16px">
      <dl class="property-list">
        <dt>执行模式</dt><dd>{{ mapSwitchModeLabel(switchStatus?.mode) }}</dd>
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
      <button :disabled="busy || activationBlocked || !!switchStatus?.current || !switchTargetMapId" @click="switchAction('begin')"><ActionIcon name="safety"/>开始安全切换</button>
      <button :disabled="busy || activationBlocked || !isMapSwitchPhase(switchStatus?.current?.phase,10) || switchStatus?.mode !== 'Held'" @click="switchAction('drain')"><ActionIcon name="route"/>恢复中断的原任务收尾</button>
      <button :disabled="busy || activationBlocked || !isMapSwitchPhase(switchStatus?.current?.phase,20)" @click="switchAction('resume')"><IconCircleCheck :size="14" />确认放行</button>
      <button :disabled="busy || !store.fresh || !!storageError || (activationIntent && !(review?.outcome==='OriginalMapObserved' && switchStatus?.current?.releaseId===activationIntent.releaseId)) || !switchStatus?.current || isMapSwitchPhase(switchStatus?.current?.phase,90)" @click="switchAction('cancel')"><IconX :size="14" />重新核对并取消切换</button>
    </template>
  </ConsoleDialog>

  <ConsoleDialog v-if="reviewOpen" title="原地图激活结果 · 只读复核" wide @close="reviewOpen=false">
    <section class="activation-review"><p>原编号 {{activationIntent?.releaseId}} · {{activationIntent?.mapId}} / {{activationIntent?.versionId}}</p>
    <p class="inline-warning">通信失败不等于未提交；只读复核不重发激活、不取消切换、不恢复派发、不解除占用。</p>
    <button :disabled="reviewBusy||!store.fresh||store.demoMode" @click="loadActivationReview"><ActionIcon name="refresh"/>刷新只读复核</button><p v-if="reviewError" role="status">{{reviewError}}</p>
    <template v-if="review"><h3>{{reviewLabel(review.outcome)}}</h3><p>{{reviewCurrent?'有效观察':'观察已失效，联动禁用'}} · {{shortTime(review.capturedAt)}}</p><p>{{review.message}}</p>
    <dl class="property-list"><dt>持久化阶段</dt><dd>{{review.record?.phaseText||'原记录未找到'}}</dd><dt>数据库活动图</dt><dd>{{review.persistentMapId||'无'}} / {{review.persistentVersionId||'无'}}</dd><dt>运行图</dt><dd>{{review.runtimeMapId||'无'}} / {{review.runtimeVersionId||'无'}}</dd><dt>执行门</dt><dd>{{mapSwitchModeLabel(review.mode)}} · 观察到的新任务门 {{review.canStartNewWork?'开（不是放行许可）':'关'}}</dd><dt>本地内容一致</dt><dd>{{review.contentConsistent?'是':'否/未知'}}</dd></dl>
    <details><summary>查看本地内容摘要和证据边界</summary><p style="overflow-wrap:anywhere">数据库SHA256 {{review.persistentDefinitionHash||'未取得'}}<br/>运行图SHA256 {{review.runtimeDefinitionHash||'未取得'}}</p><p v-for="e in review.evidence" :key="e">{{e}}</p></details>
    <p v-if="review.outcome==='OriginalMapObserved'">未观察到提交不能自动重试。进入安全切换查看具体阻塞；核对后可人工取消尚未提交的原切换，再编辑修正版。</p>
    <button v-if="review.outcome==='OriginalMapObserved'" @click="reviewOpen=false;loadSwitchStatus()">查看阻塞并处理原切换</button>
    </template></section>
    <template #footer><button @click="reviewOpen=false"><ActionIcon name="close"/>关闭并保留待核对</button><button :disabled="!mayAcknowledge" @click="acknowledgeActivation"><ActionIcon name="safety"/>已知原结果，转安全流程核对</button></template>
  </ConsoleDialog>
</template>

<style scoped>
.map-catalog-page > :not(.map-catalog){flex-shrink:0}
/* The page owns scrolling. Grid rows must size to their content, never to
   the remaining viewport height, or overflow:hidden clips card actions. */
.map-catalog-page > .map-catalog{flex:none;overflow:visible;grid-auto-rows:max-content;grid-template-columns:repeat(auto-fill,minmax(min(100%,340px),1fr));align-items:stretch;padding-bottom:20px}
.map-card{display:flex;flex-direction:column;min-width:0;height:auto}
.map-card-art{height:118px;flex-shrink:0;gap:6px;padding-top:20px}
.map-card-art>b{width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:11px}
.map-card h3{overflow-wrap:anywhere;line-height:1.6}
.map-card>p{min-height:0;overflow-wrap:anywhere;padding-bottom:8px}
.map-card dl{grid-template-columns:62px minmax(0,1fr);line-height:1.6}
.map-card footer{margin-top:auto;display:grid;grid-template-columns:minmax(0,1fr) auto;gap:8px;padding:12px}
.map-card footer button{min-width:0;min-height:32px;padding:6px;font-size:11px}
.map-query-toolbar{display:grid;grid-template-columns:minmax(260px,1fr) 150px auto auto;align-items:center;gap:10px;padding:10px 12px;border:1px solid var(--ui-line);border-radius:var(--ui-radius);background:var(--ui-surface-raised)}
.map-query-toolbar>span{color:var(--ui-text-3);font-size:11px;white-space:nowrap}
.map-card.active{border-color:color-mix(in srgb,var(--ui-primary) 58%,var(--ui-line));box-shadow:inset 0 3px var(--ui-primary)}
.map-card-art>.el-tag{position:absolute;left:12px;top:11px}
.map-card>p{display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden}
.map-empty{grid-column:1/-1;min-height:260px}
@media(max-width:780px){.map-query-toolbar{grid-template-columns:1fr}.map-query-toolbar .el-select{width:100%!important}.map-card footer{grid-template-columns:1fr}}
</style>
