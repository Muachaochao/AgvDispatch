<script setup lang="ts">
import { computed } from 'vue';
import { useRouter } from 'vue-router';
import { IconArrowLeft, IconCircleCheck, IconListDetails, IconMap, IconRoute, IconFlask } from '@tabler/icons-vue';
import FleetTestPanel from '../components/FleetTestPanel.vue';
import { useDispatchStore } from '../stores/dispatch';

const store = useDispatchStore();
const router = useRouter();
const observation = computed(() => !store.snapshot ? '尚未取得后台观察' : store.fresh ? '后台观察有效' : '后台观察已过期');
const tools = [
  { path: '/plc-cycle-test', title: 'PLC 空载联调', text: '按单轮、原任务、升降及 PLC 条件核对空载流程。', icon: IconRoute },
  { path: '/navigation-test', title: '单车导航调试', text: '核对车辆、地图、单目标导航与顶升下降动作。', icon: IconRoute },
  { path: '/commissioning', title: '运行条件与通信诊断', text: '检查地图绑定、车辆状态、九步参数和通信门禁。', icon: IconCircleCheck },
  { path: '/planning', title: '地图与区域编辑', text: '核对车站、道路、展示区域及共享资源。', icon: IconMap }
] as const;
</script>

<template>
  <section class="management-page test-center-page">
    <header class="management-title">
      <h2><IconFlask :size="23" />工程测试中心</h2>
      <span :class="store.fresh ? 'green-text' : 'amber-text'">{{ observation }}</span>
      <button @click="router.push('/tasks')"><IconListDetails :size="15" />业务任务</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>

    <section class="test-center-intro">
      <div>
        <h3>现场测试入口</h3>
        <p>测试操作集中在本页，业务任务页只保留任务查询和创建。以下按钮仍连接当前后台；发送车辆动作前，现场人员和设备条件必须已经核对。</p>
      </div>
      <span :class="store.submissionEnabled ? 'test-capability ready' : 'test-capability'">{{ store.submissionEnabled ? '后端发送门已启用' : '后端发送门未启用' }}</span>
    </section>

    <nav class="test-tool-grid" aria-label="工程测试工具">
      <button v-for="tool in tools" :key="tool.path" @click="router.push(tool.path)">
        <component :is="tool.icon" :size="20" />
        <span><b>{{ tool.title }}</b><small>{{ tool.text }}</small></span>
      </button>
    </nav>

    <section class="test-stage" aria-labelledby="fleet-test-heading">
      <header>
        <div>
          <h3 id="fleet-test-heading">多车多工位测试</h3>
          <p>从当前工程配置选择参与车辆和工位，验证错峰、同时呼叫、资源竞争和前后交接；查询、停止及恢复均沿用原运行记录。</p>
        </div>
        <button @click="router.push('/tasks')"><IconListDetails :size="15" />查看原任务</button>
      </header>
      <FleetTestPanel/>
    </section>
  </section>
</template>

<style scoped>
.test-center-page { max-width: 1500px; }
.test-center-intro { display:flex; align-items:center; justify-content:space-between; gap:24px; margin:16px 0; padding:18px 20px; border:1px solid #2c435d; border-radius:8px; background:linear-gradient(120deg,#101d32,#122b3c); }
.test-center-intro h3,.test-stage h3 { margin:0 0 6px; color:var(--text-primary,#d8e8f4); }
.test-center-intro p,.test-stage p { margin:0; color:#9cb3c7; line-height:1.7; }
.test-capability { flex:none; padding:7px 11px; border:1px solid #7e6742; border-radius:999px; color:#e4bc72; font-size:12px; }
.test-capability.ready { border-color:#277f68; color:#62dab5; }
.test-tool-grid { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:10px; margin:16px 0; }
.test-tool-grid button { display:flex; align-items:flex-start; gap:11px; min-height:94px; padding:14px; text-align:left; color:#79d9e9; }
.test-tool-grid button span { display:grid; gap:5px; }
.test-tool-grid b { color:#dceaf5; }
.test-tool-grid small { color:#91a9bd; line-height:1.5; }
.test-stage { border:1px solid #2c435d; border-radius:8px; padding:18px; background:#0d1829; }
.test-stage > header { display:flex; align-items:start; justify-content:space-between; gap:18px; }
@media (max-width:1050px) { .test-tool-grid { grid-template-columns:repeat(2,minmax(0,1fr)); } }
@media (max-width:700px) { .test-center-intro,.test-stage > header { align-items:stretch; flex-direction:column; } .test-tool-grid { grid-template-columns:1fr; } }
</style>
