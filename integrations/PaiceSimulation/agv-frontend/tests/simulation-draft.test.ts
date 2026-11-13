import assert from "node:assert/strict";
import test from "node:test";
import {
  clearDraftTasks,
  mainDispatchStationId,
  pickDraftStation,
  removeLastDraftTask,
  toggleMainDispatchStation,
} from "../src/simulation/draft-task-set.ts";

test("two workstation clicks create one draft task", () => {
  let draft = { tasks: [], pendingFrom: null };
  draft = pickDraftStation(draft, "W1");
  draft = pickDraftStation(draft, "W2");
  assert.deepEqual(draft, {
    tasks: [{ id: 1, from: "W1", to: "W2" }],
    pendingFrom: null,
  });
});

test("clicking the same station keeps one pending endpoint", () => {
  const draft = pickDraftStation(
    { tasks: [], pendingFrom: "W1" },
    "W1",
  );
  assert.deepEqual(draft, { tasks: [], pendingFrom: "W1" });
});

test("new task ids continue after the highest existing id", () => {
  const draft = pickDraftStation(
    { tasks: [{ id: 7, from: "W1", to: "W2" }], pendingFrom: "W3" },
    "W4",
  );
  assert.equal(draft.tasks[1]?.id, 8);
});

test("remove and clear preserve task identity", () => {
  const tasks = [
    { id: 1, from: "W1", to: "W2" },
    { id: 2, from: "W2", to: "W3" },
  ];
  assert.deepEqual(removeLastDraftTask({ tasks, pendingFrom: "W4" }), {
    tasks: [{ id: 1, from: "W1", to: "W2" }],
    pendingFrom: "W4",
  });
  assert.deepEqual(clearDraftTasks(), { tasks: [], pendingFrom: null });
});

test("engineering simulation accepts only CNC workstation calls", () => {
  assert.equal(
    mainDispatchStationId({ id: "BUS-CNC-007", type: "WORKSTATION" }),
    "CNC-007",
  );
  assert.equal(
    mainDispatchStationId({ id: "CNC-305", type: "WORKSTATION" }),
    "CNC-305",
  );
  assert.equal(
    mainDispatchStationId({ id: "CAD2-J0012", type: "JUNCTION" }),
    null,
  );
  assert.equal(
    mainDispatchStationId({ id: "BUS-CNC-306", type: "WORKSTATION" }),
    null,
  );
});

test("engineering simulation toggles unique PLC call stations", () => {
  let stations: string[] = [];
  stations = toggleMainDispatchStation(stations, "BUS-CNC-001");
  stations = toggleMainDispatchStation(stations, "BUS-CNC-002");
  assert.deepEqual(stations, ["BUS-CNC-001", "BUS-CNC-002"]);
  stations = toggleMainDispatchStation(stations, "BUS-CNC-001");
  assert.deepEqual(stations, ["BUS-CNC-002"]);
});
