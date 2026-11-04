// Isolated mock HTTP + headless Chromium: no field backend, PLC, vehicle or task writes.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dist = process.env.AGV_SIMULATION_WEB_DIST ? resolve(process.env.AGV_SIMULATION_WEB_DIST) : resolve(project, 'AgvDispatch.Web/dist');
const profile = resolve(project, '.codex-tmp', `simulation-browser-${Date.now()}`);
mkdirSync(profile, { recursive: true });
const binary = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync);
assert.ok(binary, 'Chromium browser required');
let enabled = false, running = true, rootName = 'mock-private-lab', offline = false, fresh = true, active = false, protocolRunning = false, equipmentRunning = false, requestDelay = 0, inflight = 0, maximum = 0;
let child, ws, seq = 0;
const pending = new Map(), mutations = [], errors = [];
const sleep = ms => new Promise(ok => setTimeout(ok, ms));
const status = () => ({ enabled, running: enabled && running, rootName, fresh,
  device: { time: (Date.now() - (fresh ? 0 : 10000)) / 1000, cars: {
    'AGV-01': { x: 1, y: 2, station: 'LM1', loaded: false, current_task: null, offline: false },
    'AGV-02': { x: 3, y: 4, station: 'LM2', loaded: true, current_task: 'original-transport', offline: false }
  } }, map: { nodes: [{ id: '取料', agvSiteId: 'LM1', x: 1, y: 2 }, { id: '倒料', agvSiteId: 'LM2', x: 3, y: 4 }], edges: [{ id: 'E1', from: '取料', to: '倒料', geometryKind: 'CubicBezier', controlPoints: [{ x: 1, y: 4 }, { x: 3, y: 2 }] }] },
  scenario: { id: 'original-scenario', status: active ? 'running' : 'idle', completedRounds: 0, totalRounds: 2, message: '等待原任务反馈' },
  protocol: protocolRunning ? { running: true, name: 'browser-protocol', transport: 'tcp', framing: 'line-json', port: 45678, rules: 1, requests: 0 } : { running: false },
  equipment: equipmentRunning ? { running: true, floors: ['1','2'], currentFloor: '1', targetFloor: null, direction: 'Stopped', door: 'Closed', moving: false, fault: false, signals: { call: true }, schedules: [], signalEvents: [] } : { running: false, currentFloor: '1', door: 'Closed', signals: {} },
  report: { id: 'previous-scenario', name: 'plc-delay', finishedAt: Date.now() / 1000, status: 'inconclusive', checks: [{ name: '真实机械反馈', status: 'unknown', detail: '模型不提供现场证据' }] }
});
const server = createServer(async (req, res) => {
  try {
    const path = new URL(req.url, 'http://127.0.0.1').pathname;
    const json = (code, data) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); };
    if (path === '/api/simulation') {
      inflight++; maximum = Math.max(maximum, inflight);
      try { await sleep(requestDelay); return offline ? json(503, { error: 'controller unavailable fixture' }) : json(200, status()); } finally { inflight--; }
    }
    if (path === '/api/simulation/report') return json(200, status().report);
    if (path.startsWith('/api/simulation/')) {
      let body = ''; for await (const chunk of req) body += chunk;
      mutations.push({ path, body: JSON.parse(body) });
      if (path.endsWith('/run')) active = true;
      if (path.endsWith('/abort')) active = false;
      if (path.endsWith('/stop')) running = false;
      if (path.endsWith('/start')) running = true;
      if (path.endsWith('/new-instance')) { assert.equal(running, false); assert.equal(mutations.at(-1).body.vehicles, 2); rootName = 'mock-fresh-lab'; }
      if (path.endsWith('/configure')) {
        assert.equal(running, false); assert.equal(mutations.at(-1).body.stations.length, 2);
        assert.equal(mutations.at(-1).body.vehicles.length, 2); assert.equal(mutations.at(-1).body.dumper.resourceId, 'RDUMP');
        assert.equal(mutations.at(-1).body.vehicles[0].batteryPercent, 86);
        assert.equal(mutations.at(-1).body.vehicles[0].accelerationMetersPerSecondSquared, .6);
        assert.equal(mutations.at(-1).body.vehicles[0].operationSeconds, 2);
        assert.deepEqual(mutations.at(-1).body.vehicles[0].voltageCurve.map(point => point.percent), [0, 30, 80]);
        assert.equal(mutations.at(-1).body.vehicles[0].chargingGainPercentPerHour, 20);
        rootName = 'mock-custom-lab';
      }
      if (path.endsWith('/protocol-start')) { assert.equal(mutations.at(-1).body.transport, 'tcp'); protocolRunning = true; }
      if (path.endsWith('/protocol-stop')) protocolRunning = false;
      if (path.endsWith('/equipment-start')) equipmentRunning = true;
      if (path.endsWith('/equipment-stop')) equipmentRunning = false;
      return json(200, status());
    }
    if (path.startsWith('/api/')) return json(503, { error: 'mock only' });
    const file = resolve(dist, '.' + (path === '/' ? '/index.html' : path));
    if (!file.startsWith(dist + sep) || !existsSync(file)) { res.writeHead(404); return res.end(); }
    res.writeHead(200, { 'Content-Type': file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html' }); res.end(readFileSync(file));
  } catch (error) { errors.push(String(error)); res.writeHead(500); res.end('{}'); }
});
await new Promise(ok => server.listen(0, '127.0.0.1', ok));
async function wait(fn, message) { const until = Date.now() + 25000; while (Date.now() < until) { if (await fn()) return; await sleep(100); } throw new Error(message); }
function rpc(method, params = {}) { return new Promise((ok, no) => { const id = ++seq, timer = setTimeout(() => no(new Error(method + ' timeout')), 15000); pending.set(id, { ok: result => { clearTimeout(timer); ok(result); }, no }); ws.send(JSON.stringify({ id, method, params })); }); }
async function evaluate(expression) { const result = await rpc('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }); if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails)); return result.result.value; }
async function click(label) {
  await wait(() => evaluate(`(()=>{const b=[...document.querySelectorAll('.simulation-page button')].find(e=>e.textContent.trim()===${JSON.stringify(label)});if(!b||b.disabled||b.matches(':disabled'))return false;b.click();return true})()`), 'Button unavailable: ' + label);
  await wait(async () => !(await disabled('刷新原记录')), 'Request completed: ' + label);
}
async function disabled(label) { return evaluate(`(()=>{const b=[...document.querySelectorAll('.simulation-page button')].find(e=>e.textContent.trim()===${JSON.stringify(label)});return !!b&&(b.disabled||b.matches(':disabled'))})()`); }
function check(label, condition) { assert.ok(condition, label); console.log('PASS ' + label); }
try {
  child = spawn(binary, ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  await wait(() => existsSync(resolve(profile, 'DevToolsActivePort')), 'Browser startup');
  const port = readFileSync(resolve(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0];
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  ws = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
  await new Promise(ok => ws.addEventListener('open', ok, { once: true }));
  ws.addEventListener('message', event => { const data = JSON.parse(event.data); if (data.id) { const entry = pending.get(data.id); if (entry) { pending.delete(data.id); data.error ? entry.no(new Error(JSON.stringify(data.error))) : entry.ok(data.result); } } });
  await rpc('Page.enable'); await rpc('Runtime.enable');
  await rpc('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
  await rpc('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/#/simulation-center` });
  await wait(() => evaluate(`document.querySelector('.configuration-guide')?.textContent.includes('配置一次')`), 'Disabled environment guidance');
  check('Unconfigured environment cannot start or submit scenarios', await disabled('启动仿真环境') && await disabled('运行场景') && mutations.length === 0);
  enabled = true; await click('刷新原记录');
  await wait(() => evaluate(`document.querySelectorAll('.map-car').length===2&&document.querySelector('.environment-state').textContent.includes('反馈有效')`), 'Two device positions');
  check('Map renders observed vehicles and original curved roads', await evaluate(`document.querySelectorAll('.roads path').length===1&&document.querySelector('.roads path').getAttribute('d').includes('C')&&document.querySelector('.environment-state').textContent.includes('反馈有效')`));
  await click('启动协议服务器');
  check('Declarative protocol server starts only on loopback and reports its assigned endpoint',
    mutations.at(-1).path.endsWith('/protocol-start') && mutations.at(-1).body.rules.length===1 &&
    await evaluate(`document.querySelector('.protocol-designer').textContent.includes('tcp://127.0.0.1:45678')`));
  await click('停止协议服务器');
  await click('启动设备模型');
  await click('前往2层');
  check('Optional elevator/door model accepts a bounded target-floor command',
    mutations.at(-1).path.endsWith('/equipment-control') && mutations.at(-1).body.elevator.targetFloor==='2' && !!mutations.at(-1).body.sequence);
  await click('停止设备模型');
  const screenshot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  const screenshotPath = resolve(profile, 'simulation-center.png'); writeFileSync(screenshotPath, Buffer.from(screenshot.data, 'base64')); console.log('Preview ' + screenshotPath);
  await evaluate(`(()=>{const e=document.querySelector('[aria-label="选择虚拟车辆"]');e.value='AGV-02';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  await click('断开通信');
  check('Fault targets the selected original vehicle and has a unique sequence', mutations.at(-1).body.vehicles['AGV-02'].offline === true && !!mutations.at(-1).body.sequence);
  await click('丢弃下一响应');
  check('Response loss is explicitly requested as a one-shot control', mutations.at(-1).body.vehicles['AGV-02'].dropNextResponse === true);
  await click('阻止 Done');
  check('PLC fault request changes the device input model', mutations.at(-1).body.plc.suppressDone === true);
  await evaluate(`(()=>{document.querySelector('.edge-controls').open=true;document.querySelector('.edge-options input').click()})()`);
  await click('应用道路阻塞');
  check('Road fault references the selected vehicle and original edge ID', mutations.at(-1).body.vehicles['AGV-02'].blockedEdges.join(',') === 'E1');
  await click('解除所选车全部道路阻塞');
  check('Road unblock clears only the selected vehicle edge list', mutations.at(-1).body.vehicles['AGV-02'].blockedEdges.length === 0);
  fresh = false; await click('刷新原记录');
  check('Stale evidence disables scene starts and device controls', await disabled('运行场景') && await disabled('暂停运动') && await disabled('注入设备故障'));
  fresh = true; await click('刷新原记录');
  await evaluate(`(()=>{const e=document.querySelector('[aria-label="测试场景"]');e.value='simultaneous-calls';e.dispatchEvent(new Event('change',{bubbles:true}));const n=document.querySelector('.scenario-panel input');n.value='2';n.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  await click('运行场景');
  check('Scenario preserves selected kind and round count', mutations.at(-1).body.name === 'simultaneous-calls' && mutations.at(-1).body.rounds === 2);
  check('Running scenario prevents a second start', await disabled('运行场景'));
  check('A prior report is identified separately from the current running scenario', await evaluate(`document.querySelector('.report-identity').textContent.includes('上一场景报告')&&document.querySelector('.report-identity').textContent.includes('previous-scenario')&&document.querySelector('.report-panel').textContent.includes('当前场景仍在运行')`));
  await click('中止后续场景');
  check('Abort does not mutate original tasks or clear evidence', mutations.at(-1).path.endsWith('/abort'));
  check('A running environment cannot be replaced with a fresh instance', await disabled('沿用当前地图新建'));
  await click('停止仿真环境');
  await evaluate(`(()=>{const map={name:'custom-mock',nodes:[
    {id:'PICK-A',agvSiteId:'LA',x:1,y:1,enabled:true},{id:'PICK-B',agvSiteId:'LB',x:2,y:1,enabled:true},
    {id:'WAIT',agvSiteId:'LW',x:3,y:1,enabled:true},{id:'DUMP',agvSiteId:'LD',x:4,y:1,enabled:true},
    {id:'PARK',agvSiteId:'LP',x:5,y:1,enabled:true}],
    edges:[{id:'E1',from:'PICK-A',to:'WAIT',bidirectional:true},{id:'E2',from:'WAIT',to:'DUMP',bidirectional:true},{id:'E3',from:'PICK-B',to:'WAIT',bidirectional:true},{id:'E4',from:'DUMP',to:'PARK',bidirectional:true}],
    resources:[{id:'RDUMP',capacity:1,boundary:[{x:3.5,y:.5},{x:4.5,y:.5},{x:4.5,y:1.5}]}]};
    const input=document.querySelector('.file-picker input');const transfer=new DataTransfer();transfer.items.add(new File([JSON.stringify(map)],'custom.agvmap.json',{type:'application/json'}));input.files=transfer.files;input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await wait(()=>evaluate(`document.querySelector('.map-import')?.textContent.includes('custom.agvmap.json')&&document.querySelectorAll('.vehicle-config-row').length===2`),'Custom map form loaded');
  await click('增加曲线点');
  await evaluate(`(()=>{const rows=[...document.querySelectorAll('.vehicle-config-row')[0].querySelectorAll('.voltage-point')];const percents=[80,0,30];rows.forEach((row,index)=>{const input=row.querySelector('input');input.value=String(percents[index]);input.dispatchEvent(new Event('input',{bubbles:true}))})})()`);
  check('Battery voltage curve supports bounded custom points before submission',
    await evaluate(`document.querySelectorAll('.vehicle-config-row')[0].querySelectorAll('.voltage-point').length===3`));
  await click('创建自定义隔离实例');
  check('Imported map, stations, vehicles and dumper resource are submitted as one isolated configuration',
    mutations.at(-1).path.endsWith('/configure') && mutations.at(-1).body.mapDefinition.nodes.length===5 &&
    mutations.at(-1).body.stations.length===2 && mutations.at(-1).body.vehicles.length===2 &&
    mutations.at(-1).body.dumper.resourceId==='RDUMP' && await evaluate(`document.querySelector('#simulation-environment').textContent==='mock-custom-lab'`));
  await evaluate(`(()=>{const input=document.querySelector('[aria-label="新实例车辆数"]');input.value='9';input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  check('Fresh instance vehicle count outside 1..8 is rejected in the form', await disabled('沿用当前地图新建'));
  await evaluate(`(()=>{const input=document.querySelector('[aria-label="新实例车辆数"]');input.value='2';input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  await click('沿用当前地图新建');
  check('A stopped environment can prepare a fresh sibling without clearing old records', mutations.at(-1).path.endsWith('/new-instance') && await evaluate(`document.querySelector('#simulation-environment').textContent==='mock-fresh-lab'`));
  await click('启动仿真环境');
  check('Unknown report evidence is never styled as passed', await evaluate(`document.querySelector('.report-table .state-pill').textContent==='未核定'&&!document.querySelector('.report-table .state-pill').classList.contains('pass')`));
  requestDelay = 2200; maximum = 0; await sleep(6500);
  check('Slow polling has no overlapping status requests', maximum === 1);
  requestDelay = 0; offline = true;
  await wait(() => evaluate(`document.querySelector('.simulation-error')?.textContent.includes('controller unavailable')`), 'Controller outage');
  check('Controller outage blocks controls despite retained prior device data', await disabled('运行场景') && await disabled('恢复通信'));
  assert.deepEqual(errors, []); console.log('PASS simulation browser regression; mock HTTP only');
} finally { ws?.close(); child?.kill(); server.closeAllConnections(); await new Promise(ok => server.close(ok)); }
