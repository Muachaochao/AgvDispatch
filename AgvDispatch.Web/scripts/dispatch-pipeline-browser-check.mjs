// Isolated HTTP fixtures only. This script never connects to daily 5199, a PLC or an AGV.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dist = resolve(project, 'AgvDispatch.Web/dist');
const evidence = resolve(project, '.codex-tmp', `dispatch-pipeline-browser-${Date.now()}`);
mkdirSync(evidence, { recursive: true });
const binary = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary, 'A Chromium browser is required');
let generation = 1, unavailable = false, disabled = false, missingLayers = false, malformedLayers = false, futureCapture = false;
let child, ws, sequence = 0, checks = 0;
const pending = new Map(), errors = [], writes = [], reads = [];
const names = ['PLC 呼叫队列', '候选筛选', '滚动全局匹配', '交通路权', '安全复核', '执行与恢复'];
const keys = ['queue', 'candidates', 'matching', 'traffic', 'safety', 'execution'];
const fact = (label, value) => ({ label, value });
function snapshot() {
  const stamp = new Date().toISOString();
  const prior = new Date(Date.now() - 60000).toISOString();
  const row = (id, taskId, state, title, facts, extra = {}) => ({ id, taskId, state, title, facts, reason: title, updatedAt: stamp, ...extra });
  const layers = [
    { state: disabled ? 'Disabled' : 'Ready', summary: disabled ? 'PLC 自动接单未启用，原任务保留。' : `原队列第${generation}次观察`,
      metrics: [{ label: '持久化等待', value: '302', unit: '单' }, { label: '本轮窗口', value: '80', unit: '单' }],
      checks: [{ key: 'durable', label: '原呼叫与优先级', state: 'Pass', actual: '优先级50；等待补偿2', expected: '沿用原任务呼叫时间', reason: '已持久化，不重建任务。', observedAt: stamp }],
      records: [
        row('CALL-A', 'T-A', 'Waiting', `CNC-A-OBS-${generation}`, [fact('基础优先级', '50'), fact('原始呼叫', '2026-11-03 08:00:00'), fact('有效优先级', '52')], { stationId: 'CNC-A', link: '/tasks/T-A' }),
        row('CALL-B', 'T-B', 'Ready', 'CNC-B 已进入窗口', [fact('基础优先级', '20')], { stationId: 'CNC-B', link: '/tasks/T-B' })
      ], totalCount: 302, truncated: true, note: '仅展示前2单样本，其余原任务仍保存在持久化队列。' },
    { state: disabled ? 'Disabled' : 'Waiting', summary: '健康车辆与硬约束候选分别核对。',
      metrics: [{ label: '可用车辆', value: '1', unit: '台' }, { label: '排除车辆', value: '1', unit: '台' }],
      checks: [{ key: 'telemetry', label: '健康与定位', state: 'Waiting', actual: 'AGV-02 急停', expected: '在线、健康且定位有效', reason: '急停不能由路线成本覆盖。', observedAt: stamp }],
      records: [row('CAR-01', 'T-A', 'Eligible', 'AGV-01 可参与匹配', [fact('电量', '86%'), fact('定位质量', 'Good'), fact('站点', 'LM10')], { vehicleId: 'AGV-01', stationId: 'CNC-A' }),
        row('CAR-02', 'T-A', 'Excluded', 'AGV-02 急停未解除', [fact('电量', '72%'), fact('急停', 'true')], { vehicleId: 'AGV-02', stationId: 'CNC-A' })], totalCount: 2 },
    { state: disabled ? 'Disabled' : 'Observing', summary: '推荐组合尚未登记命令。',
      metrics: [{ label: '推荐', value: '1', unit: '单' }, { label: '已登记', value: '0', unit: '单' }],
      checks: [{ key: 'assignment', label: '匹配与实际派车', state: 'NotChecked', actual: '推荐 AGV-01；尚未登记', expected: '取得路权并通过安全层后登记', reason: '推荐不证明车端已经执行。', observedAt: null }],
      records: [row('MATCH-A', 'T-A', 'Recommended', 'CNC-A 全局推荐 AGV-01', [fact('全局推荐', 'AGV-01'), fact('传统选择', 'AGV-03'), fact('实际登记', '未登记'), fact('行驶估算', '8秒')], { vehicleId: 'AGV-01', stationId: 'CNC-A' })], totalCount: 1 },
    { state: 'Waiting', summary: 'TRAFFIC-LM28 容量已满，等待原占用交接。',
      metrics: [{ label: '资源容量', value: '1' }, { label: '实际占用', value: '1' }],
      checks: [{ key: 'capacity', label: '公共资源容量', state: 'Waiting', actual: '占用1 / 容量1', expected: '整条路径可申请', reason: '原车未释放，后到请求不能插队。', observedAt: stamp }],
      records: [row('WAIT-A', 'T-A', 'Waiting', '等待 TRAFFIC-LM28', [fact('请求步骤', 'STEP-A'), fact('队列优先级', '52'), fact('阻塞任务', 'T-OWNER')], { vehicleId: 'AGV-01', stationId: 'CNC-A', resourceId: 'TRAFFIC-LM28', link: '/resource-review' }),
        row('LEASE-OLD', 'T-OWNER', 'Acquired', '原车辆持有 TRAFFIC-LM28', [fact('OwnerStepId', 'STEP-OLD'), fact('实际占用', '1'), fact('心跳过期', '不释放物理路权')], { vehicleId: 'AGV-03', resourceId: 'TRAFFIC-LM28', link: '/resource-review' })], totalCount: 2 },
    { state: disabled ? 'Disabled' : 'Waiting', summary: '本任务尚未进行发送前安全复核。',
      metrics: [{ label: '未复核任务', value: '1', unit: '单' }],
      checks: [{ key: 'map', label: '地图执行门禁', state: 'Pass', actual: 'Running / MAP-1 / V1', expected: '当前地图屏障放行', reason: '门禁放行不能替代其他复核。', observedAt: stamp },
        { key: 'plc', label: '当前 PLC 许可', state: 'NotChecked', actual: '尚未进入安全层', expected: '真实当前阶段PLC条件', reason: '只读页面不运行绑定或模拟安全校验。', observedAt: null },
        { key: 'old-vehicle', label: '车辆采样', state: 'Stale', actual: '60秒前', expected: '有效实时遥测', reason: '旧采样不能显示为通过。', observedAt: prior }],
      records: [row('CHECK-A', 'T-A', 'NotChecked', '尚未复核 PLC 与当前步骤路权', [fact('步骤', 'STEP-A'), fact('地图版本', 'V1')], { vehicleId: 'AGV-01', stationId: 'CNC-A' })], totalCount: 1 },
    { state: disabled ? 'Disabled' : 'Running', summary: '原命令按原编号核对，不能重建业务任务。',
      metrics: [{ label: '待恢复原命令', value: '1', unit: '条' }],
      checks: [{ key: 'stable-id', label: '原步骤幂等身份', state: 'Pass', actual: 'STEP-A / VENDOR-ORIGINAL', expected: '重试沿用原编号', reason: '先查询厂家原编号，再按原命令处理。', observedAt: stamp }],
      records: [row('OUTBOX-ORIGINAL', 'T-A', 'RetryScheduled', '原命令等待核对厂家反馈', [fact('原步骤编号', 'STEP-A'), fact('发送尝试', '2'), fact('厂家编号', 'VENDOR-ORIGINAL'), fact('命令ID', 'OUTBOX-ORIGINAL')], { vehicleId: 'AGV-01', link: '/recovery' })], totalCount: 1 }
  ].map((layer, index) => ({ key: keys[index], number: index + 1, name: names[index], observedAt: stamp, checks: [], records: [], metrics: [], totalCount: 0, truncated: false, note: '', ...layer }));
  if (malformedLayers) {
    for (let index = 0; index < layers.length; index++) {
      layers[index].state = 'Ready';
      layers[index].observedAt = index % 2 ? new Date(Date.now() + 60000).toISOString() : prior;
      layers[index].checks = [{ key: 'invalid', label: '无有效时间的判断', state: 'Pass', actual: '不可信', expected: '有效观察时间', reason: '不能显示为已通过。', observedAt: null }];
    }
  }
  return { capturedAt: futureCapture ? new Date(Date.now() + 60000).toISOString() : stamp, cycleNumber: generation + 10, optimizationCapturedAt: disabled ? null : stamp,
    mode: disabled ? 'Shadow' : 'Global', summary: disabled ? '业务派车与命令发送均未启用，当前不会自动发车。' : `现场观察批次 OBS-${generation}`,
    gates: [{ key: 'dispatch', label: '业务派车', enabled: !disabled, detail: disabled ? '未启用' : '已配置启用' },
      { key: 'creation', label: 'PLC自动接单', enabled: !disabled, detail: '原呼叫持久化后应答' },
      { key: 'outbox', label: '车辆命令发送', enabled: !disabled, detail: '保留原命令与步骤编号' },
      { key: 'unknown', label: '未知许可', enabled: null, detail: '未取得观察，不推测已允许' }], layers: missingLayers ? [] : layers };
}
const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://127.0.0.1');
    if (req.method !== 'GET') writes.push({ method: req.method, path: url.pathname });
    const json = (code, value) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
    if (url.pathname === '/api/config') return json(200, { taskSubmissionEnabled: false });
    if (url.pathname === '/api/dispatch-pipeline') { reads.push({ time: Date.now(), generation }); return json(unavailable ? 503 : 200, unavailable ? { error: '隔离夹具后台不可用' } : snapshot()); }
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
    pending.set(id, { ok: value => { clearTimeout(timeout); ok(value); }, no: error => { clearTimeout(timeout); no(error); } });
    ws.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await rpc('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
async function click(testId) {
  await wait(() => evaluate(`(()=>{const e=document.querySelector('[data-testid=${testId}]');if(!e||e.disabled)return false;e.click();return true;})()`), testId);
}
async function selectLayer(key) {
  await evaluate(`document.querySelector('[data-layer=${key}]').click()`);
  await wait(() => evaluate(`document.querySelector('[data-layer=${key}]').getAttribute('aria-pressed')==='true'`), 'select ' + key);
}
const pageText = () => evaluate(`document.querySelector('.dispatch-pipeline-page')?.textContent ?? ''`);
const detailText = () => evaluate(`document.querySelector('[data-testid=pipeline-detail]')?.textContent ?? ''`);
async function search(value) {
  await evaluate(`(()=>{const e=document.querySelector('[data-testid=pipeline-search]');e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('input',{bubbles:true}));})()`);
}
function check(label, value) { assert.ok(value, label); checks++; console.log('PASS ' + label); }
async function refresh() {
  const before = reads.length;
  await click('pipeline-refresh');
  await wait(() => reads.length > before, 'new read');
  await sleep(150);
}
async function screenshot(name, section) {
  if (section) await evaluate(`(()=>{const p=document.querySelector('.dispatch-pipeline-page'),e=document.querySelector(${JSON.stringify(section)});p.scrollTop+=e.getBoundingClientRect().top-p.getBoundingClientRect().top-12;})()`);
  else await evaluate("document.querySelector('.dispatch-pipeline-page').scrollTop=0");
  await sleep(100);
  const image = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  writeFileSync(resolve(evidence, name + '.png'), Buffer.from(image.data, 'base64'));
}
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
  await rpc('Page.addScriptToEvaluateOnNewDocument', { source: `window.WebSocket=class{constructor(){this.timer=setInterval(()=>this.onmessage?.({data:JSON.stringify({source:'backend',connected:true,receivedAt:new Date().toISOString(),tasks:{items:[],totalCount:0},status:{agvs:[],safety:{dispatchingEnabled:false,plcWritesAllowed:false,agvCommandsAllowed:false}},devices:{equipment:[]},resources:[],alarms:[]})}),100)}close(){clearInterval(this.timer)}};` });
  await rpc('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/#/dispatch-pipeline?layer=traffic` });
  await wait(async () => (await detailText()).includes('TRAFFIC-LM28'), 'inspection loaded');
  check('deep link opens the matching layer and keeps fixed six-layer order', await evaluate(`document.querySelector('[data-layer=traffic]').getAttribute('aria-pressed')==='true'&&[...document.querySelectorAll('[data-layer]')].map(e=>e.dataset.layer).join(',')==='${keys.join(',')}'`));
  check('resource detail contains actual capacity, request owner and retained physical lease', (await detailText()).includes('STEP-OLD') && (await detailText()).includes('占用1 / 容量1') && (await detailText()).includes('T-OWNER'));
  await selectLayer('queue');
  check('queue detail preserves original task identity, priority and call time with bounded sample notice', (await detailText()).includes('T-A') && (await detailText()).includes('2026-11-03 08:00:00') && (await detailText()).includes('302') && (await detailText()).includes('样本'));
  await selectLayer('candidates');
  check('candidate details expose battery and hard exclusion reason', (await detailText()).includes('AGV-01') && (await detailText()).includes('86%') && (await detailText()).includes('急停未解除'));
  await selectLayer('matching');
  check('matching recommendation, traditional choice and actual registration remain distinct', (await detailText()).includes('AGV-03') && (await detailText()).includes('AGV-01') && (await detailText()).includes('未登记'));
  await selectLayer('safety');
  check('unperformed PLC review and stale telemetry never read as approved', await evaluate(`(()=>{const rows=[...document.querySelectorAll('[data-testid=pipeline-check]')];return rows.some(r=>r.textContent.includes('当前 PLC 许可')&&r.textContent.includes('未复核'))&&rows.some(r=>r.textContent.includes('车辆采样')&&r.textContent.includes('过期'));})()`));
  await evaluate(`document.querySelector('[data-layer=execution]').focus()`);
  await rpc('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r', unmodifiedText: '\r' });
  await rpc('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
  await wait(() => evaluate("document.querySelector('[data-layer=execution]').getAttribute('aria-pressed')==='true'"), 'keyboard layer navigation');
  check('layer navigation supports keyboard activation', await evaluate(`document.querySelector('[data-layer=execution]').getAttribute('aria-pressed')==='true'`));
  await selectLayer('execution');
  check('recovery detail exposes original command, step, attempts and vendor identity', (await detailText()).includes('OUTBOX-ORIGINAL') && (await detailText()).includes('STEP-A') && (await detailText()).includes('VENDOR-ORIGINAL') && (await detailText()).includes('发送尝试'));
  await screenshot('dispatch-pipeline-execution-detail', '.pipeline-records-section');
  await selectLayer('traffic'); await search('AGV-01');
  check('entity search narrows records without losing layer context', await evaluate(`document.querySelectorAll('[data-testid=pipeline-record]').length===1&&document.querySelector('[data-testid=pipeline-record]').textContent.includes('T-A')`));
  await search(''); await click('pipeline-only-issues');
  check('issue filter shows waiting request while hiding acquired lease', await evaluate(`document.querySelectorAll('[data-testid=pipeline-record]').length===1&&document.querySelector('[data-testid=pipeline-record]').textContent.includes('T-A')`));
  await click('pipeline-only-issues');
  await screenshot('dispatch-pipeline-desktop');
  await screenshot('dispatch-pipeline-records', '.pipeline-records-section');
  await selectLayer('queue');
  await click('pipeline-freeze');
  generation = 2; await refresh();
  check('frozen observation retains original data while latest read continues', (await detailText()).includes('CNC-A-OBS-1') && !(await detailText()).includes('CNC-A-OBS-2') && await evaluate(`document.querySelector('.dispatch-pipeline-page').classList.contains('is-historical')`));
  check('frozen data explicitly identifies historical evidence rather than current execution', (await pageText()).includes('历史') && (await pageText()).includes('非当前'));
  const downloads = resolve(evidence, 'downloads'); mkdirSync(downloads, { recursive: true });
  await rpc('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloads });
  await click('pipeline-export');
  await wait(() => readdirSync(downloads).some(name => name.endsWith('.json')), 'frozen observation export');
  const exported = readFileSync(resolve(downloads, readdirSync(downloads).find(name => name.endsWith('.json'))), 'utf8');
  JSON.parse(exported);
  check('read-only export preserves the displayed frozen evidence rather than unseen latest data', exported.includes('CNC-A-OBS-1') && !exported.includes('CNC-A-OBS-2'));
  await screenshot('dispatch-pipeline-frozen');
  unavailable = true; await refresh();
  check('read failure retains frozen evidence and independently warns about current connection', (await detailText()).includes('CNC-A-OBS-1') && (await pageText()).includes('隔离夹具后台不可用') && (await pageText()).includes('历史'));
  unavailable = false; await click('pipeline-resume'); await refresh();
  await wait(async () => (await detailText()).includes('CNC-A-OBS-2'), 'resume latest');
  check('resuming explicitly returns to latest live observation', await evaluate(`!document.querySelector('.dispatch-pipeline-page').classList.contains('is-historical')`));
  const historyValues = await evaluate(`Array.from(document.querySelector('[data-testid=pipeline-history]').options).map(o=>o.value).filter(Boolean)`);
  check('successful observations are selectable within this page session', historyValues.length >= 2 && (await pageText()).includes('本页'));
  await evaluate(`(()=>{const e=document.querySelector('[data-testid=pipeline-history]');e.value=${JSON.stringify(historyValues.at(-1))};e.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  check('selecting old observation changes actual records and marks history', (await detailText()).includes('CNC-A-OBS-1') && await evaluate(`document.querySelector('.dispatch-pipeline-page').classList.contains('is-historical')`));
  await click('pipeline-resume'); disabled = true; await refresh();
  check('disabled runtime explains original gates and does not imply shadow matching is running', (await pageText()).includes('不会自动发车') && (await pageText()).includes('未启用') && (await pageText()).includes('影子'));
  missingLayers = true; await refresh();
  check('old backend without per-layer details yields six unknown observations', await evaluate("document.querySelectorAll('[data-layer]').length===6&&[...document.querySelectorAll('[data-layer]')].every(e=>!['ready','running','pass'].some(c=>e.classList.contains(c)))"));
  missingLayers = false; disabled = false; malformedLayers = true; await refresh();
  check('future or expired stage evidence cannot be displayed as current readiness', await evaluate("Array.from(document.querySelectorAll('[data-layer]')).every(e=>!['ready','running','pass'].some(c=>e.classList.contains(c)))"));
  await selectLayer('safety');
  check('a Pass check without observation time stays unconfirmed', await evaluate("Array.from(document.querySelectorAll('[data-testid=pipeline-check] .pipeline-status-badge')).every(e=>!['ready','running','pass'].some(c=>e.classList.contains(c)))"));
  malformedLayers = false; await refresh();
  futureCapture = true; await refresh();
  check('future snapshot capture is rejected instead of becoming a historical pass', (await pageText()).includes('采集时间无效') && await evaluate("Array.from(document.querySelectorAll('[data-layer]')).every(e=>!['ready','running','pass'].some(c=>e.classList.contains(c)))"));
  futureCapture = false; await refresh();
  unavailable = true; await refresh();
  check('live connection failure cannot leave ready layer or passed check styling', await evaluate("!document.querySelector('.dispatch-pipeline-page').classList.contains('is-historical')&&[...document.querySelectorAll('[data-layer],[data-testid=pipeline-check] .pipeline-status-badge')].every(e=>!['ready','running','pass'].some(c=>e.classList.contains(c)))"));
  unavailable = false; await refresh();
  await rpc('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: false });
  await evaluate(`document.querySelector('.dispatch-pipeline-page').scrollTop=0`); await sleep(150);
  await screenshot('dispatch-pipeline-narrow');
  await screenshot('dispatch-pipeline-narrow-detail', '[data-testid=pipeline-detail]');
  check('narrow layout contains horizontal overflow within record tables', await evaluate(`document.querySelector('.dispatch-pipeline-page').scrollWidth<=document.querySelector('.dispatch-pipeline-page').clientWidth+1`));
  check('inspection interactions issued no command, policy update or other write request', writes.length === 0);
  assert.deepEqual(errors, []);
  writeFileSync(resolve(evidence, 'result.json'), JSON.stringify({ checks, reads: reads.length, writes, errors }, null, 2));
  console.log(`PASS ${checks} six-layer inspection browser checks; isolated fixture only. Evidence: ${evidence}`);
} finally {
  ws?.close(); child?.kill(); server.closeAllConnections(); await new Promise(ok => server.close(ok));
}
