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

const child = spawn(browser, [
  '--headless=new',
  '--disable-gpu',
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
    captureBeyondViewport: true
  });
  writeFileSync(resolve(evidence, `${name}.png`), Buffer.from(result.data, 'base64'));
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
  await sleep(800);

  const state = await evaluate(`(() => ({
    theme: document.documentElement.dataset.theme,
    nodeCount: document.querySelectorAll('g[data-kind=node]').length,
    edgeCount: document.querySelectorAll('g[data-kind=edge]').length,
    coordinateCount: document.querySelectorAll('polygon[data-kind=resource]').length,
    message: document.querySelector('.workbench-message')?.textContent || '',
    title: document.querySelector('.workbench-title-copy small')?.textContent || '',
    layoutHeight: document.querySelector('.editor-layout')?.getBoundingClientRect().height || 0,
    treeRows: document.querySelectorAll('.editor-tree .el-tree-node').length,
    backgrounds: ['.workbench-title', '.editor-tree', '.editor-inspector', '.editor-options', '.backend-map-tools']
      .map(selector => ({ selector, color: getComputedStyle(document.querySelector(selector)).backgroundColor }))
  }))()`);

  check('light theme is active', state.theme === 'light');
  check('current active map loads automatically', state.nodeCount === 1112 && state.edgeCount === 1887 && state.coordinateCount === 1641);
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
  await screenshot('light-editor');

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

  writeFileSync(resolve(evidence, 'results.json'), JSON.stringify({ checks, errors }, null, 2));
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
