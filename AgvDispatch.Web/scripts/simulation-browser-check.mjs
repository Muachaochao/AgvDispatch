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
let enabled = false, running = true, deviceCars = true, rootName = 'mock-private-lab', offline = false, fresh = true, active = false, protocolRunning = false, equipmentRunning = false, requestDelay = 0, statusInflight = 0, statusMaximum = 0, liveInflight = 0, liveMaximum = 0, taskDetailUnavailable = false;
let child, ws, seq = 0;
const pending = new Map(), mutations = [], errors = [];
const sleep = ms => new Promise(ok => setTimeout(ok, ms));
const versionComparison = { status: 'different', message: '仿真与现场存在后台构建和派车参数差异', observedAt: Date.now() / 1000,
  field: { informationalVersion: 'field-original' }, simulation: { informationalVersion: 'simulation-test' },
  differences: [{ key: 'automatic', label: '自动建单', field: false, simulation: true }], scope: '仅比较所列构建和参数' };
const priorReport = { id: 'history-baseline', name: 'normal-cycle', status: 'passed', finishedAt: 1700000000,
  metrics: { taskCount: 1, durationSeconds: 20 }, checks: [] };
const taskRows = [
  { taskId: 'TASK-ORIGINAL', stationId: 'CNC-002', vehicleId: 'AGV-02', stage: 'Dumping', priority: 0,
    createdAt: '2026-11-07T00:00:00Z', updatedAt: '2026-11-07T00:01:00Z' },
  { taskId: 'TASK-WAITING', stationId: 'CNC-003', vehicleId: null, stage: 'WaitingForVehicle', priority: 0,
    createdAt: '2026-11-07T00:00:05Z', updatedAt: '2026-11-07T00:00:30Z' }
];
const taskDetail = { task: taskRows[0], waitingExplanation: '等待倒料机给出可以继续的反馈',
  steps: [
    'GoToPickup', 'PickupFullBox', 'DeliverFullBoxToDumper', 'WaitForDumperReady', 'EnterDumpingPosition',
    'DumpFullBox', 'ReturnEmptyBox', 'PlaceEmptyBox', 'CompletePlcHandshake'
  ].map((stepType, index) => ({ stepId: `STEP-${index + 1}`, sequence: index + 1, stepType,
    status: index < 4 ? 'Completed' : index === 4 ? 'Executing' : 'Pending', isCurrent: index === 4,
    waitingExplanation: index === 4 ? '等待倒料机允许进入' : '', elapsedText: index < 4 ? '1.0秒' : '',
    startedAt: index <= 4 ? `2026-11-07T00:00:0${index}Z` : undefined,
    completedAt: index < 4 ? `2026-11-07T00:00:0${index + 1}Z` : undefined,
    completionRequirement: '必须取得设备反馈后才允许推进', evidenceBoundary: '只读任务证据，不写PLC' })),
  executionTimeline: [{ id: 'event-step', occurredAt: '2026-11-07T00:00:02Z', stepId: 'STEP-2',
    kind: 'StepCompleted', title: '满箱已取走', summary: '车辆反馈取货完成', source: '车辆' }],
  handshakes: [{ taskId: 'TASK-ORIGINAL', stepId: 'STEP-5', cycleId: 'CYCLE-1', contractVersion: 'v1',
    phase: 'WaitingRequest', revision: 2, reason: '等待设备允许进入', pendingSignal: 'DumperReady', pendingValue: true,
    nextPhase: 'RequestAcknowledged', startedAt: '2026-11-07T00:00:04Z', updatedAt: '2026-11-07T00:00:20Z', baseline: [] }],
  commands: [
    ['COMMAND-1', 'STEP-1', 'Completed', 'JackLoad', 'LM1 > LM2'],
    ['COMMAND-2', 'STEP-3', 'Completed', 'Wait', 'LM2 > LM1'],
    ['COMMAND-3', 'STEP-5', 'Sent', 'Wait', 'LM1 > LM2'],
    ['COMMAND-4', 'STEP-7', 'Pending', 'JackUnload', 'LM2 > LM1']
  ].map(([commandId, stepId, status, action, sites]) => ({ commandId, stepId, status, attemptCount: 1,
    route: { taskId: 'TASK-ORIGINAL', stepId, commandId, vehicleId: 'AGV-02',
      mapId: 'map', mapVersionId: 'v1', mapDefinitionHash: 'a'.repeat(64), nodeIds: ['取料', '倒料'],
      edgeIds: ['E1'], vendorSites: sites.split(' > '), action } })),
  resources: [{ resourceId: 'RDUMP', vehicleId: 'AGV-02', expiresAt: '2026-11-07T00:02:00Z', heartbeatExpired: false }],
  plcConditions: [{ stationId: 'CNC-002', variableName: 'DumperReady', currentValue: false,
    expectedValue: true, quality: 'Good', satisfied: false }],
  handover: { state: 'Pending', reason: '等待原任务到达出区步骤', returnCommandCompleted: false,
    exitPhysicallyVerified: false, heldResources: ['RDUMP'], recordedReleaseReasons: [] }
};
const reportSteps = taskDetail.steps.map((step, index) => ({ number: index + 1, stepType: step.stepType,
  stepName: step.stepType, status: step.status, waitingExplanation: step.waitingExplanation }));
