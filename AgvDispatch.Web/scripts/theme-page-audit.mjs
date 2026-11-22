import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const target = process.env.THEME_AUDIT_URL || 'http://127.0.0.1:5199/';
const theme = process.env.THEME_AUDIT_THEME === 'dark' ? 'dark' : 'light';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(target).hostname), 'Only loopback test servers are allowed');

const browser = process.env.THEME_AUDIT_BROWSER || [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe'
].find(existsSync);
assert.ok(browser, 'Set THEME_AUDIT_BROWSER to an installed Chromium browser');

const profile = resolve(root, '.codex-tmp', `theme-page-audit-${Date.now()}`);
const evidence = resolve(root, 'evidence', 'theme-audit');
mkdirSync(profile, { recursive: true });
mkdirSync(evidence, { recursive: true });

const child = spawn(browser, [
  '--window-position=-32000,-32000',
  '--window-size=1440,900',
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
const consoleMessages = [];
let activeRoute = '';

function rpc(method, params = {}) {
  return new Promise((resolvePromise, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Timeout ${method}`));
    }, 30000);
    pending.set(id, {
      resolve(value) { clearTimeout(timer); resolvePromise(value); },
      reject(error) { clearTimeout(timer); reject(error); }
    });
    socket.send(JSON.stringify({ id, method, params }));
  });
}

async function evaluate(expression) {
  const response = await rpc('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (response.exceptionDetails) throw new Error(response.exceptionDetails.exception?.description || response.exceptionDetails.text);
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

async function capture(name) {
  const response = await rpc('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  writeFileSync(resolve(evidence, `${name}.png`), Buffer.from(response.data, 'base64'));
}

async function scrollPage(selector, position = 'top') {
  await evaluate(`(() => {
    const target = document.querySelector(${JSON.stringify(selector)});
    if (!target) return false;
    let scroller = target;
    let current = target.parentElement;
    while (current) {
      const style = getComputedStyle(current);
      if (current.scrollHeight > current.clientHeight + 1 && /(auto|scroll)/.test(style.overflowY)) {
        scroller = current;
        break;
      }
      current = current.parentElement;
    }
    if (scroller === target) {
      scroller.scrollTop = ${position === 'bottom' ? 'scroller.scrollHeight' : '0'};
    } else {
      const targetRect = target.getBoundingClientRect();
      const scrollerRect = scroller.getBoundingClientRect();
      scroller.scrollTop += ${position === 'bottom'
        ? 'targetRect.bottom - scrollerRect.bottom'
        : 'targetRect.top - scrollerRect.top'};
    }
    return true;
  })()`);
  await sleep(180);
}

async function clickByText(selector, text) {
  return evaluate(`(() => {
    const button = [...document.querySelectorAll(${JSON.stringify(selector)})]
      .find(element => element.textContent.trim() === ${JSON.stringify(text)});
    if (!button || button.disabled) return false;
    button.click();
    return true;
  })()`);
}

async function openTaskCreateStep(step) {
  await waitFor(() => clickByText('button', '创建任务'), 'Task creation button did not become available');
  await waitFor(() => evaluate(`Boolean(document.querySelector('.task-create-drawer .creation-step-content'))`), 'Task creation drawer did not mount');
  if (step > 1) {
    await evaluate(`document.querySelectorAll('.creation-el-step')[${step - 1}]?.click()`);
    await waitFor(
      () => evaluate(`document.querySelector('.creation-step-content .creation-step-heading > .el-tag')?.textContent.includes(${JSON.stringify(`第 ${step} 步`)})`),
      `Task creation step ${step} did not open`
    );
  }
}

async function openTaskDetail() {
  await waitFor(
    () => evaluate(`(() => { const button = [...document.querySelectorAll('.history-presets button')].find(item => item.textContent.trim() === '全部时间'); return Boolean(button && !button.disabled); })()`),
    'Task history time-range controls did not become available'
  );
  await clickByText('.history-presets button', '全部时间');
  await waitFor(() => evaluate(`Boolean(document.querySelector('.task-table .el-table__row'))`), 'Task rows did not load');
  await waitFor(() => clickByText('.task-table .el-table__row button', '详情'), 'Task detail button was not available');
  await waitFor(() => evaluate(`Boolean(document.querySelector('.console-dialog .execution-detail'))`), 'Task execution detail did not load');
}

async function openMapDropdownItem(text) {
  await waitFor(() => evaluate(`Boolean(document.querySelector('.map-card .el-dropdown button'))`), 'Map actions did not load');
  await evaluate(`document.querySelector('.map-card .el-dropdown button')?.click()`);
  await waitFor(() => evaluate(`Boolean([...document.querySelectorAll('.el-dropdown-menu li')].find(item => item.textContent.includes(${JSON.stringify(text)})))`), `Map dropdown item ${text} did not open`);
  await clickByText('.el-dropdown-menu li', text);
  await waitFor(() => evaluate(`Boolean(document.querySelector('.console-dialog'))`), `Map dialog ${text} did not open`);
  if (text === '版本历史') await sleep(900);
}

async function openMapActivation() {
  await waitFor(() => evaluate(`Boolean([...document.querySelectorAll('.map-card footer button')].find(button => button.textContent.trim() === '激活' && !button.disabled))`), 'Enabled map activation button was not available');
  await clickByText('.map-card footer button', '激活');
  await waitFor(() => evaluate(`document.querySelector('.console-dialog')?.textContent.includes('激活地图版本')`), 'Map activation dialog did not open');
}

async function openAlarmDetail() {
  await waitFor(() => evaluate(`Boolean(document.querySelector('.alarm-table .el-table__row'))`), 'Alarm rows did not load');
  await waitFor(() => clickByText('.alarm-table .el-table__row button', '详情'), 'Alarm detail button was not available');
  await waitFor(() => evaluate(`Boolean(document.querySelector('.console-dialog .alarm-detail-hero'))`), 'Alarm detail dialog did not open');
}

async function openVehicleObstacle() {
  await waitFor(() => evaluate(`Boolean(document.querySelector('.vehicle-table .el-table__row .obstacle-summary'))`), 'Vehicle rows did not load');
  await evaluate(`document.querySelector('.vehicle-table .el-table__row .obstacle-summary')?.click()`);
  await waitFor(() => evaluate(`Boolean(document.querySelector('.console-dialog'))`), 'Vehicle obstacle dialog did not open');
}

async function openVehicleDisconnect() {
  await waitFor(() => evaluate(`Boolean(document.querySelector('.vehicle-table .el-table__row'))`), 'Vehicle rows did not load');
  await waitFor(() => clickByText('.vehicle-table .el-table__row button', '断开通信'), 'Vehicle disconnect button was not available');
  await waitFor(() => evaluate(`document.querySelector('.console-dialog')?.textContent.includes('确认断开通信')`), 'Vehicle disconnect dialog did not open');
}

async function openAlarmAcknowledge() {
  await waitFor(() => evaluate(`Boolean(document.querySelector('.alarm-table .el-table__row'))`), 'Alarm rows did not load');
  await waitFor(() => clickByText('.alarm-table .el-table__row button', '详情'), 'Alarm detail button was not available');
  await waitFor(() => evaluate(`document.querySelector('.console-dialog')?.textContent.includes('确认告警')`), 'Alarm acknowledgement button did not become available');
  await clickByText('.console-dialog footer button, .console-dialog .dialog-footer-actions button', '确认告警…');
  await waitFor(() => evaluate(`Boolean(document.querySelector('.console-dialog .ack-boundary'))`), 'Alarm acknowledgement dialog did not open');
}

async function confirmNavigationControl() {
  await waitFor(() => evaluate(`Boolean(document.querySelector('.navigation-control .nav-confirm input'))`), 'Navigation confirmation checkbox did not render');
  await evaluate(`document.querySelector('.navigation-control .nav-confirm input')?.click()`);
  await sleep(300);
}

const auditExpression = selector => `(() => {
  const root = document.querySelector(${JSON.stringify(selector)});
  if (!root) return { missing: true };
  const parse = value => {
    const match = String(value || '').match(/rgba?\\(([^)]+)\\)/);
    if (!match) return null;
    const parts = match[1].split(',').map(Number);
    return { r: parts[0], g: parts[1], b: parts[2], a: parts.length > 3 ? parts[3] : 1 };
  };
  const luminance = color => {
    const channel = value => {
      value /= 255;
      return value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4;
    };
    return .2126 * channel(color.r) + .7152 * channel(color.g) + .0722 * channel(color.b);
  };
  const blend = (top, bottom) => ({
    r: top.r * top.a + bottom.r * (1 - top.a),
    g: top.g * top.a + bottom.g * (1 - top.a),
    b: top.b * top.a + bottom.b * (1 - top.a),
    a: 1
  });
  const background = element => {
    let current = element;
    while (current) {
      const color = parse(getComputedStyle(current).backgroundColor);
      if (color && color.a > .98) return color;
      if (color && color.a > 0) {
        const parent = current.parentElement ? background(current.parentElement) : { r: 255, g: 255, b: 255, a: 1 };
        return blend(color, parent);
      }
      current = current.parentElement;
    }
    return { r: 255, g: 255, b: 255, a: 1 };
  };
  const ratio = (foreground, backdrop) => {
    const light = Math.max(luminance(blend(foreground, backdrop)), luminance(backdrop));
    const dark = Math.min(luminance(blend(foreground, backdrop)), luminance(backdrop));
    return (light + .05) / (dark + .05);
  };
  const label = element => {
    const own = [...element.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent.trim()).filter(Boolean).join(' ');
    return (element.getAttribute('aria-label') || own || element.textContent || '').trim().replace(/\\s+/g, ' ').slice(0, 120);
  };
  const inViewport = element => {
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.top < innerHeight;
  };
  const textSamples = [];
  const darkSurfaces = [];
  for (const element of root.querySelectorAll('*')) {
    if (!inViewport(element)) continue;
    const style = getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    const backgroundValue = parse(style.backgroundColor);
    const intentionalControl = element.closest('button, .el-button, .el-tag, .status-dot');
    if (backgroundValue && backgroundValue.a > .85 && rect.width * rect.height > 4000 && !intentionalControl && luminance(backgroundValue) < .16)
      darkSurfaces.push({ tag: element.tagName.toLowerCase(), className: String(element.className || '').slice(0, 120), background: style.backgroundColor, width: Math.round(rect.width), height: Math.round(rect.height) });
    const text = label(element);
    if (!text || ![...element.childNodes].some(node => node.nodeType === Node.TEXT_NODE && node.textContent.trim())) continue;
    const foreground = parse(style.color);
    if (!foreground || foreground.a < .2 || style.visibility === 'hidden' || style.display === 'none') continue;
    const backdrop = background(element);
    const contrast = ratio(foreground, backdrop);
    const size = Number.parseFloat(style.fontSize);
    const threshold = size >= 24 || size >= 18.66 && Number(style.fontWeight) >= 700 ? 3 : 4.5;
    if (contrast < threshold) textSamples.push({
      text,
      tag: element.tagName.toLowerCase(),
      className: String(element.className || '').slice(0, 120),
      color: style.color,
      background: 'rgb(' + [backdrop.r, backdrop.g, backdrop.b].map(Math.round).join(',') + ')',
      contrast: Number(contrast.toFixed(2)),
      fontSize: style.fontSize,
      fontWeight: style.fontWeight
    });
  }
  return {
    theme: document.documentElement.dataset.theme,
    layout: {
      viewportWidth: innerWidth,
      rootClientWidth: root.clientWidth,
      rootScrollWidth: root.scrollWidth,
      documentScrollWidth: document.documentElement.scrollWidth,
      horizontalOverflow: root.scrollWidth > Math.max(root.clientWidth, innerWidth) + 1 ||
        document.documentElement.scrollWidth > innerWidth + 1
    },
    lowContrastCount: textSamples.length,
    lowContrast: textSamples.slice(0, 40),
    darkSurfaceCount: darkSurfaces.length,
    darkSurfaces: darkSurfaces.slice(0, 20)
  };
})()`;

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
    if (response.method === 'Runtime.consoleAPICalled') {
      consoleMessages.push({
        type: response.params.type,
        text: response.params.args.map(argument => argument.value ?? argument.description ?? '').join(' ')
      });
    }
  });

  await rpc('Page.enable');
  await rpc('Runtime.enable');
  await rpc('Emulation.setFocusEmulationEnabled', { enabled: true });
  await rpc('Page.setWebLifecycleState', { state: 'active' });
  await rpc('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  const controlFixtureScript = process.env.THEME_AUDIT_CONTROL_FIXTURES === '1' ? `
    const originalFetch = window.fetch.bind(window);
    window.fetch = async (...request) => {
      const response = await originalFetch(...request);
      const raw = String(request[0]?.url || request[0] || '');
      const url = new URL(raw, location.origin);
      if (url.pathname === '/api/config' && response.ok) {
        const data = await response.clone().json();
        data.taskSubmissionEnabled = true;
        data.taskCancellationEnabled = true;
        data.recoveryActionsEnabled = true;
        data.alarmActionsEnabled = true;
        data.engineeringRuntimeApplyEnabled = true;
        return new Response(JSON.stringify(data), { status: response.status, headers: { 'Content-Type': 'application/json' } });
      }
      if (url.pathname === '/api/vehicles/configuration' && response.ok) {
        const data = await response.clone().json();
        data.activeVehicles = (data.activeVehicles || []).map((vehicle, index) => ({ ...vehicle, enabled: index === 0 }));
        return new Response(JSON.stringify(data), { status: response.status, headers: { 'Content-Type': 'application/json' } });
      }
      if (url.pathname === '/api/alarms' && response.ok) {
        const data = await response.clone().json();
        if (data.items?.[0]) data.items[0] = { ...data.items[0], status: 'Open', acknowledgedAt: null, acknowledgedBy: null, acknowledgeComment: null };
        if (data.summary) data.summary = { ...data.summary, open: 1, total: Math.max(1, data.summary.total || 1) };
        return new Response(JSON.stringify(data), { status: response.status, headers: { 'Content-Type': 'application/json' } });
      }
      return response;
    };
  ` : '';
  const navigationFixtureScript = process.env.THEME_AUDIT_NAV_FIXTURES === '1' ? `
    const navigationOriginalFetch = window.fetch.bind(window);
    const navigationFixture = () => {
      const query = location.hash.includes('?') ? location.hash.split('?')[1] : '';
      return new URLSearchParams(query).get('fixture') === 'paused' ? 'paused' : 'running';
    };
    const navigationRecord = () => {
      const paused = navigationFixture() === 'paused';
      const now = new Date().toISOString();
      return {
        taskId: 'NAVTEST-AUDIT-3001-3002',
        vendorTaskId: 'NAVTEST-AUDIT-3001-3002',
        vehicleId: 'AGV-01',
        sourceSite: 'LM34',
        targetSite: 'LM35',
        robotMap: 'ZM-3AGV',
        robotMapMd5: '29ea47ebbceccdb84d43b919fe96b352',
        state: paused ? 'Suspended' : 'Running',
        message: paused
          ? '原导航已暂停，并连续两次观察到车速接近零、定位有效且无严重报警；可继续原导航或取消。'
          : '原任务链状态 NAVTEST-AUDIT-3001-3002=2；仅全部终态且车辆静止时结束测试。',
        updatedAt: now,
        operator: '隔离验收',
        navigationApi: 3051,
        route: ['LM34', 'LM35'],
        targetX: -7.224,
        targetY: -2.196,
        currentStation: 'LM34',
        x: -5.7031,
        y: -1.1372,
        observedAt: now,
        blockedSince: null,
        vendorTaskIds: ['NAVTEST-AUDIT-3001-3002'],
        detour: null,
        autoDetour: null,
        cancellationAttemptedAt: null,
        cancellationVendorTaskId: null,
        pauseAttemptedAt: paused ? now : null,
        pauseVendorTaskId: paused ? 'NAVTEST-AUDIT-3001-3002' : null,
        pauseStoppedSamples: paused ? 2 : 0,
        pauseLastObservedAt: paused ? now : null,
        pauseStoppedAt: paused ? now : null,
        resumeAttemptedAt: null,
        resumeVendorTaskId: null
      };
    };
    window.fetch = async (...request) => {
      const raw = String(request[0]?.url || request[0] || '');
      const url = new URL(raw, location.origin);
      if (url.pathname === '/api/navigation-test') {
        return new Response(JSON.stringify({
          enabled: true,
          vehicleId: 'AGV-01',
          current: navigationRecord(),
          message: '隔离视觉验收数据；不会向车辆发送命令。',
          ready: true,
          blockReason: null,
          autoDetourEnabled: false,
          autoDetourWaitSeconds: 10
        }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (url.pathname === '/api/navigation-test/fork') {
        return new Response(JSON.stringify({
          supported: false, ready: false, blockReason: '隔离视觉验收不连接货叉。',
          vehicleId: 'AGV-01', telemetry: null, current: null
        }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (url.pathname === '/api/navigation-test/jack') {
        return new Response(JSON.stringify({
          ready: false, blockReason: '隔离视觉验收不连接顶升。',
          vehicleId: 'AGV-01', telemetry: null, current: null
        }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return navigationOriginalFetch(...request);
    };
    class NavigationAuditWebSocket {
      constructor() {
        this.readyState = 1;
        this.onopen = this.onmessage = this.onerror = this.onclose = null;
        const pushSnapshot = async () => {
          try {
            const response = await navigationOriginalFetch('/api/snapshot', { cache: 'no-store' });
            const data = await response.json();
            const now = new Date().toISOString();
            data.receivedAt = now;
            data.connected = true;
            data.source = 'backend';
            data.error = null;
            data.status = {
              ...(data.status || {}),
              generatedAt: now,
              agvs: (data.status?.agvs || []).map(vehicle => vehicle.vehicleId === 'AGV-01'
                ? {
                    ...vehicle,
                    connection: 'Online',
                    quality: 'Good',
                    blocked: false,
                    obstacle: { ...(vehicle.obstacle || {}), state: 'Clear', message: '隔离视觉验收：无障碍。' },
                    emergencyStopped: false,
                    lastSuccessfulPollAt: now,
                    updatedAt: now,
                    velocityX: 0,
                    velocityY: 0,
                    angularVelocity: 0
                  }
                : vehicle)
            };
            this.onmessage?.({ data: JSON.stringify(data) });
          } catch (error) {
            this.onerror?.(error);
          }
        };
        setTimeout(async () => {
          this.onopen?.(new Event('open'));
          await pushSnapshot();
          this.snapshotTimer = setInterval(pushSnapshot, 1000);
        }, 120);
      }
      close() {
        clearInterval(this.snapshotTimer);
        this.readyState = 3;
        this.onclose?.(new Event('close'));
      }
      send() {}
    }
    window.WebSocket = NavigationAuditWebSocket;
  ` : '';
  await rpc('Page.addScriptToEvaluateOnNewDocument', {
    source: `try { localStorage.setItem('agv-ui-theme', ${JSON.stringify(theme)}); } catch {} ${controlFixtureScript} ${navigationFixtureScript}`
  });

  const previousRoutes = [
    { key: 'recovery', path: '/#/recovery', selector: '.recovery-page', maxWait: '.recovery-panel' },
    { key: 'charging', path: '/#/charging', selector: '.charging-page', maxWait: '.charge-request-form' },
    { key: 'dispatch-pipeline', path: '/#/dispatch-pipeline?layer=traffic', selector: '.dispatch-pipeline-page', maxWait: '[data-testid=pipeline-record]' },
    {
      key: 'task-create-step1',
      path: '/#/tasks',
      selector: '.task-create-drawer',
      scrollSelector: '.creation-step-content',
      mountedSelector: '.task-history-page',
      prepare: () => openTaskCreateStep(1)
    },
    {
      key: 'task-create-step2',
      path: '/#/tasks',
      selector: '.task-create-drawer',
      scrollSelector: '.creation-step-content',
      mountedSelector: '.task-history-page',
      prepare: () => openTaskCreateStep(2)
    },
    {
      key: 'task-create-step3',
      path: '/#/tasks',
      selector: '.task-create-drawer',
      scrollSelector: '.creation-step-content',
      mountedSelector: '.task-history-page',
      prepare: () => openTaskCreateStep(3)
    },
    {
      key: 'engineering-common',
      path: '/#/engineering?tab=overview',
      selector: '.engineering-center',
      scrollSelector: '.engineering-center',
      maxWait: '.engineering-content',
      prepare: async () => {
        await waitFor(() => evaluate(`document.querySelector('.engineering-level-switch button.active')?.textContent.includes('常用配置')`), 'Common engineering level did not activate');
      }
    },
    {
      key: 'engineering-advanced',
      path: '/#/engineering?tab=nodes',
      selector: '.engineering-center',
      scrollSelector: '.engineering-center',
      maxWait: '.engineering-content',
      prepare: async () => {
        await waitFor(() => evaluate(`document.querySelector('.engineering-level-switch button.active')?.textContent.includes('高级配置')`), 'Advanced engineering level did not activate');
      }
    },
    { key: 'map-catalog', path: '/#/maps', selector: '.map-catalog-page', scrollSelector: '.map-catalog-page', maxWait: '.map-query-toolbar' }
  ];
  const detailRoutes = [
    {
      key: 'task-detail',
      path: '/#/tasks',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.task-history-page',
      prepare: openTaskDetail
    },
    {
      key: 'map-detail',
      path: '/#/maps',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.map-catalog-page',
      maxWait: '.map-card',
      prepare: () => openMapDropdownItem('地图详情')
    },
    {
      key: 'map-versions',
      path: '/#/maps',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.map-catalog-page',
      maxWait: '.map-card',
      prepare: () => openMapDropdownItem('版本历史')
    },
    {
      key: 'map-activate',
      path: '/#/maps',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.map-catalog-page',
      maxWait: '.map-card',
      prepare: openMapActivation
    },
    {
      key: 'engineering-runtime',
      path: '/#/engineering?tab=runtime',
      selector: '.engineering-embedded',
      scrollSelector: '.engineering-embedded',
      mountedSelector: '.engineering-center',
      maxWait: '.engineering-embedded'
    },
    {
      key: 'engineering-communication',
      path: '/#/engineering?tab=communication',
      selector: '.engineering-embedded',
      scrollSelector: '.engineering-embedded',
      mountedSelector: '.engineering-center',
      maxWait: '.engineering-embedded'
    }
  ];
  const operationalRoutes = [
    { key: 'alarm-list', path: '/#/alarms', selector: '.alarm-history-page', scrollSelector: '.alarm-history-page', maxWait: '.alarm-table .el-table__row' },
    {
      key: 'alarm-detail',
      path: '/#/alarms',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.alarm-history-page',
      maxWait: '.alarm-table .el-table__row',
      prepare: openAlarmDetail
    },
    { key: 'vehicle-list', path: '/#/vehicles', selector: '.management-page', scrollSelector: '.management-page', maxWait: '.vehicle-table .el-table__row' },
    {
      key: 'vehicle-obstacle',
      path: '/#/vehicles',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.management-page',
      maxWait: '.vehicle-table .el-table__row',
      prepare: openVehicleObstacle
    },
    {
      key: 'vehicle-disconnect',
      path: '/#/vehicles',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.management-page',
      maxWait: '.vehicle-table .el-table__row',
      prepare: openVehicleDisconnect
    },
    {
      key: 'alarm-acknowledge',
      path: '/#/alarms',
      selector: '.console-dialog',
      scrollSelector: '.dialog-content',
      mountedSelector: '.alarm-history-page',
      maxWait: '.alarm-table .el-table__row',
      prepare: openAlarmAcknowledge
    }
  ];
  const navigationControlRoutes = [
    {
      key: 'navigation-pause-control',
      path: '/#/navigation-test?fixture=running',
      selector: '.nav-test',
      scrollSelector: '.navigation-control',
      maxWait: '.navigation-control .pause-control',
      prepare: confirmNavigationControl
    },
    {
      key: 'navigation-resume-control',
      path: '/#/navigation-test?fixture=paused',
      selector: '.nav-test',
      scrollSelector: '.navigation-control',
      maxWait: '.navigation-control button',
      prepare: confirmNavigationControl
    }
  ];
  const allDetailRoutes = [...detailRoutes, ...operationalRoutes];
  const narrowRoutes = [...allDetailRoutes, ...navigationControlRoutes]
    .map(route => ({ ...route, key: `${route.key}-narrow`, narrow: true }));
  const routes = [...previousRoutes, ...allDetailRoutes, ...navigationControlRoutes, ...narrowRoutes]
    .filter(route => !process.env.THEME_AUDIT_ROUTES || process.env.THEME_AUDIT_ROUTES.split(',').includes(route.key));
  const results = [];
  for (const route of routes) {
    activeRoute = route.key;
    console.log(`AUDIT ${theme} ${route.key}`);
    await rpc('Emulation.setDeviceMetricsOverride', route.narrow
      ? { width: 390, height: 844, deviceScaleFactor: 1, mobile: false }
      : { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
    await evaluate(`[...document.querySelectorAll('button[aria-label="关闭弹窗"]')].forEach(button => button.click())`).catch(() => {});
    await sleep(120);
    const navigation = new URL(route.path, target);
    navigation.hash += `${navigation.hash.includes('?') ? '&' : '?'}_audit=${Date.now()}-${sequence}`;
    await rpc('Page.navigate', { url: navigation.href });
    await waitFor(() => evaluate(`Boolean(document.querySelector(${JSON.stringify(route.mountedSelector || route.selector)}))`), `${route.key} did not mount`);
    if (route.maxWait) await waitFor(() => evaluate(`Boolean(document.querySelector(${JSON.stringify(route.maxWait)}))`), `${route.key} data did not load`).catch(() => {});
    await sleep(700);
    if (route.prepare) await route.prepare();
    if (route.key === 'recovery') {
      await evaluate(`(() => {
        const row = document.querySelector('.recovery-table .el-table__row');
        row?.click();
        return Boolean(row);
      })()`);
      await sleep(800);
    }
    if (route.key.startsWith('navigation-')) {
      await evaluate(`document.querySelector('.navigation-control')?.scrollIntoView({ block: 'center', inline: 'nearest' })`);
      await sleep(250);
    } else {
      await scrollPage(route.scrollSelector || route.selector, 'top');
    }
    const topAudit = await evaluate(auditExpression(route.selector));
    await capture(`${route.key}-${theme}-top`);
    if (route.key.startsWith('navigation-')) {
      await evaluate(`document.querySelector('.navigation-control')?.scrollIntoView({ block: 'end', inline: 'nearest' })`);
      await sleep(250);
    } else {
      await scrollPage(route.scrollSelector || route.selector, 'bottom');
    }
    const bottomAudit = await evaluate(auditExpression(route.selector));
    await capture(`${route.key}-${theme}-bottom`);
    results.push({ route: route.key, top: topAudit, bottom: bottomAudit });
  }
  writeFileSync(resolve(evidence, `theme-page-audit-${theme}.json`), JSON.stringify({ capturedAt: new Date().toISOString(), target, theme, results, errors }, null, 2));
  console.log(JSON.stringify({ evidence, results, errors }, null, 2));
} catch (error) {
  await capture(`failure-${theme}-${activeRoute || 'startup'}`).catch(() => {});
  const diagnostic = await evaluate(`({ url: location.href, title: document.title, body: document.body?.innerText?.slice(0, 1200), html: document.documentElement.outerHTML.slice(0, 2000) })`).catch(reason => ({ diagnosticError: String(reason) }));
  writeFileSync(resolve(evidence, 'theme-page-audit-failure.txt'), JSON.stringify({ error: String(error.stack || error), theme, activeRoute, errors, consoleMessages, diagnostic }, null, 2));
  console.error(error);
  process.exitCode = 1;
} finally {
  if (socket?.readyState === 1) await rpc('Browser.close').catch(() => {});
  socket?.close();
  child.kill();
}
