// Synthetic Chromium check for the controlled-switch panel. It never connects to 5199, PLC or a vehicle.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dist = resolve(project, 'AgvDispatch.Web', process.env.TASK_SWITCH_DIST || 'dist');
const evidence = resolve(project, 'artifacts/task-switch-browser');
const profile = resolve(project, '.codex-tmp', `task-switch-browser-${Date.now()}`);
mkdirSync(evidence, { recursive: true });
mkdirSync(profile, { recursive: true });
const binary = process.env.TEST_BROWSER || ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary, 'A Chromium browser is required');

const taskId = 'TASK-SWITCH-SOURCE';
const requestId = '0123456789abcdef0123456789abcdef';
const now = new Date().toISOString();
let record = null;
const mutations = [];
const unexpected = [];
const task = {
  taskId, stationId: 'CNC-001', vehicleId: 'AGV-01', stage: 'GoingToPickup', priority: 50,
  createdAt: now, updatedAt: now, errorMessage: null,
};
const detail = {
  task,
  steps: [
    {
      stepId: 'STEP-GO', sequence: 1, stepType: 'GoToPickup', status: 'Executing', vehicleId: 'AGV-01',
      sourceSite: 'LM29', destinationSite: 'LM60', vendorTaskId: 'VENDOR-GO', retryCount: 0,
      startedAt: now, completedAt: null, errorMessage: null, isCurrent: true, progressConditions: [],
    },
  ],
  commands: [], resources: [], plcConditions: [], waitingExplanation: '前往取货点首段导航执行中。',
  handshakes: [], executionTimeline: [], executionTimelineTotal: 0, operationHistory: [], operationHistoryTotal: 0,
  handshakeMode: 'ObservationOnly', handshakeContractVersion: '',
};
const options = {
  enabled: true, eligible: true, message: '可申请受控停止。', vehicleId: 'AGV-01',
  stations: [
    { stationId: 'CNC-001', pickupSite: 'LM60', available: true, explanation: '原工位' },
    { stationId: 'CNC-002', pickupSite: 'LM62', available: true, explanation: '目标工位条件满足' },
  ],
  blockers: [], current: null,
};

const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://127.0.0.1');
    const json = (code, value) => {
      res.writeHead(code, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(value));
    };
    if (req.method === 'POST') {
      let raw = '';
      for await (const part of req) raw += part;
      const body = raw ? JSON.parse(raw) : {};
      mutations.push({ path: url.pathname, body });
      if (url.pathname === `/api/tasks/${taskId}/switch`) {
        record = {
          switchId: body.requestId, sourceTaskId: taskId, vehicleId: 'AGV-01', targetStationId: body.targetStationId,
          targetTaskId: null, stage: 'AwaitingStop', message: '已记录取消提交意图，等待原运输终结。',
          blockers: [], revision: 1, updatedAt: now, mapId: 'MAP', mapVersionId: 'V1',
          parameterRevision: 'R1', replacementRequestId: 'fedcba9876543210fedcba9876543210',
          cancelRequestId: '00112233445566770011223344556677', request: body,
        };
        return json(200, { accepted: true, message: record.message, current: record });
      }
      if (url.pathname.startsWith('/api/task-switches/')) return json(400, { error: 'unexpected mutation' });
      return json(503, { error: 'synthetic UI check' });
    }
    if (url.pathname === '/api/config') return json(200, {
      taskSubmissionEnabled: true, taskCancellationEnabled: true, recoveryActionsEnabled: true,
      engineeringSimulationOnly: false, adapter: { informationalVersion: 'task-switch-browser', sha256: 'fixture' },
    });
    if (url.pathname === '/api/charging') return json(200, {
      enabled: false, options: null, blockReason: 'synthetic fixture', sessions: [], observedAt: now,
    });
    if (url.pathname === '/api/tasks') return json(200, { items: [task], totalCount: 1, page: 1, pageSize: 50, summary: null });
    if (url.pathname === `/api/tasks/${taskId}`) return json(200, { ...detail, diagnosticsObservedAt: new Date().toISOString() });
    if (url.pathname === `/api/tasks/${taskId}/switch-options`) return json(200, { ...options, current: record });
    if (url.pathname.startsWith('/api/task-switches/')) return json(200, record);
    if (url.pathname.startsWith('/api/')) {
      unexpected.push(url.pathname);
      return json(503, { error: 'synthetic fixture only' });
    }
    const file = resolve(dist, '.' + (url.pathname === '/' ? '/index.html' : url.pathname));
    if (!file.startsWith(dist + sep) || !existsSync(file)) {
      res.writeHead(404);
      return res.end();
    }
    res.writeHead(200, {
      'Content-Type': file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html',
    });
    res.end(readFileSync(file));
  } catch (error) {
    unexpected.push(String(error));
    res.writeHead(500);
    res.end('{}');
  }
});
await new Promise(ok => server.listen(0, '127.0.0.1', ok));

