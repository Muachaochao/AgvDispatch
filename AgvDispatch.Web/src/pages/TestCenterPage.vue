<script setup lang="ts">
import { computed } from 'vue';
import { useRouter } from 'vue-router';
import { IconArrowLeft, IconCircleCheck, IconListDetails, IconRoute, IconFlask } from '@tabler/icons-vue';
import { useDispatchStore } from '../stores/dispatch';

const props = defineProps<{ embedded?: boolean }>();
const store = useDispatchStore();
const router = useRouter();
const observation = computed(() => !store.snapshot ? '尚未取得后台观察' : store.fresh ? '后台观察有效' : '后台观察已过期');
const tools = [
  { path: '/dispatch-pipeline', title: '六层调度监控', text: '核对呼叫、派车、路权、安全复核和执行证据。', icon: IconListDetails },
  { path: '/navigation-test', title: '单车导航调试', text: '核对车辆、地图、单目标导航与顶升下降动作。', icon: IconRoute },
  { path: '/commissioning', title: '运行条件与通信诊断', text: '检查地图绑定、车辆状态、九步参数和通信门禁。', icon: IconCircleCheck }
] as const;
</script>

<template>
  <section class="management-page test-center-page" :class="{ embedded: props.embedded }">
    <header v-if="!props.embedded" class="management-title">
      <h2><IconFlask :size="23" />工程测试中心</h2>
      <span :class="store.fresh ? 'green-text' : 'amber-text'">{{ observation }}</span>
      <button @click="router.push('/tasks')"><IconListDetails :size="15" />业务任务</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>

    <section class="test-center-intro">
      <div>
        <h3>PLC呼叫与六层调度验证</h3>
        <p>统一使用正常生产模式。测试人员只在PLC实验台发呼叫，Web自动建单并由六层监控保留派车证据。</p>
      </div>
      <span :class="store.submissionEnabled ? 'test-capability ready' : 'test-capability'">{{ store.submissionEnabled ? '后端发送门已启用' : '后端发送门未启用' }}</span>
    </section>

    <nav class="test-tool-grid" aria-label="工程测试工具">
      <button v-for="tool in tools" :key="tool.path" @click="router.push(tool.path)">
        <component :is="tool.icon" :size="20" />
        <span><b>{{ tool.title }}</b><small>{{ tool.text }}</small></span>
      </button>
    </nav>

    <section class="test-stage" aria-labelledby="two-car-test-heading">
      <header>
        <div>
          <h3 id="two-car-test-heading">PLC呼叫触发流程</h3>
          <p>不创建专用测试会话，不切换额外测试模式，也不读取旧多车实验记录。</p>
        </div>
        <button @click="router.push('/dispatch-pipeline')"><IconListDetails :size="15" />打开六层监控</button>
      </header>
      <ol class="two-car-steps">
        <li><b>准备</b><span>确认AGV-01、AGV-02在线停稳且空载，AGV-03保持停用。</span></li>
        <li><b>发起呼叫</b><span>在PLC人工联调台保持自动闭环模式，依次或同时置位CNC-001至CNC-004的满箱呼叫。</span></li>
        <li><b>核对派车</b><span>两车只执行各自任务；其余任务保持等待，不重复分配车辆。</span></li>
        <li><b>查看证据</b><span>在六层调度监控核对候选、匹配、路权、安全复核和原命令。</span></li>
      </ol>
      <div class="test-stage-actions">
        <button @click="router.push('/dispatch-pipeline')"><IconListDetails :size="15" />打开六层监控</button>
        <button @click="router.push('/tasks')"><IconListDetails :size="15" />查看业务任务</button>
      </div>
    </section>
  </section>
</template>

<style scoped>
.test-center-page { max-width: 1500px; }
.test-center-page.embedded { max-width: none; margin: 0; }
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
.two-car-steps { display:grid; gap:10px; margin:18px 0; padding:0; list-style:none; }
.two-car-steps li { display:grid; grid-template-columns:96px minmax(0,1fr); gap:14px; padding:11px 13px; border-left:3px solid #2e8f88; background:#101f31; }
.two-car-steps b { color:#7ce0cb; }
.two-car-steps span { color:#b8c9d8; line-height:1.65; }
.test-stage-actions { display:flex; gap:10px; flex-wrap:wrap; }
@media (max-width:1050px) { .test-tool-grid { grid-template-columns:repeat(2,minmax(0,1fr)); } }
@media (max-width:700px) { .test-center-intro,.test-stage > header { align-items:stretch; flex-direction:column; } .test-tool-grid { grid-template-columns:1fr; } }
</style>
