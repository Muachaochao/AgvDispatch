export interface DraftTask {
  id: number;
  from: string;
  to: string;
}

export interface DraftTaskSet {
  tasks: DraftTask[];
  pendingFrom: string | null;
}

export function pickDraftStation(
  draft: DraftTaskSet,
  stationId: string,
): DraftTaskSet {
  if (draft.pendingFrom === null) {
    return { tasks: draft.tasks, pendingFrom: stationId };
  }
  if (draft.pendingFrom === stationId) {
    return { tasks: draft.tasks, pendingFrom: stationId };
  }
  const nextId =
    draft.tasks.reduce((highest, task) => Math.max(highest, task.id), 0) + 1;
  return {
    tasks: [
      ...draft.tasks,
      { id: nextId, from: draft.pendingFrom, to: stationId },
    ],
    pendingFrom: null,
  };
}

export function removeLastDraftTask(draft: DraftTaskSet): DraftTaskSet {
  return { tasks: draft.tasks.slice(0, -1), pendingFrom: draft.pendingFrom };
}

export function clearDraftTasks(): DraftTaskSet {
  return { tasks: [], pendingFrom: null };
}
