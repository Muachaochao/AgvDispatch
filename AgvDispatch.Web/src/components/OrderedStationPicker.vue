<script setup lang="ts">
import { computed, ref } from 'vue';
const props=defineProps<{modelValue:string[];choices:{id:string;label:string}[];disabled?:boolean;title?:string}>();
const emit=defineEmits<{ 'update:modelValue':[string[]] }>();
const search=ref('');
const available=computed(()=>props.choices.filter(c=>!props.modelValue.includes(c.id)&&
  (c.id+' '+c.label).toLowerCase().includes(search.value.toLowerCase())));
function add(id:string){if(!props.disabled&&!props.modelValue.includes(id))emit('update:modelValue',[...props.modelValue,id]);}
function move(index:number,delta:number){if(props.disabled)return;const values=[...props.modelValue];const target=index+delta;
 if(target<0||target>=values.length)return;[values[index],values[target]]=[values[target]!,values[index]!];emit('update:modelValue',values);}
function remove(id:string){if(!props.disabled)emit('update:modelValue',props.modelValue.filter(x=>x!==id));}
function all(){if(!props.disabled)emit('update:modelValue',[...props.modelValue,...available.value.map(c=>c.id)]);}
</script>
<template>
<fieldset class="station-picker" :disabled="disabled"><legend>{{title??'参与工位 / 接续顺序'}}（已选 {{modelValue.length}} / 配置 {{choices.length}}）</legend>
 <input v-model="search" placeholder="搜索已配置工位编号或取放站点" aria-label="搜索参与工位"/>
 <div class="picker-actions"><button type="button" @click="all">加入筛选结果</button><button type="button" @click="emit('update:modelValue',[])">清空选择</button></div>
 <select aria-label="添加参与工位" value="" @change="add(($event.target as HTMLSelectElement).value);($event.target as HTMLSelectElement).value=''">
  <option value="" disabled>添加工位（{{available.length}}个可选）</option><option v-for="c in available" :key="c.id" :value="c.id">{{c.label}}</option>
 </select>
 <ol><li v-for="(id,index) in modelValue" :key="id"><span>{{choices.find(c=>c.id===id)?.label??id+'（当前配置已移除）'}}</span><button type="button" :disabled="index===0" :aria-label="'上移 '+id" @click="move(index,-1)">↑</button><button type="button" :disabled="index===modelValue.length-1" :aria-label="'下移 '+id" @click="move(index,1)">↓</button><button type="button" :aria-label="'移除 '+id" @click="remove(id)">移除</button></li></ol>
 <p v-if="modelValue.length===0">请选择至少一个工位；按上方顺序接续，编号来自当前配置。</p>
</fieldset>
</template>
<style scoped>
.station-picker{border:1px solid #34536c;padding:14px;min-width:0;display:grid;gap:10px}.station-picker legend{font-size:13px;padding:0 5px}.station-picker input,.station-picker select{width:100%;min-width:0}.picker-actions{display:flex;gap:8px;flex-wrap:wrap}.station-picker ol{max-height:220px;overflow:auto;margin:0;padding-left:28px}.station-picker li{padding:5px 0;overflow-wrap:anywhere}.station-picker li span{display:inline-block;min-width:140px;margin-right:8px}.station-picker button{padding:4px 9px;margin-right:4px}.station-picker p{font-size:12px}
</style>
