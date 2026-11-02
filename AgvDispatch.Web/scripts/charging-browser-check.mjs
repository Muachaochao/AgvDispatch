// Isolated mock HTTP + Chromium. Never connects to 5199, an AGV or a charger.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dist = resolve(project, 'AgvDispatch.Web/dist');
const evidence = resolve(project, '.codex-tmp', `charging-browser-${Date.now()}`);
mkdirSync(evidence, { recursive: true });
const binary = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary, 'Chromium browser required');
const site = { id: 'CHARGE-A', chargingSiteId: 'LM52', exitSiteId: 'LM51', resourceId: 'BAY-A',
  controlMode: 'RobokitScript', commissioned: true, allowedVehicleIds: ['AGV-01', 'AGV-02'] };
let sessions = [], allowTimedTests = false, acceptRequest = true, staleStation = false;
let child, ws, sequence = 0;
const requests = [], errors = [], calls = [], pending = new Map();
const sleep = ms => new Promise(ok => setTimeout(ok, ms));
const snapshot = () => {
  const stamp = new Date().toISOString();
  return { enabled: true, options: { enabled: true, startBatteryPercent: 25, criticalBatteryPercent: 15,
    targetBatteryPercent: 80, stateMaxAgeSeconds: 5, allowTimedTests, sites: [site] },
    sessions: sessions.map(row => row.workflowStatus ? { ...row, updatedAt: stamp,
      workflowStatus: { ...row.workflowStatus, updatedAt: stamp,
        battery: { ...row.workflowStatus.battery, observedAt: stamp },
        station: { ...row.workflowStatus.station, observedAt: new Date(Date.now() - (staleStation ? 10000 : 0)).toISOString() } } } : row),
    capturedAt: stamp };
};
const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://127.0.0.1'); calls.push(url.pathname);
    const json = (code, data) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); };
    if (url.pathname === '/api/config') return json(200, { taskSubmissionEnabled: true });
    if (url.pathname === '/api/charging') return json(200, snapshot());
    if (url.pathname === '/api/charging/request') {
      let raw = ''; for await (const part of req) raw += part;
      const intent = JSON.parse(raw); requests.push(intent);
      if (acceptRequest) sessions.push({ sessionId: 'mock-' + requests.length, vehicleId: intent.vehicleId,
        phase: 'WaitingForBusiness', message: '等待原箱体循环及设备握手安全结束', updatedAt: new Date().toISOString(),
        requestId: intent.requestId, requiredPositiveSeconds: intent.requiredPositiveSeconds,
        requestedReturnSiteId: intent.returnSiteId, site });
      return json(504, { detail: 'isolated request lost reply' });
    }
    if (url.pathname.startsWith('/api/')) return json(503, { error: 'isolated fixture only' });
    const file = resolve(dist, '.' + (url.pathname === '/' ? '/index.html' : url.pathname));
    if (!file.startsWith(dist + sep) || !existsSync(file)) { res.writeHead(404); return res.end(); }
    res.writeHead(200, { 'Content-Type': file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html' });
    res.end(readFileSync(file));
  } catch (e) { errors.push(String(e)); res.writeHead(500); res.end('{}'); }
});
await new Promise(ok => server.listen(0, '127.0.0.1', ok));
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
async function click(text) {
  await wait(() => evaluate(`(()=>{const b=[...document.querySelectorAll('.charging-page button')].find(b=>b.textContent.trim()===${JSON.stringify(text)});if(!b||b.disabled)return false;b.click();return true;})()`), 'button ' + text);
}
async function set(selector, value, event = 'input') {
  await evaluate(`(()=>{const e=document.querySelector(${JSON.stringify(selector)});e.value=${JSON.stringify(value)};e.dispatchEvent(new Event(${JSON.stringify(event)},{bubbles:true}));})()`);
}
function check(label, value) { assert.ok(value, label); console.log('PASS ' + label); }
try {
  child = spawn(binary, ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${evidence}`, 'about:blank'],
    { windowsHide: true, stdio: 'ignore' });
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
  await rpc('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/#/charging` });
  await wait(() => evaluate(`Boolean(document.querySelector('.charge-request-form'))`), 'charging form');
  check('timed test is disabled until explicitly configured', await evaluate(`document.querySelector('.charge-request-form option[value=test]').disabled`));
  check('configuration alone does not display live charger communication', await evaluate(`document.querySelector('.charge-monitoring').textContent.includes('等待桩端反馈')`));
  allowTimedTests = true; await click('刷新状态');
  await wait(() => evaluate(`!document.querySelector('.charge-request-form option[value=test]').disabled`), 'timed test enabled');
  await set('.charge-request-form input[list]', 'AGV-01');
  await set('.charge-request-form select', 'test', 'change');
  await set('.charge-request-form label:nth-of-type(3) input', 'LM44');
  await click('申请充电');
  await wait(() => evaluate(`document.querySelector('.charging-page').textContent.includes('已查询到原充电申请')`), 'accepted original reconciled');
  check('a lost reply queries the original request and shows business handoff waiting', requests.length === 1 &&
    requests[0].requiredPositiveSeconds === 60 && requests[0].returnSiteId === 'LM44' &&
    await evaluate(`!sessionStorage.getItem('charging-request-intent')&&document.querySelector('.charging-page').textContent.includes('等待业务安全结束')`));
  sessions = []; acceptRequest = false;
  await click('刷新状态'); await click('申请充电');
  await wait(() => evaluate(`Boolean(document.querySelector('.charge-request-pending'))`), 'unresolved original retained');
  const unresolved = requests[1];
  await sleep(5500);
  check('polling never automatically resends an unresolved charging request', requests.length === 2);
  await rpc('Page.reload');
  await wait(() => evaluate(`document.querySelector('.charge-request-pending')?.textContent.includes(${JSON.stringify(unresolved.requestId)})`), 'unresolved original survives reload');
  check('reload locks original selections and never starts a replacement request', requests.length === 2 &&
    await evaluate(`[...document.querySelectorAll('.charge-request-form input,.charge-request-form select')].every(e=>e.disabled)`));
  await click('重试原申请'); await wait(() => requests.length === 3, 'explicit original retry');
  check('explicit retry preserves the full original request', JSON.stringify(requests[2]) === JSON.stringify(unresolved));
  sessions = [{ ...sessions[0], sessionId: 'live', requestId: unresolved.requestId, vehicleId: 'AGV-01', phase: 'SessionRunning', site,
    originSiteId: 'LM44', returnSiteId: 'LM44', requiredPositiveSeconds: 60, updatedAt: new Date().toISOString(), message: '观察连续正电流',
    battery: { current: 19, charging: true, batteryPercent: 30, temperature: 24 }, scriptOperationId: 'live-operation',
    workflowStatus: { operationId: 'live-operation', kind: 'Charge', state: 'Running', updatedAt: new Date().toISOString(),
      message: '正在持续充电', continuousPositiveSeconds: 30, shutdownVerified: false,
      battery: { current: 19, charging: true, observedAt: new Date().toISOString() },
      station: { chargerIp: '192.0.2.11', isAuto: true, charging: null, current: 19, voltage: 54, alarms: [], observedAt: new Date().toISOString() } } }];
  await click('刷新状态');
  await wait(() => evaluate(`document.querySelector('.charge-monitoring').textContent.includes('反馈在线')`), 'fresh matched worker feedback');
  check('fresh station and vehicle evidence are distinct and shared with the footer', await evaluate(`document.querySelector('.charge-monitoring').textContent.includes('桩端充电状态待核定')&&document.querySelector('.charge-monitoring').textContent.includes('车端状态及正电流已确认')&&document.querySelector('.charger-monitor').textContent.includes('反馈在线')`));
  check('return point, continuous time and shutdown verification are shown', await evaluate(`document.querySelector('.charging-page').textContent.includes('返程：LM44')&&document.querySelector('.charging-page').textContent.includes('30.0 秒')&&document.querySelector('.charging-page').textContent.includes('停充核验：尚未确认')`));
  const screen = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  writeFileSync(resolve(evidence, 'charging-live.png'), Buffer.from(screen.data, 'base64'));
  staleStation = true; await click('刷新状态');
  await wait(() => evaluate(`document.querySelector('.charge-monitoring').textContent.includes('桩端反馈过期')`), 'stale station');
  check('old station feedback is not left as online in the page or footer', await evaluate(`!document.querySelector('.charge-monitoring').textContent.includes('反馈在线')&&document.querySelector('.charger-monitor').textContent.includes('桩端反馈过期')`));
  assert.deepEqual(errors, []);
  check('all HTTP activity stayed inside the isolated mock', calls.every(path => path.startsWith('/')));
  console.log('PASS charging browser checks; mock HTTP only. Evidence: ' + evidence);
} finally {
  ws?.close(); child?.kill(); server.closeAllConnections(); await new Promise(ok => server.close(ok));
}
