<script setup lang="ts">
import {
  IconDeviceFloppy, IconDownload, IconUpload, IconRefresh, IconHistory,
  IconEye, IconEdit, IconTrash, IconPlus, IconCopy, IconMap, IconRoute,
  IconMapPin, IconSettings, IconCheck, IconX, IconCloudUpload, IconFolder,
  IconArrowBackUp, IconArrowForwardUp, IconStack2, IconAdjustments,
  IconPointer, IconHandStop, IconLine, IconVectorBezier2, IconRuler,
  IconPolygon, IconBrush, IconCube, IconListDetails, IconRobot, IconSearch,
  IconArrowsMaximize, IconPlayerPlay, IconShieldCheck
} from '@tabler/icons-vue';

const icons = {
  save: IconDeviceFloppy, download: IconDownload, upload: IconUpload,
  refresh: IconRefresh, history: IconHistory, preview: IconEye, edit: IconEdit,
  remove: IconTrash, add: IconPlus, copy: IconCopy, map: IconMap,
  route: IconRoute, locate: IconMapPin, settings: IconSettings, check: IconCheck,
  close: IconX, publish: IconCloudUpload, folder: IconFolder, undo: IconArrowBackUp,
  redo: IconArrowForwardUp, layers: IconStack2, properties: IconAdjustments,
  select: IconPointer, pan: IconHandStop, line: IconLine, curve: IconVectorBezier2,
  measure: IconRuler, boundary: IconPolygon, brush: IconBrush, scene: IconCube,
  list: IconListDetails, vehicle: IconRobot, search: IconSearch,
  fit: IconArrowsMaximize, activate: IconPlayerPlay, safety: IconShieldCheck
} as const;
defineProps<{ name: keyof typeof icons }>();
</script>

<template><component :is="icons[name]" class="action-icon" :size="14" :stroke-width="1.7" aria-hidden="true" focusable="false" /></template>

<style scoped>.action-icon{flex-shrink:0;vertical-align:middle}</style>
