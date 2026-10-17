<script setup lang="ts">
import type { useMapEditor } from './use-map-editor';
defineProps<{editor:ReturnType<typeof useMapEditor>}>();
const alignments=[['left','左对齐'],['right','右对齐'],['top','上对齐'],['bottom','下对齐'],['centerX','水平居中'],['centerY','垂直居中'],['distributeX','水平等距'],['distributeY','垂直等距']] as const;
</script>
<template>
 <div v-if="editor.panel==='batch'" class="editor-command-panel">
  <h4>已选 {{editor.count}} 个对象</h4>
  <p>节点 {{editor.selection.node.length}} · 道路 {{editor.selection.edge.length}} · 区域 {{editor.selection.resource.length}} · 场景 {{editor.selection.scene.length}}</p>
  <div class="command-grid"><button @click="editor.selectAll">全选可编辑对象</button><button @click="editor.clear">清空选择</button><button :disabled="!editor.count" @click="editor.copy">复制 Ctrl+C</button><button :disabled="!editor.clipboard" @click="editor.pasteObjects">粘贴 Ctrl+V</button><button :disabled="!editor.count" @click="editor.duplicate">复制并偏移</button><button :disabled="!editor.count" @click="editor.remove">删除 Delete</button></div>
  <h4>移动节点 / 区域 / 场景</h4>
  <div class="two-fields"><label>ΔX（米）<input v-model.number="editor.batch.dx" type="number" step=".1"/></label><label>ΔY（米）<input v-model.number="editor.batch.dy" type="number" step=".1"/></label></div>
  <button :disabled="!editor.count" @click="editor.moveSelected">应用移动量</button>
  <h4>对齐与变换</h4><p>对齐针对所选节点；等距至少3个。旋转 / 镜像以所选对象包围盒中心为轴。</p>
  <div class="command-grid"><button v-for="[mode,label] in alignments" :key="mode" :disabled="editor.selection.node.length<(mode.startsWith('distribute')?3:2)" @click="editor.align(mode)">{{label}}</button><button :disabled="!editor.count" @click="editor.transform('rotate')">逆时针 90°</button><button :disabled="!editor.count" @click="editor.transform('rotateCW')">顺时针 90°</button><button :disabled="!editor.count" @click="editor.transform('mirrorX')">左右镜像</button><button :disabled="!editor.count" @click="editor.transform('mirrorY')">上下镜像</button><button :disabled="editor.selection.node.length!==2" @click="editor.merge">合并两节点</button></div>
  <h4>道路与启停</h4>
  <label>道路限速（米/秒）<input v-model.number="editor.batch.speed" type="number" min=".001" step=".1"/></label>
  <div class="command-grid"><button :disabled="!editor.selection.edge.length" @click="editor.roadBatch({speedLimit:Number(editor.batch.speed)})">应用限速</button><button :disabled="!editor.selection.edge.length" @click="editor.reverse">反转起终点</button><button :disabled="!editor.selection.edge.length" @click="editor.roadBatch({bidirectional:true})">设为双向</button><button :disabled="!editor.selection.edge.length" @click="editor.roadBatch({bidirectional:false})">设为单向</button><button :disabled="!editor.count" @click="editor.enabled(true)">启用节点/道路</button><button :disabled="!editor.count" @click="editor.enabled(false)">停用节点/道路</button></div>
  <label>拆分位置（参数百分比 1–99）<input v-model.number="editor.batch.splitAt" type="number" min="1" max="99"/></label><button :disabled="editor.selection.edge.length!==1" @click="editor.split">拆分所选道路（保留曲线）</button>
  <h4>拓扑工具</h4><p>按节点选择顺序连接。交叉拆分只处理同图层直线；没有选择两条道路时处理所有可编辑直线。</p>
  <div class="command-grid"><button :disabled="editor.selection.node.length<2" @click="editor.connect('chain')">自动顺序连接</button><button :disabled="editor.selection.node.length<2" @click="editor.connect('orthogonal')">正交连接</button><button :disabled="editor.selection.node.length<3" @click="editor.connect('loop')">闭合环路</button><button @click="editor.cleanup">清理失效/重复道路</button><button @click="editor.intersections">拆分交叉路口</button><button :disabled="!editor.count" @click="editor.moveToLayer">移至当前图层</button></div>
 </div>
 <div v-else-if="editor.panel==='layers'" class="editor-command-panel">
  <h4>图层管理</h4><p>隐藏、锁定或关闭选择后，对象不能被框选或修改；地图数据仍保留。</p>
  <label>当前绘图层<select v-model="editor.currentLayer" @change="editor.settings"><option v-for="layer in editor.layerList" :key="layer.id" :value="layer.id">{{layer.name}}{{layer.isLocked?' · 锁定':''}}{{!layer.visible?' · 隐藏':''}}</option></select></label>
  <label>新图层名称<input v-model="editor.batch.newLayerName" maxlength="80"/></label><button @click="editor.addLayer">新增图层</button>
  <article v-for="layer in editor.layerList" :key="layer.id" class="layer-card" :class="{current:layer.id===editor.currentLayer}">
   <input :value="layer.name" :aria-label="`图层 ${layer.id} 名称`" maxlength="80" @change="editor.updateLayer(layer.id,{name:($event.target as HTMLInputElement).value||layer.id})"/>
   <small>{{layer.id}}</small><div class="layer-switches">
    <label><input type="checkbox" :checked="layer.visible" @change="editor.updateLayer(layer.id,{visible:($event.target as HTMLInputElement).checked})"/>显示</label>
    <label><input type="checkbox" :checked="layer.isLocked" @change="editor.updateLayer(layer.id,{isLocked:($event.target as HTMLInputElement).checked})"/>锁定</label>
    <label><input type="checkbox" :checked="layer.isAllowSelect" @change="editor.updateLayer(layer.id,{isAllowSelect:($event.target as HTMLInputElement).checked})"/>可选</label>
   </div>
   <label>不透明度 {{Math.round(layer.alpha*100)}}%<input type="range" min=".1" max="1" step=".1" :value="layer.alpha" @change="editor.updateLayer(layer.id,{alpha:Number(($event.target as HTMLInputElement).value)})"/></label>
   <label>绘图颜色<input type="color" :value="layer.color||'#40a6c4'" @change="editor.updateLayer(layer.id,{color:($event.target as HTMLInputElement).value})"/></label>
   <label>道路 / 区域线宽（px）<input type="number" min=".5" max="10" step=".5" :value="layer.strokeWidth" @change="editor.updateLayer(layer.id,{strokeWidth:Number(($event.target as HTMLInputElement).value)})"/></label>
   <button @click="editor.updateLayer(layer.id,{color:null})">使用对象默认色</button><button :disabled="layer.id==='navigation'||layer.isLocked" title="保留默认导航图层；锁定层需先解锁" @click="editor.deleteLayer(layer.id)">删除并迁移对象</button>
  </article>
 </div>
 <div v-else class="editor-command-panel">
  <h4>操作历史 · {{editor.undoStack.length}} / 100</h4><p>一次完整拖动或关联刷记为一次操作。最多100步，并受32MiB快照预算限制；新编辑会清空重做分支。</p>
  <div class="command-grid"><button :disabled="!editor.undoStack.length" @click="editor.history()">撤销 Ctrl+Z</button><button :disabled="!editor.redoStack.length" @click="editor.history(true)">重做 Ctrl+Y</button></div>
  <button :disabled="!editor.undoStack.length" @click="editor.restore(-1)">回到最早保留状态</button>
  <ol class="editor-history"><li v-for="(entry,index) in [...editor.undoStack].reverse()" :key="entry.at+'-'+index"><button @click="editor.restore(editor.undoStack.length-index-1)"><strong>{{entry.label}}</strong><small>{{entry.at}} · 回退到此操作之后</small></button></li></ol>
  <p v-if="editor.redoStack.length">可重做：{{editor.redoStack.map(e=>e.label).reverse().join(' → ')}}</p>
 </div>
</template>
