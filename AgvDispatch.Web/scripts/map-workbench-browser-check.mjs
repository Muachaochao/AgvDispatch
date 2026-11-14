import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const target = process.env.MAP_WORKBENCH_TEST_URL || 'http://127.0.0.1:5199/#/planning';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(target).hostname), 'Only loopback test servers are allowed');

const browser = process.env.MAP_WORKBENCH_TEST_BROWSER || [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe'
].find(existsSync);
assert.ok(browser, 'Set MAP_WORKBENCH_TEST_BROWSER to an installed Chromium browser');

const profile = resolve(root, '.codex-tmp', `map-workbench-${Date.now()}`);
const evidence = resolve(root, 'evidence', 'map-workbench');
mkdirSync(profile, { recursive: true });
mkdirSync(evidence, { recursive: true });

const browserArguments = process.env.MAP_WORKBENCH_HEADFUL === '1'
  ? ['--window-position=-32000,-32000']
  : ['--headless=new'];
const child = spawn(browser, [
  ...browserArguments,
  '--ignore-gpu-blocklist',
  '--enable-gpu-rasterization',
  '--enable-zero-copy',
  '--no-first-run',
  '--no-default-browser-check',
  '--remote-debugging-address=127.0.0.1',
  '--remote-debugging-port=0',
  `--user-data-dir=${profile}`,
  'about:blank'
], { windowsHide: true, stdio: 'ignore' });

const sleep = milliseconds => new Promise(resolvePromise => setTimeout(resolvePromise, milliseconds));
let socket;
let sequence = 0;
const pending = new Map();
const errors = [];
const checks = [];
const performanceMetrics = {};

