import { createRequire } from "node:module";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const require = createRequire(path.join(root, "agv-frontend/package.json"));
const { chromium } = require("playwright");
const browser = await chromium.launch({ headless: true, channel: "msedge" });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
const output = path.join(root, ".runtime", "browser-check");
await mkdir(output, { recursive: true });
const errors = [],
  writes = [];
let fixtureMapId = null,
  fixtureTaskSetId = null;
page.on("pageerror", (e) => errors.push(String(e)));
let unavailable = false;
const definition = {
  name: "接口回归夹具（双车曲线）",
  unit: "m",
  coordinateSystem: "test",
  nodes: [
    { id: "A", agvSiteId: "LM1", x: 10, y: 20, type: "Station" },
    { id: "B", agvSiteId: "LM2", x: 14, y: 20, type: "Station" },
    { id: "C", agvSiteId: "LM3", x: 10, y: 24, type: "Station" },
  ],
  edges: [
    {
      id: "AB",
      from: "A",
      to: "B",
      distance: 6,
      speedLimit: 0.75,
      geometryKind: "CubicBezier",
      controlPoints: [
        { x: 10, y: 24 },
        { x: 14, y: 24 },
      ],
      bidirectional: true,
    },
    {
      id: "BC",
      from: "B",
      to: "C",
      distance: 6,
      speedLimit: 0.75,
      geometryKind: "Line",
      bidirectional: true,
    },
    {
      id: "CA",
      from: "C",
      to: "A",
      distance: 4,
      speedLimit: 0.75,
      geometryKind: "Line",
      bidirectional: true,
    },
  ],
  resources: [{ id: "R1", capacity: 1 }],
};
const task = {
  taskId: "ORIGINAL-1",
  vehicleId: "AGV-01",
  stationId: "CNC-001",
  stage: "Running",
};
const detail = {
  task,
  waitingExplanation: "等待原任务的倒料资源",
  steps: Array.from({ length: 9 }, (_, i) => ({
    sequence: i + 1,
    stepType: "Step-" + (i + 1),
    status: i === 2 ? "Running" : "Pending",
    waitingExplanation: i === 2 ? "等待倒料资源 R1" : "",
  })),
  commands: [
    {
      commandId: "ORIGINAL-COMMAND",
      status: "Sent",
      route: { nodeIds: ["B", "C"], edgeIds: ["BC"] },
    },
  ],
};
let sourceRunning = true;
await page.route("**/dispatch-api/**", async (route) => {
  if (unavailable)
    return route.fulfill({
      status: 503,
      contentType: "application/json",
      body: JSON.stringify({ error: "主工程测试断连" }),
    });
  const url = new URL(route.request().url()),
    p = url.pathname.replace("/dispatch-api", "");
  let data;
  if (route.request().method() === "POST") {
    const body = route.request().postDataJSON();
    writes.push({ p, body });
    if (p.endsWith("/stop")) sourceRunning = false;
    if (p.endsWith("/start")) sourceRunning = true;
  }
  if (p === "/api/maps")
    data = [
      {
        mapId: "active-site",
        name: "现场三车地图",
        unit: "m",
        isActive: true,
        publishedVersionId: "site-v1",
        updatedAt: "2026-11-13T10:00:00+08:00",
      },
      {
        mapId: "factory",
        name: "B07-1F·全厂305CNC·五车仿真图",
        description: "305个工位、5台AGV",
        unit: "m",
        isActive: false,
        publishedVersionId: "factory-v5",
        updatedAt: "2026-11-06T18:46:59+08:00",
      },
    ];
  else if (p === "/api/maps/factory/workspace")
    data = {
      project: {
        mapId: "factory",
        isActive: false,
        activeVersionId: null,
      },
      published: {
        mapId: "factory",
        versionId: "factory-v5",
        status: "Published",
        definition,
      },
    };
  else if (p === "/api/simulation" || route.request().method() === "POST")
    data = {
      enabled: true,
      running: sourceRunning,
      fresh: true,
      rootName: "isolated-two-car-test",
      map: definition,
      configuration: {
        mapIdentity: {
          mapId: "map",
          mapVersionId: "v1",
          definitionHash: "abc",
        },
      },
      device: {
        time: Date.now() / 1000,
        cars: {
          "AGV-01": {
            x: 10,
            y: 20,
            angle: Math.PI / 2,
            loaded: true,
            current_task: "original",
            waitReason: "等待倒料资源 R1",
          },
          "AGV-02": { x: 14, y: 20, angle: 0, moving: true },
        },
      },
      schemes: [{ id: "scheme-1", name: "双车方案", vehicleCount: 2 }],
      report: {
        id: "report-1",
        status: "inconclusive",
        metrics: { durationSeconds: 12 },
        checks: [{ name: "原任务核对", status: "inconclusive" }],
      },
    };
  else if (p === "/api/simulation/tasks") data = { items: [task] };
  else if (p === "/api/simulation/tasks/ORIGINAL-1") data = detail;
  else if (p === "/api/simulation/resources")
    data = [
      {
        resourceId: "R1",
        capacity: 1,
        occupiedCount: 1,
        leases: [{ vehicleId: "AGV-01", taskId: "ORIGINAL-1" }],
      },
    ];
  else return route.fulfill({ status: 404, body: "{}" });
  return route.fulfill({
    contentType: "application/json",
    body: JSON.stringify(data),
  });
});
try {
  await page.goto("http://127.0.0.1:5270/login");
  await page.locator("input").nth(0).fill("admin");
  await page.locator("input").nth(1).fill("admin123");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await page.waitForURL("**/dashboard");
  for (const [menu, url] of [
    ["车间地图", "roadnet"],
    ["任务集", "tasks"],
    ["仿真运行", "simulation"],
    ["对比实验", "experiments"],
  ]) {
    await page.getByRole("menuitem", { name: menu, exact: true }).click();
    await page.waitForURL("**/" + url);
    await page.waitForTimeout(300);
  }
  console.log(
    "PASS complete upstream navigation, native authentication and all core pages",
  );
  await page.getByRole("menuitem", { name: "主工程接入", exact: true }).click();
  await page.getByText("隔离仿真三维路网", { exact: true }).waitFor();
  await page
    .getByText("X 10.000 · Y 20.000 m · 90.0°", { exact: true })
    .waitFor();
  await page.getByText("等待倒料资源 R1", { exact: true }).first().waitFor();
  await page.locator(".dispatch-scene canvas").waitFor();
  assert.equal(await page.locator(".vehicle-row").count(), 2);
  await page.screenshot({
    path: path.join(output, "template-dispatch.png"),
    fullPage: false,
  });
  console.log(
    "PASS original template scene renders two observed vehicles, radians, waiting and resources",
  );
  await page
    .getByRole("button", { name: "读取 Web 仿真地图", exact: true })
    .click();
  await page
    .getByText("B07-1F·全厂305CNC·五车仿真图", { exact: true })
    .waitFor();
  await page
    .getByRole("button", { name: "保存仿真路网", exact: true })
    .waitFor();
  assert.equal(await page.locator(".map-import .el-alert--error").count(), 0);
  const mapSavedResponse = page.waitForResponse(
    (response) =>
      response.url().endsWith("/api/roadnet") &&
      response.request().method() === "POST",
  );
  await page.getByRole("button", { name: "保存仿真路网", exact: true }).click();
  fixtureMapId = (await (await mapSavedResponse).json()).data.id;
  await page
    .getByRole("button", { name: "已保存仿真路网", exact: true })
    .waitFor();
  console.log(
    "PASS source map conversion validates and persists in real independent Java database",
  );
  await page.evaluate(() =>
    Object.keys(localStorage)
      .filter((key) => key.startsWith("dispatch-import-"))
      .forEach((key) => localStorage.removeItem(key)),
  );
  await page.locator(".tasks-panel .el-table__row .el-checkbox").click();
  await page
    .getByRole("button", { name: "预览选中命令为实验任务", exact: true })
    .click();
  await page.waitForTimeout(500);
  if ((await page.getByRole("dialog").count()) === 0)
    console.error("Task import alerts:", await page.locator(".el-alert").allTextContents());
  await page.getByRole("dialog").waitFor();
  const tasksSavedResponse = page.waitForResponse(
    (response) =>
      response.url().endsWith("/api/taskset") &&
      response.request().method() === "POST",
  );
  await page
    .getByRole("button", { name: "确认保存实验任务集", exact: true })
    .click();
  fixtureTaskSetId = (await (await tasksSavedResponse).json()).data.id;
  await page
    .getByRole("button", { name: "已保存任务集", exact: true })
    .waitFor();
  console.log(
    "PASS original command becomes an identity-mapped native experimental task set",
  );
  const compared = await page.evaluate(async () => {
    const headers = {
      Authorization: "Bearer " + localStorage.getItem("agv_token"),
      "Content-Type": "application/json",
    };
    const maps = (await (await fetch("/api/roadnet/list", { headers })).json())
      .data;
    const map = maps.filter((m) => m.name.includes("接口回归夹具")).at(-1);
    const sets = (await (await fetch("/api/taskset/list", { headers })).json())
      .data;
    const set = sets.filter((s) => s.roadnetId === map.id).at(-1);
    const response = await fetch("/api/experiment/compare", {
      method: "POST",
      headers,
      body: JSON.stringify({
        roadnetId: map.id,
        taskSetId: set.id,
        agvCount: 2,
        seed: 20261113,
      }),
    });
    return (await response.json()).data;
  });
  assert.equal(compared.length, 2);
  assert.ok(compared.every((row) => row.status === "FINISHED"));
  console.log(
    "PASS both native algorithms finish on the imported graph and command task set",
  );

  await page.locator(".el-dialog__headerbtn").click();
  await page.getByRole("button", { name: "运行业务场景", exact: true }).click();
  await page.waitForTimeout(200);
  assert.equal(writes.at(-1).p, "/api/simulation/run");
  assert.equal(writes.at(-1).body.name, "normal-cycle");
  unavailable = true;
  await page.getByText("主工程测试断连", { exact: false }).first().waitFor();
  assert.equal(
    await page
      .getByRole("button", { name: "运行业务场景", exact: true })
      .isDisabled(),
    true,
  );
  console.log(
    "PASS explicit control uses isolated route and source outage disables writes",
  );
  await page.getByRole("menuitem", { name: "仿真运行", exact: true }).click();
  await page.waitForTimeout(600);
  assert.equal(await page.locator(".toolbar").count(), 1);
  assert.deepEqual(errors, []);
  await writeFile(
    path.join(output, "result.json"),
    JSON.stringify(
      {
        passed: true,
        source:
          "mock main engineering API; real independent Java persistence and native UI",
        writes,
      },
      null,
      2,
    ),
  );
} finally {
  await page
    .evaluate(
      async ({ mapId, taskSetId }) => {
        const headers = {
          Authorization: "Bearer " + localStorage.getItem("agv_token"),
        };
        if (taskSetId)
          await fetch("/api/taskset/" + taskSetId, {
            method: "DELETE",
            headers,
          });
        if (mapId)
          await fetch("/api/roadnet/" + mapId, { method: "DELETE", headers });
      },
      { mapId: fixtureMapId, taskSetId: fixtureTaskSetId },
    )
    .catch(() => {});
  await browser.close();
}