const pointTable = { fileName: 'mock-four-stations.csv', sha256: 'a'.repeat(64), bytes: 4096, rowCount: 30,
  signalCount: 30, stationCount: 3, dumperSignalCount: 5, csvText: 'mock,csv,content',
  stations: [{ stationId: 'CNC-001', signalSource: 'verified', fieldConfirmed: true },
    { stationId: 'CNC-002', signalSource: 'verified', fieldConfirmed: true },
    { stationId: 'CNC-003', signalSource: 'isolated-synthetic', fieldConfirmed: false }] };
const status = () => ({ enabled, running: enabled && running, rootName, fresh,
  selectedSchemeId: 'scheme-1', schemes: [{ id: 'scheme-1', name: '双车排队方案', vehicleCount: 2, stationCount: 2,
    plcTableName: pointTable.fileName, plcTableSha256: pointTable.sha256 }],
  recentInstances: [{ rootName: 'mock-prior-lab', mapName: '已保存地图', vehicleCount: 2, stationCount: 2, updatedAt: 1700000000 }],
  stationCatalog: pointTable.stations.map((station, index) => ({ ...station, sourceLabel: station.fieldConfirmed ? '有效点表工位' + (index + 1) : '隔离点表工位3' })),
  plcSignalTable: pointTable,
  versionComparison, history: [priorReport], recoveryGuidance: [{ taskId: 'ORIGINAL', vehicleId: 'AGV-02', stationId: 'CNC-002',
    stepNumber: 8, stepName: '倒料机就绪', blockedBy: '倒料机未给就绪许可', restoreAction: '解除就绪许可阻止', expectedFeedback: '原任务继续等待倒料完成' }],
  device: { time: (Date.now() - (fresh ? 0 : 10000)) / 1000, cars: !deviceCars ? {} : {
    'AGV-01': { x: 1, y: 2, station: 'LM1', loaded: false, current_task: null, offline: false },
    'AGV-02': { x: 3, y: 4, station: 'LM2', loaded: true, current_task: 'original-transport', offline: false }
  } }, map: { nodes: [{ id: '取料', agvSiteId: 'LM1', x: 1, y: 2 }, { id: '倒料', agvSiteId: 'LM2', x: 3, y: 4 }], edges: [{ id: 'E1', from: '取料', to: '倒料', geometryKind: 'CubicBezier', controlPoints: [{ x: 1, y: 4 }, { x: 3, y: 2 }] }],
    areaAnnotations: [{ id: 'SIM-AREA-G1', sourceType: 'CadSimulationEvidence', text: '测试取货分区', boundary: [{ x: 0, y: 0 }, { x: 5, y: 0 }, { x: 5, y: 5 }, { x: 0, y: 5 }], strokeColor: '#38bdf8', fillColor: '#0ea5e9', fillOpacity: .08 },
      { id: 'CAD-LAYER', sourceType: 'CadRasterBackground', text: 'CAD参考', sourceJson: JSON.stringify({ asset: 'cad/mock.png', opacity: .2, viewBox: { minX: 0, minY: 0, width: 5000, height: 5000 } }) }] },
  scenario: { id: 'original-scenario', status: active ? 'running' : 'idle', completedRounds: 0, totalRounds: 2, message: '等待原任务反馈' },
  protocol: protocolRunning ? { running: true, name: 'browser-protocol', transport: 'tcp', framing: 'line-json', port: 45678, rules: 1, requests: 0 } : { running: false },
  equipment: equipmentRunning ? { running: true, floors: ['1','2'], currentFloor: '1', targetFloor: null, direction: 'Stopped', door: 'Closed', moving: false, fault: false, signals: { call: true }, schedules: [], signalEvents: [] } : { running: false, currentFloor: '1', door: 'Closed', signals: {} },
  report: { id: 'previous-scenario', name: 'plc-delay', finishedAt: Date.now() / 1000, status: 'inconclusive', versionComparison,
    firstFailure: '倒料机就绪等待超时', metrics: { taskCount: 2, durationSeconds: 30 },
    taskSummary: [{ taskId: 'TASK-ORIGINAL', stationId: 'CNC-002', vehicleId: 'AGV-02', stage: 'Dumping',
      currentStepNumber: 5, currentStepName: '进入倒料位', currentStepStatus: 'waiting', waitingExplanation: '等待倒料就绪',
      completed: false, steps: reportSteps }],
    waitingChain: [{ taskId: 'ORIGINAL', vehicleId: 'AGV-02', site: 'LM2', waitReason: '等待倒料资源释放' }],
    checks: [{ name: '真实机械反馈', status: 'unknown', detail: '模型不提供现场证据' }, { name: '设备确认记录', status: 'passed', detail: { accepted: true, sequence: 'original-input' } }, { name: '原任务身份', status: 'passed', detail: ['TASK-1', 'TASK-2'] }] }
});
const server = createServer(async (req, res) => {
  try {
    const path = new URL(req.url, 'http://127.0.0.1').pathname;
    const json = (code, data) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); };
    if (path === '/api/simulation') {
      if (req.method !== 'GET') return offline ? json(503, { error: 'controller unavailable fixture' }) : json(200, status());
      statusInflight++; statusMaximum = Math.max(statusMaximum, statusInflight);
      if (statusInflight > 1) console.error('OVERLAP', req.method, path, statusInflight);
      try { await sleep(requestDelay); return offline ? json(503, { error: 'controller unavailable fixture' }) : json(200, status()); } finally { statusInflight--; }
    }
    if (path === '/api/simulation/live') {
      liveInflight++; liveMaximum = Math.max(liveMaximum, liveInflight);
      if (liveInflight > 1) console.error('OVERLAP', req.method, path, liveInflight);
      try {
        await sleep(requestDelay);
        const current = status();
        return offline ? json(503, { error: 'controller unavailable fixture' }) : json(200, {
          enabled: current.enabled, rootName: current.rootName, running: current.running,
          phase: current.phase, fresh: current.fresh, device: current.device,
          scenario: current.scenario, error: current.error ?? null
        });
      } finally { liveInflight--; }
    }
    if (path === '/api/simulation/report') return json(200, new URL(req.url, 'http://127.0.0.1').searchParams.get('id') === priorReport.id ? priorReport : status().report);
    if (path === '/api/simulation/replay') return json(200, { id: 'previous-scenario', map: status().map, timeline: [
      { time: 1700000000, vehicles: [{ vehicleId: 'AGV-02', x: 1, y: 2, site: 'LM1', taskId: 'ORIGINAL', waitReason: '等待倒料资源' }], tasks: [{ taskId: 'ORIGINAL', vehicleId: 'AGV-02', stationId: 'CNC-002', step: '等待倒料' }] },
      { time: 1700000001, vehicles: [{ vehicleId: 'AGV-02', x: 3, y: 4, site: 'LM2', taskId: 'ORIGINAL', waitReason: '等待就绪许可' }], tasks: [] }
    ] });
    if (path === '/api/simulation/point-table') return json(200, pointTable);
    if (path === '/api/simulation/tasks') return json(200, { items: taskRows, totalCount: taskRows.length, page: 1, pageSize: 500,
      summary: { total: 2, running: 1, waiting: 1, completed: 0, cancelled: 0, failed: 0, other: 0 } });
    if (path.startsWith('/api/simulation/tasks/')) return taskDetailUnavailable
      ? json(503, { error: '任务实时明细不可用' }) : json(200, taskDetail);
    if (path === '/api/simulation/snapshot') return json(200, { receivedAt: new Date().toISOString(), connected: true, map: status().map,
      tasks: { items: taskRows, totalCount: taskRows.length }, devices: { equipment: [] }, resources: [], positionedVehicleIds: ['AGV-01', 'AGV-02'],
      traffic: { capturedAt: new Date().toISOString(), requestsTruncated: false, requests: [{ taskId: 'TASK-ORIGINAL', stepId: 'STEP-5',
        vehicleId: 'AGV-02', state: 'Waiting', reason: '交叉口路权被占用，等待前车完成原任务', resourceIds: ['TRAFFIC-LM9'],
        heldResources: [], blockingObservationCurrent: true, waitingSeconds: 32, waitingStartKnown: true, waitingClockValid: true,
        waitingCategory: 'Route', blockers: [{ taskId: 'TASK-BLOCKER', stepId: 'STEP-7', vehicleId: 'AGV-01',
          resourceId: 'TRAFFIC-LM9', heartbeatExpired: false }] }] } });
    if (path === '/api/simulation/resources') return json(200, []);
    if (path.startsWith('/api/simulation/')) {
      let body = ''; for await (const chunk of req) body += chunk;
      mutations.push({ path, body: JSON.parse(body) });
      if (path.endsWith('/run')) active = true;
      if (path.endsWith('/abort')) active = false;
      if (path.endsWith('/stop')) running = false;
      if (path.endsWith('/start')) { running = true; deviceCars = true; }
      if (path.endsWith('/new-instance')) { assert.equal(running, false); assert.equal(mutations.at(-1).body.vehicles, 2); rootName = 'mock-fresh-lab'; deviceCars = false; }
      if (path.endsWith('/scheme-select') || path.endsWith('/scheme-start')) { assert.equal(running, false); assert.equal(mutations.at(-1).body.id, 'scheme-1'); rootName = 'mock-scheme-lab'; deviceCars = path.endsWith('/scheme-start'); running = path.endsWith('/scheme-start'); }
      if (path.endsWith('/configure')) {
        assert.equal(running, false); assert.equal(mutations.at(-1).body.stations.length, 2);
        assert.equal(mutations.at(-1).body.vehicles.length, 2); assert.equal(mutations.at(-1).body.dumper.resourceId, 'RDUMP');
        assert.equal(mutations.at(-1).body.plcSignalTable.sha256, pointTable.sha256);
        assert.equal(mutations.at(-1).body.mapIdentity.sourceName, 'custom.agvmap.json');
        assert.equal(mutations.at(-1).body.vehicles[0].batteryPercent, 86);
        assert.equal(mutations.at(-1).body.vehicles[0].accelerationMetersPerSecondSquared, .6);
        assert.equal(mutations.at(-1).body.vehicles[0].operationSeconds, 2);
        assert.deepEqual(mutations.at(-1).body.vehicles[0].voltageCurve.map(point => point.percent), [0, 30, 80]);
        assert.equal(mutations.at(-1).body.vehicles[0].chargingGainPercentPerHour, 20);
        rootName = 'mock-custom-lab'; deviceCars = false;
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
  await wait(async () => !(await disabled('刷新状态')), 'Request completed: ' + label);
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
  ws.addEventListener('message', event => { const data = JSON.parse(event.data); if (data.id) { const entry = pending.get(data.id); if (entry) { pending.delete(data.id); data.error ? entry.no(new Error(JSON.stringify(data.error))) : entry.ok(data.result); } } else if(data.method==='Runtime.exceptionThrown'||data.method==='Runtime.consoleAPICalled'&&data.params.type==='error') errors.push(JSON.stringify(data.params)); });
  await rpc('Page.enable'); await rpc('Runtime.enable');
  await rpc('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
  await rpc('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/#/simulation-center` });
  await wait(() => evaluate(`document.querySelector('.overview-hero')?.textContent.includes('还没有准备仿真环境')&&document.querySelector('.module-nav')?.textContent.includes('环境与地图')`), 'Disabled environment overview guidance');
  check('Business overview renders without blanking the page', await evaluate(`document.querySelector('.overview-hero').textContent.includes('还没有准备仿真环境')&&document.querySelector('.business-story').textContent.includes('AGV-01')&&!document.querySelector('.simulation-error')`));
  check('Simulation page does not expose vendor-specific implementation names',
    !(await evaluate(`document.querySelector('.simulation-page').textContent.includes('ProfControl')`)));
  check('Unconfigured environment cannot start, and scenario controls stay on their own page',
    !(await evaluate(`!!document.querySelector('.scenario-panel')||!!document.querySelector('.module-heading')`)) && mutations.length === 0);
  enabled = true; await click('刷新状态');
  await wait(() => evaluate(`document.querySelectorAll('.map-car').length===2&&document.querySelector('.overview-status-title').textContent.includes('运行正常')`), 'Two device positions');
  check('Map renders observed vehicles and original curved roads', await evaluate(`document.querySelectorAll('.roads path').length===1&&document.querySelector('.roads path').getAttribute('d').includes('C')&&document.querySelector('.overview-status-title').textContent.includes('运行正常')`));
  check('Vehicle legend, state color and load badge use separate meanings', await evaluate(`(()=>{const cars=[...document.querySelectorAll('.map-car')];const color=id=>cars.find(car=>[...car.querySelectorAll('text')].at(-1)?.textContent===id)?.style.getPropertyValue('--car-color').trim();return color('AGV-01')==='#6fb9ff'&&color('AGV-02')==='#e8bf64'&&document.querySelectorAll('.map-load-badge').length===1&&document.querySelector('.map-run-legend').textContent.includes('载箱待命')&&document.querySelector('.map-run-legend').textContent.includes('箱 = 当前载箱')})()`));
  check('Business map data is primary and CAD reference is hidden by default',
    await evaluate(`document.querySelector('.map-zones').textContent.includes('测试取货分区')&&document.querySelector('.map-node text').textContent==='LM1'&&!document.querySelector('.cad-reference')`));
  await click('车辆附近');
  check('Map can focus on the current vehicle area without removing the full-factory view',
    await evaluate(`document.querySelector('.simulation-map').getAttribute('viewBox')!=='0 0 800 430'`));
  await click('全厂');
  await click('三维');
  await wait(() => evaluate(`!!document.querySelector('.three-canvas canvas')`), 'Three dimensional canvas');
  check('Three dimensional view lists both observed vehicles', await evaluate(`document.querySelectorAll('.three-vehicles button').length===2&&!document.querySelector('.three-error')`));
  await evaluate(`document.querySelector('.three-vehicles button').click()`);
  await wait(() => evaluate(`document.querySelector('.three-vehicles button[aria-pressed=true] small')?.textContent.includes('X 1.000 m')`), 'Shared world coordinates');
  check('Selected three dimensional vehicle preserves observed XY coordinates', await evaluate(`document.querySelector('.three-vehicles button[aria-pressed=true] small').textContent.includes('Y 2.000 m')`));
  const threeShot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  writeFileSync(resolve(profile, 'simulation-three.png'), Buffer.from(threeShot.data, 'base64'));
  await click('二维');
  await wait(() => evaluate(`document.querySelectorAll('.map-car').length===2&&!document.querySelector('.three-canvas')`), 'Return to two dimensional map');
  const overviewShot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  writeFileSync(resolve(profile, 'simulation-overview.png'), Buffer.from(overviewShot.data, 'base64'));
  await click('查看CAD参考');
  check('CAD reference can be enabled only as a separate inspection layer', await evaluate(`document.querySelectorAll('.cad-reference').length===1&&document.querySelector('.map-caption').textContent.includes('只用于核对位置')`));
  await click('关闭CAD参考');
  for (const [index, stage, name] of [['2', '.tasks-stage', 'tasks'], ['3', '.environment-stage', 'environment'], ['4', '.scenario-stage', 'scenario'], ['5', '.fault-stage', 'faults'], ['7', '.results-stage', 'results']]) {
    await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes(${JSON.stringify({tasks:'任务运行',environment:'环境与地图',scenario:'场景编排',faults:'故障处理',results:'监测与报告'}[name])})).click()`);
    await wait(() => evaluate(`document.querySelector('${stage}')?.offsetParent!==null`), 'Stage visible: ' + stage);
    await wait(() => evaluate(`location.hash==='#/simulation-center/${name}'`), 'Independent route: ' + name);
    const stageShot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
    writeFileSync(resolve(profile, `simulation-${name}.png`), Buffer.from(stageShot.data, 'base64'));
  }
  check('Workflow modules have independent URLs and only the selected workflow is mounted', true);
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('任务运行')).click()`);
  await wait(() => evaluate(`document.querySelector('.tasks-stage')?.offsetParent!==null&&document.querySelectorAll('.task-table tbody tr').length===2&&document.querySelectorAll('.task-step-list li').length===9`), 'Task page and nine-step detail');
  check('Task page reads the isolated task API and highlights the selected task route',
    await evaluate(`document.querySelector('.task-command-panel').textContent.includes('隔离后台在线')&&document.querySelector('.task-route-panel .roads path.task-route')&&document.querySelector('.task-route-panel .map-node.task-node')&&document.querySelector('.task-timeline').textContent.includes('PLC')`));
  await wait(() => evaluate(`document.querySelectorAll('.task-route-leg-list button').length===4&&document.querySelector('.task-route-now').textContent.includes('AGV-01')&&document.querySelector('.task-route-now').textContent.includes('32 秒')`), 'Route legs and traffic blocker guidance');
  check('Task route explains the blocking vehicle, resource and return leg',
    await evaluate(`document.querySelector('.task-route-now').textContent.includes('交叉口路权')&&document.querySelector('.task-route-leg-list .returning').textContent.includes('返程')`));
  await evaluate(`document.querySelector('.task-route-leg-list .returning').click()`);
  await wait(() => evaluate(`document.querySelector('.task-route-panel .roads path.task-route-focus.task-route-return')&&document.querySelector('.task-route-leg-list .returning').classList.contains('active')`), 'Return leg focus');
  check('Selecting a route leg isolates that leg on the map', true);
  await evaluate(`document.querySelector('.task-route-panel').scrollIntoView({block:'start'})`);
  const routeShot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  writeFileSync(resolve(profile, 'simulation-task-route.png'), Buffer.from(routeShot.data, 'base64'));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('环境与地图')).click()`);
  await wait(() => evaluate(`document.querySelector('.environment-stage')?.offsetParent!==null&&!!document.querySelector('.version-panel')`), 'Version panel on environment page');
  check('Build differences stay on the environment page', await evaluate(`document.querySelector('.version-panel').textContent.includes('field-original')&&document.querySelector('.version-panel').textContent.includes('自动建单')`));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('监测与报告')).click()`);
  await wait(() => evaluate(`document.querySelector('.results-stage')?.offsetParent!==null`), 'Return to results page');
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('人工处置')).click()`);
  await wait(() => evaluate(`!!document.querySelector('.recovery-guidance')`), 'Manual recovery guidance');
  check('Recovery guidance is readable without raw configuration keys', await evaluate(`document.querySelector('.recovery-guidance').textContent.includes('第8步')&&document.querySelector('.recovery-guidance').textContent.includes('解除就绪许可阻止')`));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('监测与报告')).click()`);
  await wait(() => evaluate(`!!document.querySelector('.report-panel')`), 'Results report');
  check('Report highlights task records, first failure, waiting chain and Chinese metrics', await evaluate(`document.querySelector('.first-failure').textContent.includes('倒料机就绪等待超时')&&document.querySelector('.task-records').textContent.includes('TASK-ORIGINAL')&&document.querySelector('.task-records').textContent.includes('等待倒料就绪')&&document.querySelector('.report-panel .waiting-chain').textContent.includes('等待倒料资源释放')&&document.querySelector('.report-metrics').textContent.includes('运行时长（秒）')&&!document.querySelector('.report-metrics').textContent.includes('durationSeconds')`));
  await evaluate(`document.querySelector('.task-record-row').click()`);
  await wait(() => evaluate(`document.querySelectorAll('.report-task-nine-steps li').length===9`), 'Expanded report task evidence');
  check('Report task rows expand to nine-step, PLC, command and resource evidence',
    await evaluate(`document.querySelector('.report-task-evidence').textContent.includes('倒料机就绪')&&document.querySelector('.report-task-evidence').textContent.includes('运输命令与出区')&&document.querySelector('.report-task-evidence').textContent.includes('RDUMP')&&!document.querySelector('.report-task-evidence').textContent.includes('DumperReady')`));
  await evaluate(`document.querySelector('.task-record-row').click()`);
  taskDetailUnavailable = true;
  await evaluate(`document.querySelector('.task-record-row').click()`);
  await wait(() => evaluate(`document.querySelectorAll('.report-task-nine-steps-summary li').length===9&&document.querySelector('.report-task-evidence').textContent.includes('报告内九步证据已显示')`), 'Report summary steps survive live detail outage');
  check('Saved report keeps nine-step evidence when live task detail is unavailable', true);
  await click('载入本报告时间轴');
  check('Replay loads original report positions on its saved map without device mutations', await evaluate(`document.querySelector('.replay-map .map-car text').textContent==='AGV-02'&&document.querySelector('.history-panel .waiting-chain').textContent.includes('ORIGINAL')`));
  await evaluate(`(()=>{const input=document.querySelector('[aria-label="回放时间点"]');input.value='1';input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  await wait(() => evaluate(`document.querySelector('.history-panel .waiting-chain').textContent.includes('等待就绪许可')`), 'Replay changes recorded frame');
  check('Replay scrubbing preserves original task and has no control request', mutations.length === 0);
  await evaluate(`(()=>{const e=document.querySelectorAll('.history-panel select')[1];e.value='history-baseline';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  await wait(() => evaluate(`document.querySelector('.history-panel table')?.textContent.includes('+10')`), 'Report comparison finite numeric delta');
  await evaluate(`(()=>{const e=document.querySelector('.history-panel select');e.value='history-baseline';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  await wait(() => evaluate(`document.querySelector('.report-identity').textContent.includes('history-baseline')`), 'Read selected historical report');
  check('History selection reads the requested report rather than replacing it with current status polling', await evaluate(`document.querySelector('.report-panel').textContent.includes('history-baseline')`));
  await evaluate(`(()=>{const e=document.querySelector('.history-panel select');e.value='';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('场景编排')).click()`);
  await wait(() => evaluate(`document.querySelector('.scenario-stage')?.offsetParent!==null`), 'Scenario page');
  await evaluate(`(()=>{const e=document.querySelector('[aria-label="测试场景"]');e.value='dispatch-backlog';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  await click('运行场景');
  check('Formal-dispatch backlog emits station calls continuously without any vehicle selection', mutations.at(-1).body.name === 'dispatch-backlog' && mutations.at(-1).body.calls.length === 6 && mutations.at(-1).body.calls.every(call => !Object.hasOwn(call, 'vehicleId')));
  await click('中止后续场景');
  await evaluate(`(()=>{const e=document.querySelector('[aria-label="测试场景"]');e.value='normal-cycle';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('环境与地图')).click()`);
  await wait(() => evaluate(`document.querySelector('.environment-stage')?.offsetParent!==null`), 'Environment page for protocol controls');
  await click('启动协议服务器');
  check('Declarative protocol server starts only on loopback and reports its assigned endpoint',
    mutations.at(-1).path.endsWith('/protocol-start') && mutations.at(-1).body.rules.length===1 &&
    await evaluate(`document.querySelector('.protocol-designer').textContent.includes('tcp://127.0.0.1:45678')`));
  await click('停止协议服务器');
  await click('启动设备模型');
  await click('前往2层');
  await wait(() => mutations.at(-1)?.path.endsWith('/equipment-control'), 'Equipment control request observed');
  check('Optional elevator/door model accepts a bounded target-floor command',
    mutations.at(-1).path.endsWith('/equipment-control') && mutations.at(-1).body.elevator.targetFloor==='2' && !!mutations.at(-1).body.sequence);
  await click('停止设备模型');
  const screenshot = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  const screenshotPath = resolve(profile, 'simulation-center.png'); writeFileSync(screenshotPath, Buffer.from(screenshot.data, 'base64')); console.log('Preview ' + screenshotPath);
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('故障处理')).click()`);
  await wait(() => evaluate(`document.querySelector('.fault-stage')?.offsetParent!==null`), 'Fault page');
  await evaluate(`(()=>{const e=document.querySelector('[aria-label="选择虚拟车辆"]');e.value='AGV-02';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  await click('断开通信');
  check('Fault targets the selected original vehicle and has a unique sequence', mutations.at(-1).body.vehicles['AGV-02'].offline === true && !!mutations.at(-1).body.sequence);
  await click('丢弃下一响应');
  check('Response loss is explicitly requested as a one-shot control', mutations.at(-1).body.vehicles['AGV-02'].dropNextResponse === true);
  await click('阻止完成许可');
  check('PLC fault request changes the device input model', mutations.at(-1).body.plc.suppressDone === true);
  await wait(() => evaluate(`(()=>{document.querySelector('.edge-controls').open=true;const input=document.querySelector('.edge-options input');if(!input||input.matches(':disabled'))return false;if(!input.checked)input.click();return input.checked})()`), 'Road selection enabled between polls');
  await click('应用道路阻塞');
  check('Road fault references the selected vehicle and original edge ID', mutations.at(-1).body.vehicles['AGV-02'].blockedEdges.join(',') === 'E1');
  await click('解除所选车全部道路阻塞');
  check('Road unblock clears only the selected vehicle edge list', mutations.at(-1).body.vehicles['AGV-02'].blockedEdges.length === 0);
  fresh = false; await click('刷新状态');
  check('Stale evidence disables device controls and keeps scenario controls on their own page',
    await disabled('暂停运动') && await disabled('注入设备故障') && !(await evaluate(`!!document.querySelector('.scenario-panel')`)));
  fresh = true; await click('刷新状态');
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('场景编排')).click()`);
  await wait(() => evaluate(`document.querySelector('.scenario-stage')?.offsetParent!==null`), 'Scenario page after fault checks');
  await evaluate(`(()=>{const e=document.querySelector('[aria-label="测试场景"]');e.value='simultaneous-calls';e.dispatchEvent(new Event('change',{bubbles:true}));const n=document.querySelector('.scenario-panel input');n.value='2';n.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  await click('运行场景');
  check('Scenario preserves selected kind and round count', mutations.at(-1).body.name === 'simultaneous-calls' && mutations.at(-1).body.rounds === 2);
  check('Running scenario prevents a second start', await disabled('运行场景'));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('监测与报告')).click()`);
  await wait(() => evaluate(`document.querySelector('.results-stage')?.offsetParent!==null`), 'Results page while scenario runs');
  check('A prior report is identified separately from the current running scenario', await evaluate(`document.querySelector('.report-identity').textContent.includes('上一场景报告')&&document.querySelector('.report-identity').textContent.includes('previous-scenario')&&document.querySelector('.report-panel').textContent.includes('当前场景仍在运行')`));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('场景编排')).click()`);
  await wait(() => evaluate(`document.querySelector('.scenario-stage')?.offsetParent!==null`), 'Return to scenario page');
  await click('中止后续场景');
  check('Abort does not mutate original tasks or clear evidence', mutations.at(-1).path.endsWith('/abort'));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('环境与地图')).click()`);
  await wait(() => evaluate(`document.querySelector('.environment-stage')?.offsetParent!==null`), 'Environment page before stop');
  check('A running environment cannot be replaced with a fresh instance', await disabled('沿用当前地图新建'));
  await click('停止环境');
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('运行总览')).click()`);
  await wait(() => evaluate(`document.querySelector('.overview-hero')?.textContent.includes('这个实例已经跑过')`), 'Historical instance guidance');
  check('A stopped instance with device history requires a fresh instance instead of silently failing to restart',
    await evaluate(`document.querySelector('.overview-hero').textContent.includes('需要新建一个干净环境')&&document.querySelector('.overview-hero button').textContent.includes('去新建干净实例')`));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('环境与地图')).click()`);
  await wait(() => evaluate(`document.querySelector('.environment-stage')?.offsetParent!==null`), 'Environment page for custom map');
  await evaluate(`(()=>{const map={name:'custom-mock',nodes:[
    {id:'PICK-A',agvSiteId:'LA',x:1,y:1,enabled:true},{id:'PICK-B',agvSiteId:'LB',x:2,y:1,enabled:true},
    {id:'WAIT',agvSiteId:'LW',x:3,y:1,enabled:true},{id:'DUMP',agvSiteId:'LD',x:4,y:1,enabled:true},
    {id:'PARK',agvSiteId:'LP',x:5,y:1,enabled:true}],
    edges:[{id:'E1',from:'PICK-A',to:'WAIT',bidirectional:true},{id:'E2',from:'WAIT',to:'DUMP',bidirectional:true},{id:'E3',from:'PICK-B',to:'WAIT',bidirectional:true},{id:'E4',from:'DUMP',to:'PARK',bidirectional:true}],
    resources:[{id:'RDUMP',capacity:1,boundary:[{x:3.5,y:.5},{x:4.5,y:.5},{x:4.5,y:1.5}]}]};
    const input=document.querySelector('.file-picker input');const transfer=new DataTransfer();transfer.items.add(new File([JSON.stringify(map)],'custom.agvmap.json',{type:'application/json'}));input.files=transfer.files;input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await wait(()=>evaluate(`document.querySelector('.map-import')?.textContent.includes('custom.agvmap.json')&&document.querySelectorAll('.vehicle-config-row').length===2`),'Custom map form loaded');
  await click('载入当前实例点表');
  await wait(()=>evaluate(`document.querySelector('.plc-table-summary')?.textContent.includes('mock-four-stations.csv')&&document.querySelector('.plc-table-summary')?.textContent.includes('aaaaaaaaaaaa')`),'Point table binding loaded');
  check('Scheme canvas requires an explicit PLC table and retains its source hash', await evaluate(`document.querySelector('.plc-table-binding').textContent.includes('3 工位')&&document.querySelector('.plc-table-binding').textContent.includes('${'a'.repeat(12)}')`));
  await click('增加隔离仿真工位');
  await evaluate(`(()=>{const row=document.querySelectorAll('.station-config-row')[2];const site=row.querySelector('select');site.value='PARK';site.dispatchEvent(new Event('change',{bubbles:true}));const priority=row.querySelectorAll('input')[1];priority.value='90';priority.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  check('Additional station requires explicit isolated point semantics and supports a dispatch priority', await evaluate(`document.querySelectorAll('.station-config-row').length===3&&document.querySelectorAll('.station-config-row')[2].textContent.includes('现场未确认')&&document.querySelectorAll('.station-config-row')[2].querySelectorAll('input')[1].value==='90'`));
  await evaluate(`(()=>{const e=document.querySelectorAll('.station-config-row')[2].querySelectorAll('select')[1];e.value='verified';e.dispatchEvent(new Event('change',{bubbles:true}))})()`);
  check('Unconfirmed field station cannot be saved as a verified PLC point-table mapping', await disabled('创建自定义隔离实例'));
  await evaluate(`document.querySelectorAll('.station-config-row')[2].querySelector('button').click()`);
  await click('增加曲线点');
  await evaluate(`(()=>{const rows=[...document.querySelectorAll('.vehicle-config-row')[0].querySelectorAll('.voltage-point')];const percents=[80,0,30];rows.forEach((row,index)=>{const input=row.querySelector('input');input.value=String(percents[index]);input.dispatchEvent(new Event('input',{bubbles:true}))})})()`);
  check('Battery voltage curve supports bounded custom points before submission',
    await evaluate(`document.querySelectorAll('.vehicle-config-row')[0].querySelectorAll('.voltage-point').length===3`));
  await click('创建自定义隔离实例');
  check('Imported map, stations, vehicles and dumper resource are submitted as one isolated configuration',
    mutations.at(-1).path.endsWith('/configure') && mutations.at(-1).body.mapDefinition.nodes.length===5 &&
    mutations.at(-1).body.stations.length===2 && mutations.at(-1).body.vehicles.length===2 &&
    mutations.at(-1).body.vehicles[0].lengthMeters===.8 && mutations.at(-1).body.vehicles[0].widthMeters===.52 &&
    mutations.at(-1).body.dumper.resourceId==='RDUMP' && mutations.at(-1).body.dumper.waitingSites[0]===mutations.at(-1).body.dumper.waitingSite);
  await evaluate(`(()=>{const input=document.querySelector('[aria-label="新实例车辆数"]');input.value='9';input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  check('Fresh instance vehicle count outside 1..8 is rejected in the form', await disabled('沿用当前地图新建'));
  await evaluate(`(()=>{const input=document.querySelector('[aria-label="新实例车辆数"]');input.value='2';input.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  await click('沿用当前地图新建');
  check('A stopped environment can prepare a fresh sibling without clearing old records', mutations.at(-1).path.endsWith('/new-instance'));
  await click('启动环境');
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('监测与报告')).click()`);
  await wait(() => evaluate(`document.querySelector('.results-stage')?.offsetParent!==null`), 'Results after fresh environment starts');
  check('Unknown report evidence is never styled as passed', await evaluate(`document.querySelector('.check-records .state-pill').textContent==='未核定'&&!document.querySelector('.check-records .state-pill').classList.contains('pass')`));
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('环境与地图')).click()`);
  await wait(() => evaluate(`document.querySelector('.environment-stage')?.offsetParent!==null`), 'Environment for saved scheme');
  await evaluate(`(()=>{const e=document.querySelector('.scheme-panel input');e.value='当前地图双车方案';e.dispatchEvent(new Event('input',{bubbles:true}))})()`);
  await click('保存当前方案');
  check('Scheme saves complete map, vehicle physics, workstation mapping and chosen scene', mutations.at(-1).path.endsWith('/scheme-save') && mutations.at(-1).body.name === '当前地图双车方案' && mutations.at(-1).body.configuration.vehicles.length === 2 && mutations.at(-1).body.configuration.mapDefinition.name === 'custom-mock' && mutations.at(-1).body.configuration.plcSignalTable.sha256 === pointTable.sha256 && mutations.at(-1).body.configuration.mapIdentity.sourceName === 'custom.agvmap.json' && mutations.at(-1).body.scenario.name === 'simultaneous-calls');
  check('Running environment prevents destructive scheme replacement', await disabled('一键开测') && await disabled('按方案新建实例'));
  await click('停止环境'); await click('按方案新建实例');
  check('Saved scheme creates a clean instance only after the current environment stops', mutations.at(-1).path.endsWith('/scheme-select') && mutations.at(-1).body.id === 'scheme-1');
  await click('一键开测');
  check('One-click testing submits only the selected saved scheme identity', mutations.at(-1).path.endsWith('/scheme-start') && mutations.at(-1).body.id === 'scheme-1');
  await sleep(2500);
  await wait(() => statusInflight === 0 && liveInflight === 0, 'Status polling idle');
  requestDelay = 2200; statusMaximum = 0; liveMaximum = 0; await sleep(6500);
  check('Slow polling has no overlapping status requests', statusMaximum <= 1 && liveMaximum === 1);
  requestDelay = 0; offline = true;
  await wait(() => evaluate(`document.querySelector('.simulation-error')?.textContent.includes('controller unavailable')`), 'Controller outage');
  await evaluate(`[...document.querySelectorAll('.stage-tabs button')].find(button=>button.textContent.includes('故障处理')).click()`);
  await wait(() => evaluate(`document.querySelector('.fault-stage')?.offsetParent!==null`), 'Fault page after controller outage');
  check('Controller outage blocks controls despite retained prior device data', await disabled('启动环境') && await disabled('恢复通信'));
  assert.deepEqual(errors, []); console.log('PASS simulation browser regression; mock HTTP only');
} finally { ws?.close(); child?.kill(); server.closeAllConnections(); await new Promise(ok => server.close(ok)); }