function rpc(method, params = {}) {
  return new Promise((resolvePromise, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Timeout ${method}`));
    }, 20000);
    pending.set(id, {
      resolve(value) {
        clearTimeout(timer);
        resolvePromise(value);
      },
      reject(error) {
        clearTimeout(timer);
        reject(error);
      }
    });
    socket.send(JSON.stringify({ id, method, params }));
  });
}

async function evaluate(expression) {
  const response = await rpc('Runtime.evaluate', {
    expression,
    returnByValue: true,
    awaitPromise: true
  });
  if (response.exceptionDetails) {
    throw new Error(response.exceptionDetails.exception?.description || response.exceptionDetails.text);
  }
  return response.result.value;
}

async function waitFor(predicate, message, timeout = 30000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await sleep(100);
  }
  throw new Error(message);
}

function check(name, condition) {
  assert.ok(condition, name);
  checks.push(name);
  console.log(`PASS ${name}`);
}

async function screenshot(name) {
  const result = await rpc('Page.captureScreenshot', {
    format: 'png',
    captureBeyondViewport: false
  });
  writeFileSync(resolve(evidence, `${name}.png`), Buffer.from(result.data, 'base64'));
}

async function startFrameSample() {
  await evaluate(`(() => {
    const sample = { frames: 0, startedAt: performance.now(), running: true };
    window.__mapFrameSample = sample;
    const frame = () => {
      if (!sample.running) return;
      sample.frames += 1;
      requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
  })()`);
}

async function finishFrameSample() {
  const sample = await evaluate(`(() => {
    const sample = window.__mapFrameSample;
    sample.running = false;
    return { frames: sample.frames, elapsed: performance.now() - sample.startedAt };
  })()`);
  return sample.elapsed > 0 ? sample.frames * 1000 / sample.elapsed : 0;
}

async function canvasCenter() {
  return evaluate(`(() => {
    const rect = document.querySelector('.editor-canvas').getBoundingClientRect();
    return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
  })()`);
}

async function measurePanFps() {
  await evaluate(`(() => [...document.querySelectorAll('button')].find(candidate => candidate.textContent.trim() === '平移')?.click())()`);
  await sleep(80);
  const center = await canvasCenter();
  const viewBefore = await evaluate('document.querySelector(".editor-canvas svg").dataset.viewBox');
  await rpc('Input.dispatchMouseEvent', { type: 'mousePressed', ...center, button: 'left', buttons: 1, clickCount: 1 });
  for (let index = 0; index < 8; index += 1) {
    await rpc('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: center.x + 3 + index * 1.5,
      y: center.y + 2 + index * 0.8,
      button: 'left',
      buttons: 1
    });
    await sleep(12);
  }
  await sleep(80);
  await startFrameSample();
  for (let index = 0; index < 32; index += 1) {
    const phase = index / 31 * Math.PI * 2;
    await rpc('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: center.x + 15 + Math.sin(phase) * 55 + index * 1.7,
      y: center.y + 8 + Math.sin(phase * 2) * 28 + index * 0.7,
      button: 'left',
      buttons: 1
    });
    await sleep(12);
  }
  const sawTransform = await evaluate('getComputedStyle(document.querySelector(".navigation-svg-layer")).visibility === "visible"');
  await rpc('Input.dispatchMouseEvent', { type: 'mouseReleased', ...center, button: 'left', buttons: 0, clickCount: 1 });
  await sleep(80);
  await evaluate(`(() => [...document.querySelectorAll('button')].find(candidate => candidate.textContent.trim() === '选择 / 框选')?.click())()`);
  return {
    fps: await finishFrameSample(),
    changed: viewBefore !== await evaluate('document.querySelector(".editor-canvas svg").dataset.viewBox'),
    sawTransform
  };
}

async function measureZoomFps() {
  const center = await canvasCenter();
  const zoomBefore = Number(await evaluate('document.querySelector(".editor-canvas svg").dataset.zoom'));
  await startFrameSample();
  let sawTransform = false;
  for (let index = 0; index < 20; index += 1) {
    await evaluate(`(() => {
      const target = document.querySelector('.editor-canvas svg');
      target.dispatchEvent(new WheelEvent('wheel', {
        deltaY: -90,
        clientX: ${center.x},
        clientY: ${center.y},
        bubbles: true,
        cancelable: true
      }));
    })()`);
    if (index === 5) sawTransform = await evaluate('getComputedStyle(document.querySelector(".navigation-svg-layer")).visibility === "visible"');
    await sleep(14);
  }
  await sleep(240);
  return {
    fps: await finishFrameSample(),
    changed: Number(await evaluate('document.querySelector(".editor-canvas svg").dataset.zoom')) > zoomBefore * 1.5,
    sawTransform
  };
}

function median(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return sorted[Math.floor(sorted.length / 2)] ?? 0;
}

try {
  await waitFor(() => existsSync(resolve(profile, 'DevToolsActivePort')), 'Browser did not expose its debug port');
  const port = readFileSync(resolve(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0];
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = pages.find(candidate => candidate.type === 'page');
  assert.ok(page, 'Browser page target missing');

  socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolvePromise, reject) => {
    socket.addEventListener('open', resolvePromise, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  socket.addEventListener('message', event => {
    const response = JSON.parse(event.data);
    if (response.id) {
      const request = pending.get(response.id);
      if (!request) return;
      pending.delete(response.id);
      response.error ? request.reject(new Error(JSON.stringify(response.error))) : request.resolve(response.result);
      return;
    }
    if (response.method === 'Runtime.exceptionThrown') {
      errors.push(response.params.exceptionDetails.exception?.description || response.params.exceptionDetails.text);
    }
  });

  await rpc('Page.enable');
  await rpc('Runtime.enable');
  await rpc('Emulation.setDeviceMetricsOverride', {
    width: 1680,
    height: 1100,
    deviceScaleFactor: 1,
    mobile: false
  });

  const origin = new URL(target).origin;
  await rpc('Page.navigate', { url: origin });
  await waitFor(() => evaluate('document.readyState === "complete"'), 'Application origin did not load');
  await evaluate('localStorage.setItem("agv-ui-theme", "light")');
  await rpc('Page.navigate', { url: target });
  await waitFor(() => evaluate('document.readyState === "complete"'), 'Planning route did not load');
  if (!await evaluate('location.hash.includes("/planning")')) {
    await evaluate('location.hash = "#/planning"');
  }
  await waitFor(() => evaluate('Boolean(document.querySelector(".planning-workbench"))'), 'Planning workbench did not mount');

  await waitFor(
    () => evaluate('document.querySelectorAll("g[data-kind=node]").length > 1000'),
    'The active backend map did not load its nodes',
    45000
  );
  await waitFor(
    () => evaluate('document.querySelector(".editor-canvas svg")?.dataset.labelIndex === "worker"'),
    'The label collision index did not move to its worker',
    15000
  );
  await waitFor(
    () => evaluate('Number(document.querySelector(".map-static-canvas")?.dataset.renderEdgeBatches || 0) > 0'),
    'The Canvas static layer did not complete its first render',
    15000
  );
  await sleep(800);

  const state = await evaluate(`(() => ({
    theme: document.documentElement.dataset.theme,
    nodeCount: document.querySelectorAll('g[data-kind=node]').length,
    edgeCount: Number(document.querySelector('.editor-canvas svg')?.dataset.renderEdges || 0),
    edgeBatches: Number(document.querySelector('.map-static-canvas')?.dataset.renderEdgeBatches || 0),
    canvasMode: document.querySelector('.map-static-canvas')?.dataset.renderMode || '',
    canvasLabels: Number(document.querySelector('.map-static-canvas')?.dataset.renderLabels || 0),
    coordinateCount: document.querySelectorAll('polygon[data-kind=resource]').length,
    countsText: document.querySelector('.workbench-tab-row > small')?.textContent || '',
    labelCount: Number(document.querySelector('.editor-canvas svg')?.dataset.visibleLabels || 0),
    labelIndex: document.querySelector('.editor-canvas svg')?.dataset.labelIndex || '',
    labelIndexMs: Number(document.querySelector('.editor-canvas svg')?.dataset.labelIndexMs || 0),
    arrowMarkers: Number(document.querySelector('.map-static-canvas')?.dataset.renderArrows || 0),
    message: document.querySelector('.workbench-message')?.textContent || '',
    title: document.querySelector('.workbench-title-copy small')?.textContent || '',
    layoutHeight: document.querySelector('.editor-layout')?.getBoundingClientRect().height || 0,
    treeRows: document.querySelectorAll('.editor-tree .el-tree-node').length,
    backgrounds: ['.workbench-title', '.editor-tree', '.editor-inspector', '.editor-options', '.backend-map-tools']
      .map(selector => ({ selector, color: getComputedStyle(document.querySelector(selector)).backgroundColor }))
  }))()`);
  console.log(`MAP_STATE ${JSON.stringify({ nodeCount: state.nodeCount, edgeCount: state.edgeCount, edgeBatches: state.edgeBatches, coordinateCount: state.coordinateCount, labelCount: state.labelCount, labelIndex: state.labelIndex, labelIndexMs: state.labelIndexMs })}`);

  check('light theme is active', state.theme === 'light');
  check('current active map loads automatically', state.nodeCount === 1112 && state.edgeCount === 1887 && state.countsText.includes('1112 航点') && state.countsText.includes('1887 道路') && state.countsText.includes('1641 共享区域'));
  check('identical road layers collapse into a small number of Canvas path batches', state.edgeBatches > 0 && state.edgeBatches <= 8);
  check('static map base renders through Canvas', state.canvasMode === 'detail' && state.canvasLabels === state.labelCount);
  check('zoomed-out label level keeps only a readable subset', state.labelCount > 0 && state.labelCount < state.nodeCount);
  check('label collision processing runs in the Web Worker', state.labelIndex === 'worker' && state.labelIndexMs >= 0);
  check('overview levels omit thousands of road arrow markers', state.arrowMarkers === 0);
  check('map identity is visible in the workbench header', state.title.includes('305CNC') && state.title.includes('B07-1F'));
  check('source switch no longer leaves the workbench blank', !state.message.includes('数据源已切换'));
  check('editor canvas receives a practical desktop height', state.layoutHeight >= 520);
  check('layer tree and backend tools are present', state.treeRows >= 3);
  check('light workbench surfaces no longer use the old dark backgrounds', state.backgrounds.every(item => {
    const channels = item.color.match(/\d+(?:\.\d+)?/g)?.slice(0, 3).map(Number) || [];
    return channels.length === 3 && Math.min(...channels) >= 220;
  }));

  const firstNodeId = await evaluate(`(() => {
    const node = document.querySelector('.editor-tree .el-tree-node__children .el-tree-node[data-key]') ||
      document.querySelector('.editor-tree .el-tree-node');
    node?.querySelector('.el-tree-node__content')?.click();
    return node?.getAttribute('data-key') || '';
  })()`);
  await sleep(200);
  const inspector = await evaluate(`(() => ({
    heading: document.querySelector('.inspector-heading h3')?.textContent || '',
    formItems: document.querySelectorAll('.inspector-form .el-form-item').length,
    elementSelects: document.querySelectorAll('.inspector-form .el-select').length,
    elementNumbers: document.querySelectorAll('.inspector-form .el-input-number').length
  }))()`);
  check('object tree selection opens the Element Plus inspector', Boolean(firstNodeId) && inspector.heading.includes('属性'));
  check('inspector uses Element Plus form controls', inspector.formItems > 0 && inspector.elementSelects > 0 && inspector.elementNumbers > 0);

  await evaluate(`(() => {
    const input = document.querySelector('.editor-tree > .el-input input');
    input.value = ${JSON.stringify(firstNodeId.split(':').at(-1)?.slice(0, 8) || 'CAD')};
    input.dispatchEvent(new Event('input', { bubbles: true }));
  })()`);
  await sleep(300);
  const searchState = await evaluate(`(() => ({
    visibleRows: [...document.querySelectorAll('.editor-tree .el-tree-node')].filter(node => node.getClientRects().length).length,
    hasMatchingLabel: [...document.querySelectorAll('.editor-tree .el-tree-node')].some(node => node.getClientRects().length && node.textContent.includes(${JSON.stringify(firstNodeId.split(':').at(-1)?.slice(0, 8) || 'CAD')})),
    canvasNodes: document.querySelectorAll('g[data-kind=node]').length
  }))()`);
  check('object tree search filters labels without replacing the canvas', searchState.visibleRows > 0 && searchState.hasMatchingLabel && searchState.canvasNodes === state.nodeCount);

  const panSamples = [];
  for (let index = 0; index < 3; index += 1) {
    panSamples.push(await measurePanFps());
    await evaluate(`(() => [...document.querySelectorAll('button')].find(candidate => candidate.textContent.trim() === '适配')?.click())()`);
    await sleep(250);
  }
  performanceMetrics.panFpsSamples = panSamples.map(sample => sample.fps);
  performanceMetrics.panFps = median(performanceMetrics.panFpsSamples);
  console.log(`PAN_FPS ${performanceMetrics.panFps.toFixed(1)}`);
  check('real map panning sustains the software-rendered interactive baseline', performanceMetrics.panFps >= 20);
  check('panning benchmark changes the visible map viewport', panSamples.every(sample => sample.changed));
  check('panning uses the lightweight navigation layer without full redraw', panSamples.every(sample => sample.sawTransform));

  const zoomSamples = [];
  for (let index = 0; index < 3; index += 1) {
    zoomSamples.push(await measureZoomFps());
    await evaluate(`(() => [...document.querySelectorAll('button')].find(candidate => candidate.textContent.trim() === '适配')?.click())()`);
    await sleep(250);
  }
  performanceMetrics.zoomFpsSamples = zoomSamples.map(sample => sample.fps);
  performanceMetrics.zoomFps = median(performanceMetrics.zoomFpsSamples);
  console.log(`ZOOM_FPS ${performanceMetrics.zoomFps.toFixed(1)}`);
  check('real map wheel zoom sustains an interactive frame rate', performanceMetrics.zoomFps >= 30);
  check('zoom benchmark changes the actual map scale', zoomSamples.every(sample => sample.changed));
  check('wheel zoom uses the lightweight navigation layer without full redraw', zoomSamples.every(sample => sample.sawTransform));
  const finalZoom = await measureZoomFps();
  check('detail screenshot uses the zoomed map state', finalZoom.changed);
  await screenshot('detail-editor');
  await evaluate(`(() => [...document.querySelectorAll('button')].find(candidate => candidate.textContent.trim() === '适配')?.click())()`);
  await sleep(250);
  await waitFor(
    () => evaluate(`(() => {
      const canvas = document.querySelector('.map-static-canvas');
      const svg = document.querySelector('.editor-canvas svg');
      return canvas?.dataset.renderMode === 'detail' &&
        Number(canvas?.dataset.renderLabels || 0) === Number(svg?.dataset.visibleLabels || 0);
    })()`),
    'Canvas did not return to the detailed overview layer',
    8000
  );
  const overviewCanvas = await evaluate(`(() => ({
    mode: document.querySelector('.map-static-canvas')?.dataset.renderMode || '',
    labels: Number(document.querySelector('.map-static-canvas')?.dataset.renderLabels || 0)
  }))()`);
  check('overview returns to the detailed Canvas layer with collision-filtered labels', overviewCanvas.mode === 'detail' && overviewCanvas.labels === state.labelCount);
  await screenshot('light-editor');

  for (let index = 0; index < 5; index += 1) {
    await evaluate('document.querySelector("[aria-label=\\"编辑地图放大\\"]")?.click()');
    await sleep(80);
  }
  await sleep(300);
  const detailState = await evaluate(`(() => ({
    labels: Number(document.querySelector('.editor-canvas svg')?.dataset.visibleLabels || 0),
    renderedNodes: Number(document.querySelector('.editor-canvas svg')?.dataset.renderNodes || 0),
    arrowMarkers: Number(document.querySelector('.map-static-canvas')?.dataset.renderArrows || 0),
    canvasLabels: Number(document.querySelector('.map-static-canvas')?.dataset.renderLabels || 0),
    canvasMode: document.querySelector('.map-static-canvas')?.dataset.renderMode || ''
  }))()`);
  console.log(`DETAIL_STATE ${JSON.stringify(detailState)}`);
  check('detail zoom reveals more labels and culls off-screen nodes', detailState.labels > state.labelCount && detailState.renderedNodes < state.nodeCount);
  check('detail zoom restores directional road arrows', detailState.arrowMarkers > 0);
  check('detail Canvas labels match the worker collision result', detailState.canvasMode === 'detail' && detailState.canvasLabels === detailState.labels);
  await evaluate(`(() => {
    const button = [...document.querySelectorAll('button')].find(candidate => candidate.textContent.trim() === '适配');
    button?.click();
  })()`);
  await sleep(300);

  await evaluate(`(() => {
    const button = [...document.querySelectorAll('button')].find(candidate => candidate.textContent.trim() === '切换地图');
    if (!button) throw new Error('Map switch button missing');
    button.click();
  })()`);
  await waitFor(() => evaluate('Boolean(document.querySelector(".map-picker-list article"))'), 'Map picker did not open');
  const picker = await evaluate(`(() => ({
    cards: document.querySelectorAll('.map-picker-list article').length,
    activeCards: document.querySelectorAll('.map-picker-list article.active').length,
    hasActiveTag: document.querySelector('.map-picker-list')?.textContent.includes('当前使用') || false
  }))()`);
  check('map picker shows the current map first and remains searchable', picker.cards > 0 && picker.activeCards === 1 && picker.hasActiveTag);
  await screenshot('light-map-picker');

  await evaluate('document.querySelector(".map-picker-dialog .el-dialog__headerbtn")?.click()');
  await sleep(200);
  await evaluate('localStorage.setItem("agv-ui-theme", "dark")');
  await rpc('Page.reload', { ignoreCache: true });
  await waitFor(
    () => evaluate('document.documentElement.dataset.theme === "dark" && document.querySelectorAll("g[data-kind=node]").length > 1000'),
    'The workbench did not reload in dark theme',
    45000
  );
  await sleep(600);
  await screenshot('dark-desktop');
  check('dark theme reloads the same active map', await evaluate('document.querySelectorAll("g[data-kind=node]").length === 1112'));
  check('no uncaught browser exceptions', errors.length === 0);

  writeFileSync(resolve(evidence, 'results.json'), JSON.stringify({ checks, errors, performanceMetrics }, null, 2));
  console.log(`PERFORMANCE ${JSON.stringify(performanceMetrics)}`);
  console.log(`PASS ${checks.length} map workbench browser checks; ${evidence}`);
} catch (error) {
  if (socket?.readyState === 1) await screenshot('failure').catch(() => {});
  writeFileSync(resolve(evidence, 'failure.txt'), String(error.stack || error));
  console.error(error);
  process.exitCode = 1;
} finally {
  if (socket?.readyState === 1) await rpc('Browser.close').catch(() => {});
  socket?.close();
  child.kill();
}
