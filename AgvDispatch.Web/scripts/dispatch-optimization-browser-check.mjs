// An isolated HTTP fixture and headless Chromium; no connection to现场5199, PLC or vehicles.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dist = resolve(project, 'AgvDispatch.Web/dist');
const evidence = resolve(project, '.codex-tmp', `dispatch-optimization-browser-${Date.now()}`);
mkdirSync(evidence, { recursive: true });
const binary = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary, 'Chromium browser required');
let options = { mode: 'Shadow', intervalMs: 3000, windowSize: 80, candidatesPerVehicle: 15, minimumCandidatesPerTask: 2,
  waitPriorityPerMinute: 1, maxWaitPriority: 30, deadlineHorizonMinutes: 15, maxDeadlinePriority: 30,
  minimumBatteryPercent: 20, nominalSpeedMetersPerSecond: 1, standbyBonusSeconds: 1, batteryRiskSeconds: 10,
  crossZoneSeconds: 10, returnCostWeight: 0.2, siteZones: { LM28: 'A区' }, stationDeadlineMinutes: { 'CNC-A': 30 } };
let revision = 'r1', unavailable = false, includeLayers = false, writes = [], child, ws, sequence = 0;
const pending = new Map(), errors = [];
const snapshot = () => ({ options, revision, capturedAt: new Date().toISOString(), cycleNumber: 7, durationMs: 3.7,
  waitingTaskCount: 300, windowTaskCount: 80, eligibleVehicleCount: 2, candidateCount: 3, routeCacheHits: 9, routeCacheMisses: 2,
  message: '影子推荐已计算；等待交通资源 TRAFFIC-LM28 容量释放。',
  layers: includeLayers ? [
    { key: 'queue', number: 1, name: 'PLC 呼叫队列', state: 'Ready', summary: '300 个呼叫已持久化，80 个进入窗口。', itemCount: 300 },
    { key: 'candidates', number: 2, name: '候选筛选', state: 'Ready', summary: '2 台车辆通过本轮硬约束。', itemCount: 2 },
    { key: 'matching', number: 3, name: '滚动全局匹配', state: options.mode === 'Global' ? 'Ready' : 'Observing', summary: '已计算2组全局推荐。', itemCount: 2 },
    { key: 'traffic', number: 4, name: '交通路权', state: 'Waiting', summary: 'TRAFFIC-LM28 容量不足，保留原占用等待释放。', itemCount: 2 },
    { key: 'safety', number: 5, name: '安全复核', state: 'Waiting', summary: '尚未取得本轮路权，未进入命令登记前复核。', itemCount: null },
    { key: 'execution', number: 6, name: '执行与恢复', state: 'Idle', summary: '本轮尚未登记车辆命令。', itemCount: 0 }
  ].map(layer => ({ ...layer, updatedAt: new Date().toISOString() })) : undefined,
  tasks: [
    { taskId: 'T-A', stationId: 'CNC-A', calledAt: new Date(Date.now() - 120000).toISOString(), basePriority: 50, effectivePriority: 52,
      waitMinutes: 2, recommendedVehicleId: 'AGV-01', greedyVehicleId: 'AGV-03', dispatchedVehicleId: null, reason: '保留唯一可执行车辆给 CNC-B。', blockedLayer: 'traffic',
      candidates: [{ vehicleId: 'AGV-01', travelSeconds: 8, totalCost: 7, eligible: true, reason: '待机加分 1 秒。' },
        { vehicleId: 'AGV-02', travelSeconds: 0, totalCost: 0, eligible: false, reason: '急停信号未解除。' },
        { vehicleId: 'AGV-03', travelSeconds: 5, totalCost: 5, eligible: true, reason: '空闲车辆。' }] },
    { taskId: 'T-B', stationId: 'CNC-B', calledAt: new Date(Date.now() - 60000).toISOString(), basePriority: 50, effectivePriority: 51,
      waitMinutes: 1, recommendedVehicleId: 'AGV-03', greedyVehicleId: null, dispatchedVehicleId: null, reason: '等待 LM28 路权。',
      candidates: [{ vehicleId: 'AGV-03', travelSeconds: 4, totalCost: 4, eligible: true, reason: '唯一合法车型。' }] }
  ] });
