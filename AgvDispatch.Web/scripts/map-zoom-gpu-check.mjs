import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const target = process.env.MAP_ZOOM_TEST_URL || 'http://127.0.0.1:5199/#/planning';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(target).hostname), 'Only loopback test servers are allowed');

const browser = process.env.MAP_ZOOM_TEST_BROWSER || [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe'
].find(existsSync);
assert.ok(browser, 'Set MAP_ZOOM_TEST_BROWSER to an installed Chromium browser');

const profile = resolve(root, '.codex-tmp', `map-zoom-gpu-${Date.now()}`);
const evidence = resolve(root, 'evidence', 'map-performance');
mkdirSync(profile, { recursive: true });
mkdirSync(evidence, { recursive: true });

const child = spawn(browser, [
  '--window-position=-32000,-32000',
  '--window-size=1486,900',
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-background-timer-throttling',
  '--disable-backgrounding-occluded-windows',
  '--disable-renderer-backgrounding',
  '--disable-features=CalculateNativeWinOcclusion',
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

function rpc(method, params = {}) {
  return new Promise((resolvePromise, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Timeout ${method}`));
    }, 30000);
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

async function waitFor(predicate, message, timeout = 45000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await sleep(100);
  }
  throw new Error(message);
}

function distribution(samples) {
  const frames = samples.filter(value => Number.isFinite(value) && value > 0);
  const sorted = [...frames].sort((left, right) => left - right);
  const percentile = value => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * value))] ?? null;
  const averageMs = frames.reduce((sum, value) => sum + value, 0) / Math.max(1, frames.length);
  return {
    count: frames.length,
    averageMs,
    averageFps: averageMs > 0 ? 1000 / averageMs : null,
    p95Ms: percentile(0.95),
    p99Ms: percentile(0.99),
    maxMs: sorted.at(-1) ?? null
  };
}

function median(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return sorted[Math.floor(sorted.length / 2)] ?? null;
}

async function idleSample() {
  const frames = await evaluate(`new Promise(resolve => {
    const frames = [];
    let last = 0;
    const startedAt = performance.now();
    const tick = time => {
      if (last) frames.push(time - last);
      last = time;
      if (performance.now() - startedAt < 400) requestAnimationFrame(tick);
      else resolve(frames);
    };
    requestAnimationFrame(tick);
  })`);
  return distribution(frames);
}

const fitMap = () => evaluate(`(() => {
  const fit = [...document.querySelectorAll('button')].find(button => button.textContent.trim() === '适配');
  fit?.click();
  return Boolean(fit);
})()`);
const readZoom = () => evaluate(`Number(document.querySelector('.editor-interaction-layer')?.dataset.zoom)`);

async function dispatchZoom(center, inputMode) {
  if (inputMode === 'cdp') {
    await rpc('Input.dispatchMouseEvent', {
      type: 'mouseWheel',
      x: center.x,
      y: center.y,
      deltaX: 0,
      deltaY: -80
    });
    return;
  }
  await evaluate(`(() => {
    const target = document.querySelector('.editor-interaction-layer');
    target?.dispatchEvent(new WheelEvent('wheel', {
      bubbles: true,
      cancelable: true,
      clientX: ${center.x},
      clientY: ${center.y},
      deltaX: 0,
      deltaY: -80,
      view: window
    }));
    return target?.tagName ?? null;
  })()`);
}

async function detectZoomInput(center) {
  const before = await readZoom();
  await dispatchZoom(center, 'cdp');
  await sleep(100);
  if (await readZoom() !== before) return 'cdp';
  return 'dom';
}

async function normalZoomSample() {
  await fitMap();
  await sleep(250);
  const center = await evaluate(`(() => {
    const rect = document.querySelector('.editor-interaction-layer').getBoundingClientRect();
    return { x: rect.x + rect.width / 2, y: Math.min(innerHeight - 40, rect.y + rect.height / 2) };
  })()`);
  const inputMode = await detectZoomInput(center);
  await fitMap();
  await sleep(250);
  const sampleStartZoom = await readZoom();
  await evaluate(`(() => {
    const frames = [];
    let last = 0;
    const sample = { frames, running: true };
    window.__mapZoomSample = sample;
    const tick = time => {
      if (last) frames.push(time - last);
      last = time;
      if (sample.running) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
    return true;
  })()`);
  for (let index = 0; index < 8; index += 1) {
    await dispatchZoom(center, inputMode);
    await sleep(32);
  }
  await sleep(260);
  const result = await evaluate(`(() => {
    const sample = window.__mapZoomSample;
    sample.running = false;
    const after = Number(document.querySelector('.editor-interaction-layer')?.dataset.zoom);
    const fit = [...document.querySelectorAll('button')].find(button => button.textContent.trim() === '适配');
    fit?.click();
    return { frames: sample.frames, before: ${sampleStartZoom}, after };
  })()`);
  return { ...distribution(result.frames), inputMode, zoomBefore: result.before, zoomAfter: result.after };
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
  await rpc('Emulation.setFocusEmulationEnabled', { enabled: true });
  await rpc('Page.setWebLifecycleState', { state: 'active' });
  await rpc('Emulation.setDeviceMetricsOverride', {
    width: 1486,
    height: 803,
    deviceScaleFactor: 1.5,
    mobile: false
  });
  await rpc('Page.navigate', { url: target });
  await waitFor(() => evaluate(`Boolean(document.querySelector('.editor-interaction-layer'))`), 'Planning workbench did not mount');
  await waitFor(() => evaluate(`document.querySelectorAll('g[data-kind=node]').length > 1000`), 'Map nodes did not load');
  await waitFor(() => evaluate(`document.querySelector('.editor-interaction-layer')?.dataset.labelIndex === 'worker'`), 'Label worker did not become ready');
  await waitFor(() => evaluate(`Number(document.querySelector('.map-static-canvas')?.dataset.renderEdgeBatches || 0) > 0`), 'Canvas detail layer did not render');
  await sleep(700);

  const idle = await idleSample();
  assert.ok((idle.averageFps ?? 0) >= 30, `Browser frame cadence is not active: ${JSON.stringify(idle)}`);

  const zoomSamples = [];
  for (let index = 0; index < 3; index += 1) {
    zoomSamples.push(await normalZoomSample());
    await sleep(300);
  }
  assert.ok(zoomSamples.every(sample => sample.zoomAfter > sample.zoomBefore * 2), 'Wheel benchmark did not change zoom');
  assert.equal(errors.length, 0);

  const result = {
    capturedAt: new Date().toISOString(),
    target,
    viewport: { width: 1486, height: 803, deviceScaleFactor: 1.5 },
    idle,
    zoomSamples,
    zoomP95Ms: median(zoomSamples.map(sample => sample.p95Ms)),
    zoomP99Ms: median(zoomSamples.map(sample => sample.p99Ms)),
    zoomAverageFps: median(zoomSamples.map(sample => sample.averageFps)),
    errors
  };
  writeFileSync(resolve(evidence, 'field-gpu-zoom.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
} catch (error) {
  writeFileSync(resolve(evidence, 'field-gpu-zoom-failure.txt'), String(error.stack || error));
  console.error(error);
  process.exitCode = 1;
} finally {
  if (socket?.readyState === 1) await rpc('Browser.close').catch(() => {});
  socket?.close();
  child.kill();
}
