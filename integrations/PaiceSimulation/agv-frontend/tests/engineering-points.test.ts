import test from "node:test";
import assert from "node:assert/strict";
import { inspectEngineeringPoints, readPlcSignals } from "../src/integration/engineering-points.ts";

test("finds missing, disabled and repeated engineering point assignments", () => {
  const points = new Map([
    ["A", { id: "A", x: 0, y: 1 }],
    ["B", { id: "B", x: 2, y: 3, enabled: false }],
  ]);
  const items = [
    { label: "AGV-01", nodeId: "A", group: "vehicle" },
    { label: "AGV-02", nodeId: "A", group: "vehicle" },
    { label: "CNC-01", nodeId: "B", group: "station" },
    { label: "倒料点", nodeId: "C", group: "dumper" },
  ];
  assert.deepEqual(inspectEngineeringPoints(points, items).map(issue => [issue.label, issue.reason]), [
    ["AGV-01", "与 AGV-02 共用点位"],
    ["AGV-02", "与 AGV-01 共用点位"],
    ["CNC-01", "地图点位已停用"],
    ["倒料点", "地图点位不存在"],
  ]);
});

test("reads exact PLC addresses and direction from the saved CSV, including quoted fields", () => {
  assert.deepEqual(readPlcSignals(
    '\uFEFFstationId,address,variableName,direction,access,enabled,sourceNote\r\n' +
    'CNC-001,DB1.DBX12.0,FullBoxRequested,PlcToHost,Read,true,"note, with comma\nand newline"\r\n'
  ).get("CNC-001"), [
    { address: "DB1.DBX12.0", name: "FullBoxRequested", direction: "PlcToHost", access: "Read", enabled: true },
  ]);
});