let child, ws, sequence = 0;
const pending = new Map();
const sleep = ms => new Promise(ok => setTimeout(ok, ms));
async function wait(fn, label) {
  const end = Date.now() + 20000;
  while (Date.now() < end) {
    if (await fn()) return;
    await sleep(100);
  }
  throw new Error(label);
}
function rpc(method, params = {}) {
  return new Promise((ok, fail) => {
    const id = ++sequence;
    const timer = setTimeout(() => fail(new Error(method + ' timeout')), 15000);
    pending.set(id, {
      ok: value => { clearTimeout(timer); ok(value); },
      fail: error => { clearTimeout(timer); fail(error); },
    });
    ws.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await rpc('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
async function click(text) {
  await wait(() => evaluate(`(()=>{const b=[...document.querySelectorAll('button')].find(e=>e.textContent.trim()===${JSON.stringify(text)});if(!b||b.disabled)return false;b.click();return true;})()`), 'button ' + text);
}
function check(label, pass) {
  assert.ok(pass, label);
  console.log('PASS ' + label);
}

try {
  child = spawn(binary, ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-port=0',
    `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  await wait(() => existsSync(resolve(profile, 'DevToolsActivePort')), 'browser');
  const port = readFileSync(resolve(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0];
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  ws = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
  await new Promise(ok => ws.addEventListener('open', ok, { once: true }));
  ws.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (!message.id) return;
    const request = pending.get(message.id);
    if (!request) return;
    pending.delete(message.id);
    message.error ? request.fail(new Error(JSON.stringify(message.error))) : request.ok(message.result);
  });
  await rpc('Page.enable');
  await rpc('Runtime.enable');
  await rpc('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await rpc('Page.addScriptToEvaluateOnNewDocument', { source: `
    window.WebSocket = class {
      constructor() {
        this.timer = setInterval(() => this.onmessage?.({data: JSON.stringify({
          source:'backend', connected:true, receivedAt:new Date().toISOString(),
          tasks:{items:[],totalCount:0}, status:{agvs:[],safety:{dispatchingEnabled:false}},
          devices:{equipment:[]}, resources:[], alarms:[]
        })}), 100);
      }
      close(){clearInterval(this.timer)}
    };` });
  const base = `http://127.0.0.1:${server.address().port}`;
  await rpc('Page.navigate', { url: `${base}/#/tasks/${taskId}` });
  await wait(() => evaluate(`Boolean(document.querySelector('.task-switch-panel'))`), 'task switch panel');
  await click('查看换单条件');
  await wait(() => evaluate(`document.querySelector('.switch-form') !== null`), 'switch form');
  check('Eligible navigation shows the controlled-switch entry', await evaluate(`document.querySelector('.task-switch-panel header p').textContent.includes('空车取货导航')`));
  await evaluate(`(()=>{const s=document.querySelector('.switch-form select');s.value='CNC-002';s.dispatchEvent(new Event('change',{bubbles:true}));const t=document.querySelector('.switch-form textarea');t.value='现场确认空车，目标工位等待处理，需要受控切换';t.dispatchEvent(new Event('input',{bubbles:true}));const c=document.querySelector('.switch-form input[type=checkbox]');c.click();})()`);
  await click('申请受控停止并换单');
  await wait(() => mutations.length === 1, 'switch request');
  const request = mutations[0];
  assert.match(request.body.requestId, /^[0-9a-f]{32}$/);
  check('Request pins original stage, target, confirmation and reason', request.path === `/api/tasks/${taskId}/switch` &&
    request.body.expectedStage === 'GoingToPickup' && request.body.targetStationId === 'CNC-002' &&
    request.body.siteConfirmed === true && request.body.reason.includes('目标工位'));
  await wait(() => evaluate(`document.querySelector('.switch-status strong')?.textContent.includes('等待原任务结束')`), 'stop progress');
  check('Accepted request is shown as awaiting stop, never completed', await evaluate(`document.querySelector('.switch-status strong').textContent.includes('等待原任务结束')`));
  check('Stop boundary explains jack and pickup may already have occurred', await evaluate(`document.querySelector('.task-switch-panel')?.textContent.includes('停止不等于已闭合') && document.querySelector('.task-switch-panel')?.textContent.includes('到站取箱可能包含顶升动作')`));
  const screenshot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  writeFileSync(resolve(evidence, 'controlled-switch-panel.png'), Buffer.from(screenshot.data, 'base64'));
  assert.deepEqual(unexpected, []);
  console.log('PASS isolated task-switch browser checks. Evidence: ' + evidence);
} finally {
  ws?.close();
  child?.kill();
  server.closeAllConnections();
  await new Promise(ok => server.close(ok));
}
