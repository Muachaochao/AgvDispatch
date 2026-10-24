<script setup lang="ts">
import type {AreaAnnotation} from '../workbench';
import {annotationLabel} from '../area-annotations';
defineProps<{areas:AreaAnnotation[];scale:number}>();
</script>
<template>
 <g class="vendor-area-annotations" pointer-events="none" aria-label="厂家区域标注">
  <g v-for="a in areas" :key="a.id" :data-vendor-area="a.id">
   <polygon :points="a.boundary.map(p=>`${p.x},${-p.y}`).join(' ')" :stroke="a.strokeColor" :fill="a.fillColor" :fill-opacity="a.fillOpacity" stroke-width="1.5" vector-effect="non-scaling-stroke"/>
   <text :x="annotationLabel(a).x" :y="-annotationLabel(a).y-scale*.5" text-anchor="middle" :font-size="scale*1.2" fill="#f2c5ff" stroke="#0b1229" :stroke-width="scale*.22" paint-order="stroke" stroke-linejoin="round">{{a.text}}</text>
   <title>{{a.text}} · {{a.sourceType}} · 厂家显示标注</title>
  </g>
 </g>
</template>