const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://127.0.0.1');
    const json = (code, value) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
    if (url.pathname === '/api/config') return json(200, { taskSubmissionEnabled: true });
    if (url.pathname === '/api/dispatch-optimization') return json(unavailable ? 503 : 200, unavailable ? { error: '模拟后台断开' } : snapshot());
    if (url.pathname === '/api/dispatch-optimization/options' && req.method === 'PUT') {
      let raw = ''; for await (const part of req) raw += part;
      const payload = JSON.parse(raw); writes.push(payload);
      if (payload.expectedRevision !== revision) return json(409, { error: '配置修订冲突' });
      options = payload.options; revision = 'r3'; return json(200, snapshot());
    }
    if (url.pathname.startsWith('/api/')) return json(503, { error: 'isolated fixture only' });
    const file = resolve(dist, '.' + (url.pathname === '/' ? '/index.html' : url.pathname));
    if (!file.startsWith(dist + sep) || !existsSync(file)) { res.writeHead(404); return res.end(); }
    res.writeHead(200, { 'Content-Type': file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html' });
    res.end(readFileSync(file));
  } catch (error) { errors.push(String(error)); res.writeHead(500); res.end('{}'); }
});
await new Promise(ok => server.listen(0, '127.0.0.1', ok));
const sleep = ms => new Promise(ok => setTimeout(ok, ms));
async function wait(fn, label) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) { if (await fn()) return; await sleep(100); }
  throw new Error(label);
}
function rpc(method, params = {}) {
  return new Promise((ok, no) => {
    const id = ++sequence, timeout = setTimeout(() => no(new Error(method + ' timeout')), 15000);
    pending.set(id, { ok: value => { clearTimeout(timeout); ok(value); }, no });
    ws.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await rpc('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
async function click(label) {
  await wait(() => evaluate(`(()=>{const b=[...document.querySelectorAll('.dispatch-optimization-page button')].find(b=>b.textContent.trim()===${JSON.stringify(label)});if(!b||b.disabled)return false;b.click();return true;})()`), label);
}
async function field(label, value) {
  await evaluate(`(()=>{const l=[...document.querySelectorAll('.assignment-config-grid label')].find(l=>l.textContent.trim().startsWith(${JSON.stringify(label)}));const e=l.querySelector('input,select');e.value=${JSON.stringify(value)};e.dispatchEvent(new Event(e.tagName==='SELECT'?'change':'input',{bubbles:true}));})()`);
}
const pageText = () => evaluate(`document.querySelector('.dispatch-optimization-page')?.textContent ?? ''`);
function check(label, value) { assert.ok(value, label); console.log('PASS ' + label); }
try {
  child = spawn(binary, ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${evidence}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  await wait(() => existsSync(resolve(evidence, 'DevToolsActivePort')), 'browser');
  const port = readFileSync(resolve(evidence, 'DevToolsActivePort'), 'utf8').split('\n')[0];
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  ws = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
  await new Promise(ok => ws.addEventListener('open', ok, { once: true }));
  ws.addEventListener('message', event => {
    const result = JSON.parse(event.data); if (!result.id) return;
    const handler = pending.get(result.id); if (!handler) return;
    pending.delete(result.id); result.error ? handler.no(new Error(JSON.stringify(result.error))) : handler.ok(result.result);
  });
  await rpc('Page.enable'); await rpc('Runtime.enable');
  await rpc('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1100, deviceScaleFactor: 1, mobile: false });
  await rpc('Page.addScriptToEvaluateOnNewDocument', { source: `window.WebSocket=class{constructor(){this.timer=setInterval(()=>this.onmessage?.({data:JSON.stringify({source:'backend',connected:true,receivedAt:new Date().toISOString(),tasks:{items:[],totalCount:0},status:{agvs:[],safety:{dispatchingEnabled:true,plcWritesAllowed:true,agvCommandsAllowed:true}},devices:{equipment:[]},resources:[],alarms:[]})}),100)}close(){clearInterval(this.timer)}};` });
  await rpc('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/#/dispatch-optimization` });
  await wait(async () => (await pageText()).includes('保留唯一可执行车辆'), 'diagnostics loaded');
  check('all six layers are unobserved before backend stage evidence arrives', await evaluate(`document.querySelectorAll('.pipeline-layer.unknown').length===6&&document.querySelectorAll('.pipeline-layer.ready').length===0`));
  includeLayers = true; await click('刷新状态');
  await wait(() => evaluate(`document.querySelector('.pipeline-layer[data-layer=traffic]').textContent.includes('TRAFFIC-LM28')`), 'pipeline evidence');
  check('pipeline separates matching recommendations, resource wait and idle execution', await evaluate(`document.querySelector('.pipeline-layer[data-layer=matching]').classList.contains('observing')&&document.querySelector('.pipeline-layer[data-layer=traffic]').classList.contains('waiting')&&document.querySelector('.pipeline-layer[data-layer=execution]').textContent.includes('当前空闲')`));
  check('task wait details identify the exact blocked layer', await evaluate(`document.querySelector('.assignment-queue .assignment-layer-tag').textContent.includes('第 4 层')&&document.querySelector('.pipeline-layer[data-layer=traffic]').classList.contains('focused')`));
  check('initial read shows shadow recommendations without sending any action', writes.length === 0 && (await pageText()).includes('与传统派车不同 2 项'));
  check('window leaves remaining persisted calls visible', (await pageText()).includes('窗口外还有 220 个任务'));
  check('hard exclusions do not appear as zero cost routes', await evaluate(`document.querySelector('.assignment-candidates tr.excluded')?.textContent.includes('急停信号未解除')&&[...document.querySelector('.assignment-candidates tr.excluded').children].slice(1,3).every(td=>td.textContent==='—')`));
  await field('待机车加分', '1.5');
  check('standby bonus cannot override more than one second of travel', await evaluate(`document.querySelector('.assignment-config button[type=submit]').disabled`));
  await field('待机车加分', '0.5'); await field('派车模式', 'Global');
  await sleep(5500);
  check('polling retains unsaved configuration and never enables global mode implicitly', writes.length === 0 && await evaluate(`document.querySelector('.assignment-config select').value==='Global'`));
  revision = 'r2'; options = { ...options, minimumBatteryPercent: 25 };
  await click('刷新状态');
  await wait(async () => (await pageText()).includes('后台配置修订已变化'), 'revision conflict');
  check('a competing save locks submission while preserving the local draft', await evaluate(`document.querySelector('.assignment-config select').value==='Global'&&document.querySelector('.assignment-config button[type=submit]').disabled`));
  await click('载入后台配置');
  check('reload explicitly accepts current backend parameters', await evaluate(`document.querySelector('.assignment-config select').value==='Shadow'&&document.querySelector('.assignment-config').textContent.includes('与后台已保存配置一致')`));
  await field('派车模式', 'Global'); await click('保存调度策略');
  await wait(() => writes.length === 1, 'explicit save');
  await wait(async () => (await pageText()).includes('已保存为「全局匹配」'), 'save result');
  check('explicit save carries the observed revision and exact settings', writes[0].expectedRevision === 'r2' && writes[0].options.mode === 'Global' && writes[0].options.minimumBatteryPercent === 25);
  check('saving other policy fields preserves explicit zones and station deadlines', writes[0].options.siteZones.LM28 === 'A区' && writes[0].options.stationDeadlineMinutes['CNC-A'] === 30 && writes[0].options.returnCostWeight === 0.2);
  const shot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  writeFileSync(resolve(evidence, 'dispatch-optimization-desktop.png'), Buffer.from(shot.data, 'base64'));
  await evaluate(`document.querySelector('.dispatch-optimization-page').scrollTop=document.querySelector('.dispatch-optimization-page').scrollHeight`);
  const configShot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  writeFileSync(resolve(evidence, 'dispatch-optimization-config.png'), Buffer.from(configShot.data, 'base64'));
  await rpc('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: false });
  await evaluate(`document.querySelector('.dispatch-optimization-page').scrollTop=0`);
  await sleep(100);
  const narrow = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  writeFileSync(resolve(evidence, 'dispatch-optimization-narrow.png'), Buffer.from(narrow.data, 'base64'));
  check('narrow page keeps tables internally scrollable', await evaluate(`document.querySelector('.dispatch-optimization-page').scrollWidth<=document.querySelector('.dispatch-optimization-page').clientWidth+1`));
  unavailable = true; await click('刷新状态');
  await wait(async () => (await pageText()).includes('后台读取失败'), 'backend failure');
  await field('派车模式', 'Shadow');
  check('lost backend freshness disables writes while retaining the previous results', await evaluate(`document.querySelector('.assignment-config button[type=submit]').disabled&&document.querySelector('.assignment-queue').textContent.includes('CNC-A')`));
  check('a lost connection cannot leave any stage falsely ready or running', await evaluate(`document.querySelectorAll('.pipeline-layer.stale').length===6&&document.querySelectorAll('.pipeline-layer.ready,.pipeline-layer.running').length===0`));
  assert.deepEqual(errors, []);
  console.log('PASS dispatch optimization browser; isolated fixture only. Evidence: ' + evidence);
} finally {
  ws?.close(); child?.kill(); server.closeAllConnections(); await new Promise(ok => server.close(ok));
}
