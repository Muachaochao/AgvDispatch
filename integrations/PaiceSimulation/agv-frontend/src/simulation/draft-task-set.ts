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

export function mainDispatchStationId(node: {
  id: string;
  type: string;
}): string | null {
  if (node.type !== "WORKSTATION") return null;
  const match = /^(?:BUS-)?(CNC-(\d{3}))$/.exec(node.id);
  if (!match) return null;
  const number = Number(match[2]);
  return number >= 1 && number <= 305 ? match[1] : null;
}

export function toggleMainDispatchStation(
  stations: string[],
  stationId: string,
): string[] {
  return stations.includes(stationId)
    ? stations.filter((id) => id !== stationId)
    : [...stations, stationId];
}
