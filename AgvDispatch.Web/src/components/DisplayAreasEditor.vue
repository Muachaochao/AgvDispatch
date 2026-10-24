<script setup lang="ts">
import {computed,onMounted,onUnmounted,ref} from 'vue';
import {onBeforeRouteLeave} from 'vue-router';
import {useDispatchStore} from '../stores/dispatch';
import type {MapSummary,Point} from '../types';
import {areaCenter,rectangle,suggestedStations,type DisplayArea,type DisplayAreaContext,type DisplayAreaDocument} from '../display-areas';
const props=defineProps<{initialMapId?:string}>();
const store=useDispatchStore();
const maps=ref<MapSummary[]>([]),mapId=ref(''),context=ref<DisplayAreaContext|null>(null),areas=ref<DisplayArea[]>([]);
const selected=ref(''),mode=ref<'select'|'rectangle'|'polygon'>('select'),draft=ref<Point[]>([]),message=ref(''),dirty=ref(false),busy=ref(false);
const svg=ref<SVGSVGElement>(),confirmReload=ref(false),selectedMap=ref('');
const current=computed(()=>areas.value.find(a=>a.id===selected.value));
const allPoints=computed(()=>[...(context.value?.map.nodes??[]),...(context.value?.map.edges.flatMap(e=>e.points)??[]),...areas.value.flatMap(a=>a.boundary)]);
const fitted=computed(()=>{const p=allPoints.value;if(!p.length)return {x:-5,y:-5,w:10,h:10};const xs=p.map(p=>p.x),ys=p.map(p=>-p.y),pad=Math.max(1,(Math.max(...xs)-Math.min(...xs))*.1);return {x:Math.min(...xs)-pad,y:Math.min(...ys)-pad,w:Math.max(2,Math.max(...xs)-Math.min(...xs))+pad*2,h:Math.max(2,Math.max(...ys)-Math.min(...ys))+pad*2};});
const viewport=ref<{x:number;y:number;w:number;h:number}|null>(null);
const view=computed(()=>viewport.value??fitted.value),scale=computed(()=>Math.max(view.value.w,view.value.h)/80);
let drag:{index:number;area:DisplayArea}|null=null,start:Point|null=null,pointer:number|null=null,epoch=0,alive=true;
function point(e:PointerEvent|WheelEvent):Point{const m=svg.value!.getScreenCTM()!;const p=new DOMPoint(e.clientX,e.clientY).matrixTransform(m.inverse());return {x:p.x,y:-p.y};}
function touch(){dirty.value=true;message.value='尚未保存展示分区。';}
function select(id:string){if(mode.value==='select')selected.value=id;}
function resetGesture(){drag=null;start=null;pointer=null;}
function down(e:PointerEvent){if(e.button!==0||!context.value||busy.value||mapId.value!==selectedMap.value)return;pointer=e.pointerId;svg.value?.setPointerCapture(e.pointerId);const p=point(e);if(mode.value==='rectangle'){start=p;draft.value=rectangle(p,p);}else if(mode.value==='polygon'){if(!draft.value.length||Math.hypot(draft.value.at(-1)!.x-p.x,draft.value.at(-1)!.y-p.y)>1e-6)draft.value.push(p);}}
function move(e:PointerEvent){if(pointer!==e.pointerId)return;const p=point(e);if(drag){drag.area.boundary[drag.index]=p;touch();}else if(start&&mode.value==='rectangle')draft.value=rectangle(start,p);}
function up(e:PointerEvent){if(pointer!==e.pointerId)return;if(start&&mode.value==='rectangle'){draft.value=rectangle(start,point(e));complete();}resetGesture();}
function vertex(e:PointerEvent,index:number){if(!current.value||mode.value!=='select'||busy.value||mapId.value!==selectedMap.value)return;e.stopPropagation();viewport.value={...view.value};pointer=e.pointerId;svg.value?.setPointerCapture(e.pointerId);drag={index,area:current.value};}
function complete(){if(busy.value)return;const boundary=draft.value.filter((p,i,a)=>i===0||Math.hypot(p.x-a[i-1]!.x,p.y-a[i-1]!.y)>1e-6);if(boundary.length>3&&Math.hypot(boundary[0]!.x-boundary.at(-1)!.x,boundary[0]!.y-boundary.at(-1)!.y)<1e-6)boundary.pop();let signed=0;for(let i=0;i<boundary.length;i++){const a=boundary[i]!,b=boundary[(i+1)%boundary.length]!;signed+=a.x*b.y-b.x*a.y;}if(boundary.length<3||Math.abs(signed)<1e-6){message.value='请画出有面积的区域。';return;}const a:DisplayArea={id:crypto.randomUUID(),name:`区域${areas.value.length+1}`,color:'#42bde0',boundary:boundary.map(p=>({x:p.x,y:p.y})),stationIds:suggestedStations(boundary,context.value!.map),showName:true,showBoundary:true};areas.value.push(a);selected.value=a.id;draft.value=[];mode.value='select';touch();}
function tool(next:'select'|'rectangle'|'polygon'){draft.value=[];resetGesture();mode.value=next;viewport.value={...view.value};}
function cancel(){draft.value=[];resetGesture();mode.value='select';}
function wheel(e:WheelEvent){const p=point(e),v=view.value,f=e.deltaY>0?1.15:1/1.15;if(v.w*f<.1||v.w*f>2e7)return;viewport.value={x:p.x+(v.x-p.x)*f,y:-p.y+(v.y+p.y)*f,w:v.w*f,h:v.h*f};}
function shift(dx:number,dy:number){const v=view.value;viewport.value={...v,x:v.x+v.w*dx,y:v.y+v.h*dy};}
async function load(){if(!mapId.value)return;const mine=++epoch;busy.value=true;try{const c=await store.api<DisplayAreaContext>(`/api/maps/${encodeURIComponent(mapId.value)}/display-areas`);if(!alive||mine!==epoch)return;context.value=c;selectedMap.value=mapId.value;areas.value=structuredClone(c.document?.areas??[]);selected.value=areas.value[0]?.id??'';dirty.value=false;cancel();viewport.value=null;message.value=c.applicable?'已读取后台展示分区。':'地图站点或坐标发生变化：请核对边界和站点，保存后才在大屏显示。';}catch(ex){if(mine===epoch)message.value=String(ex);}finally{if(mine===epoch)busy.value=false;}}
function requestLoad(){if(dirty.value)confirmReload.value=true;else void load();}
async function save(){if(!context.value||!selectedMap.value)return;busy.value=true;try{const doc=await store.api<DisplayAreaDocument>(`/api/maps/${encodeURIComponent(selectedMap.value)}/display-areas`,{expectedRevision:context.value.document?.revision??'',basisHash:context.value.basisHash,areas:areas.value});context.value={...context.value,document:doc,applicable:true};dirty.value=false;message.value='展示分区已保存到后台，大屏将自动更新。';}catch(ex){message.value=String(ex);}finally{busy.value=false;}}
function remove(){if(!current.value)return;areas.value=areas.value.filter(a=>a.id!==selected.value);selected.value=areas.value[0]?.id??'';touch();}
function suggest(){if(!current.value||!context.value)return;current.value.stationIds=suggestedStations(current.value.boundary,context.value.map);touch();}
function beforeUnload(e:BeforeUnloadEvent){if(dirty.value){e.preventDefault();e.returnValue='';}}
onMounted(async()=>{window.addEventListener('beforeunload',beforeUnload);try{maps.value=await store.api<MapSummary[]>('/api/maps');mapId.value=props.initialMapId||store.snapshot?.map?.id||maps.value.find(m=>m.isActive)?.mapId||maps.value[0]?.mapId||'';await load();}catch(ex){message.value=String(ex);}});
onUnmounted(()=>{alive=false;++epoch;window.removeEventListener('beforeunload',beforeUnload);});
onBeforeRouteLeave(()=>!dirty.value||window.confirm('展示分区尚未保存，是否放弃修改并离开？'));
</script>
<template>
<section class="display-area-editor">
 <h3>展示区域</h3><p>为站点分区并添加名称和底色。独立保存到后台，大屏共享显示；不会修改车辆地图、道路、交通规则或PLC。</p>
 <div class="area-toolbar"><label>后台地图 <select v-model="mapId" :disabled="busy"><option value="">请选择已保存的地图</option><option v-for="m in maps" :key="m.mapId" :value="m.mapId">{{m.name}}{{m.isActive?' · 当前运行':''}}</option></select></label><button :disabled="busy||!mapId" @click="requestLoad">读取展示分区</button><button :disabled="busy||!context||mapId!==selectedMap||!!draft.length" @click="save">保存展示分区{{dirty?' *':''}}</button><a href="#/">返回调度大屏</a></div>
 <div v-if="confirmReload" role="alert" class="area-toolbar">读取会丢弃本页未保存修改。<button @click="confirmReload=false;load()">丢弃修改并读取</button><button @click="confirmReload=false">继续编辑</button></div>
 <p role="status">{{message}}</p>
 <fieldset v-if="context" :disabled="busy||mapId!==selectedMap">
 <div class="area-toolbar"><button :class="{active:mode==='select'}" @click="tool('select')">选择 / 编辑</button><button :class="{active:mode==='rectangle'}" @click="tool('rectangle')">新增区域 · 矩形框选</button><button :class="{active:mode==='polygon'}" @click="tool('polygon')">新增区域 · 多边形</button><button v-if="mode==='polygon'" :disabled="draft.length<3" @click="complete">完成多边形</button><button v-if="mode!=='select'" @click="cancel">取消绘制</button><button @click="viewport=null">适配地图</button><button aria-label="视图左移" @click="shift(-.2,0)">←</button><button aria-label="视图右移" @click="shift(.2,0)">→</button><button aria-label="视图上移" @click="shift(0,-.2)">↑</button><button aria-label="视图下移" @click="shift(0,.2)">↓</button></div>
 <p>{{mode==='rectangle'?'在画布按住拖动，松开完成矩形。':mode==='polygon'?'沿边界逐点点击，双击或点击“完成多边形”结束；Esc取消。':'点击区域选择，拖动白色顶点调整边界；滚轮缩放。站点归属由右侧勾选，不会随边界调整自动改变。'}}</p>
 <div class="area-layout"><aside><h4>展示区域 {{areas.length}}</h4><button v-for="a in areas" :key="a.id" :class="{active:a.id===selected}" @click="tool('select');select(a.id)">{{a.name}} · {{a.stationIds.length}}站点</button><p v-if="!areas.length">点击上方“新增区域”开始绘制。</p></aside>
 <svg ref="svg" class="display-area-canvas" tabindex="0" aria-label="展示区域编辑画布" :viewBox="`${view.x} ${view.y} ${view.w} ${view.h}`" @pointerdown="down" @pointermove="move" @pointerup="up" @pointercancel="resetGesture" @lostpointercapture="resetGesture" @wheel.prevent="wheel" @dblclick.prevent="mode==='polygon'&&complete()" @keydown.esc="cancel" @keydown.enter.prevent="mode==='polygon'&&complete()">
  <g v-for="a in areas" :key="a.id" :data-area-id="a.id" @click="select(a.id)"><polygon :points="a.boundary.map(p=>`${p.x},${-p.y}`).join(' ')" :fill="a.color" fill-opacity=".13" :stroke="a.id===selected?'#ffffff':a.showBoundary?a.color:'none'" stroke-width="2" vector-effect="non-scaling-stroke"/><text v-if="a.showName" :x="areaCenter(a).x" :y="-areaCenter(a).y" :font-size="scale*1.7" :fill="a.color" text-anchor="middle" paint-order="stroke" stroke="#0b1229" :stroke-width="scale*.25">{{a.name}}</text></g>
  <polyline v-for="e in context.map.edges" :key="e.id" :points="e.points.map(p=>`${p.x},${-p.y}`).join(' ')" fill="none" stroke="#5b9ba8" stroke-width="1.5" vector-effect="non-scaling-stroke" pointer-events="none"/>
  <g v-for="n in context.map.nodes" :key="n.id" pointer-events="none"><circle :cx="n.x" :cy="-n.y" :r="scale*.4" :fill="current?.stationIds.includes(n.id)?'#fff':'#43d9b1'"/><text :x="n.x+scale*.6" :y="-n.y-scale*.6" :font-size="scale*1.3" fill="#c9f3ff" stroke="#0b1229" :stroke-width="scale*.25" paint-order="stroke">{{n.agvSiteId||n.id}}</text></g>
  <polygon v-if="draft.length" :points="draft.map(p=>`${p.x},${-p.y}`).join(' ')" fill="#42bde0" fill-opacity=".1" stroke="#fff" stroke-dasharray="5 4" stroke-width="2" vector-effect="non-scaling-stroke" pointer-events="none"/>
  <g v-if="current&&mode==='select'"><circle v-for="(p,i) in current.boundary" :key="i" :cx="p.x" :cy="-p.y" :r="scale*.65" fill="#fff" stroke="#152f4e" :stroke-width="scale*.15" class="area-vertex" @pointerdown="vertex($event,i)"/></g>
 </svg>
 <aside v-if="current"><h4>区域属性</h4><label>区域名称<input v-model="current.name" maxlength="80" @input="touch"/></label><label>区域颜色<input v-model="current.color" type="color" @input="touch"/></label><label><input v-model="current.showName" type="checkbox" @change="touch"/>显示区域名称</label><label><input v-model="current.showBoundary" type="checkbox" @change="touch"/>显示区域边界</label><h4>包含站点 {{current.stationIds.length}}</h4><button @click="suggest">按当前边界重新选择站点</button><p>可手动调整；同一站点允许出现在多个展示区域。</p><label v-for="n in context.map.nodes" :key="n.id"><input v-model="current.stationIds" type="checkbox" :value="n.id" @change="touch"/>{{n.agvSiteId||n.id}}<small v-if="n.agvSiteId&&n.agvSiteId!==n.id"> · {{n.id}}</small></label><p v-if="current.stationIds.some(id=>!context!.map.nodes.some(n=>n.id===id))" role="alert">有站点已移除，请按边界重新选择站点后保存。</p><button @click="remove">删除此区域标注</button><small>站点和道路保留。删除后需保存才生效。</small></aside><aside v-else>选择区域后编辑名称、颜色及站点归属。</aside></div>
 </fieldset>
 <p v-else-if="!busy">当前导入预览还没有后台地图编号时，请先在地图编辑保存为后台地图，再到这里选择；本功能不需要发布或激活地图。</p>
</section>
</template>
<style scoped>
.display-area-editor{padding:18px}.display-area-editor p{color:#9cb5ce;line-height:1.7}.area-toolbar{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.area-toolbar a{color:#69d4ee}.area-layout{display:grid;grid-template-columns:180px minmax(300px,1fr) 270px;gap:12px}.area-layout aside{background:#141f36;padding:12px;max-height:660px;overflow:auto}.area-layout aside>button{display:block;width:100%;margin:6px 0}.area-layout label{display:block;margin:10px 0}.area-layout label>input:not([type=checkbox]){display:block;width:100%}.display-area-canvas{width:100%;height:640px;background:#0b1229;touch-action:none;border:1px solid #31415d}.area-vertex{cursor:move}.display-area-editor fieldset{border:0;padding:0;margin:0}.active{border-color:#42bde0;background:#183c50}small{color:#9cb5ce}@media(max-width:1000px){.area-layout{grid-template-columns:1fr}.display-area-canvas{height:460px}.area-layout aside{max-height:300px}}
</style>
