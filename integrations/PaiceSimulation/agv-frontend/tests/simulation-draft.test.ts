import assert from "node:assert/strict";
import test from "node:test";
import {
  clearDraftTasks,
  pickDraftStation,
  removeLastDraftTask,
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
