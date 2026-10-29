export type FleetOptions = {
  allowedVehicles: string[];
  configuredStations: string[];
  maxConcurrentTasks: number;
};

const key = (id: string) => id.toLocaleLowerCase('en-US');

// Initialize once. A deliberate empty selection stays empty during polling.
// Frozen original requests keep removed IDs so a retry cannot silently change intent.
export function reconcileFleetSelection(selected: string[], configured: string[], initialized: boolean, frozen = false) {
  const available = new Map(configured.map(id => [key(id), id]));
  const missing = selected.filter(id => !available.has(key(id)));
  return {
    selected: !initialized ? [...configured] : frozen ? [...selected] : selected.flatMap(id => {
      const current = available.get(key(id));
      return current == null ? [] : [current];
    }),
    missing
  };
}

export function fleetSelectionStatus(options: FleetOptions | null, vehicles: string[], stations: string[]) {
  const issues: string[] = [];
  if (!options || !Number.isSafeInteger(options.maxConcurrentTasks) || options.maxConcurrentTasks < 1)
    issues.push('后台尚未配置有效的并发任务名额');
  for (const [name, selected, configured] of [
    ['车辆', vehicles, options?.allowedVehicles ?? []],
    ['工位', stations, options?.configuredStations ?? []]
  ] as const) {
    if (selected.length < 2) issues.push(`至少选择两个${name}`);
    if (new Set(selected.map(key)).size !== selected.length) issues.push(`${name}选择有重复`);
    const allowed = new Set(configured.map(key));
    const missing = selected.filter(id => !allowed.has(key(id)));
    if (missing.length) issues.push(`${name}已不在当前配置中：${missing.join('、')}`);
  }
  return { valid: issues.length === 0, issues };
}

export function selectVisibleFleetOptions(selected: string[], visible: string[], checked: boolean) {
  const visibleKeys = new Set(visible.map(key));
  return checked
    ? [...selected, ...visible.filter(id => !selected.some(value => key(value) === key(id)))]
    : selected.filter(id => !visibleKeys.has(key(id)));
}

export function moveFleetStation(selected: string[], index: number, direction: number) {
  const next = [...selected], target = index + direction;
  if (index < 0 || index >= next.length || target < 0 || target >= next.length) return next;
  [next[index], next[target]] = [next[target]!, next[index]!];
  return next;
}

// A transport error or an ordinary read with no current run is not proof of non-acceptance.
// Only the server's matching, persisted retirement receipt permits forgetting an intent.
export function fleetStartRetirementConfirmed(requestId: string, response: unknown) {
  if (!response || typeof response !== 'object') return false;
  const receipt = response as Record<string, unknown>;
  return receipt.requestId === requestId && receipt.retired === true;
}

export async function retireUnacceptedFleetStart(pending: { requestId: string }, send: (requestId: string) => Promise<unknown>) {
  const receipt = await send(pending.requestId);
  if (!fleetStartRetirementConfirmed(pending.requestId, receipt))
    throw new Error('未取得原申请持久化注销回执，保留原参数并继续核对');
}

export type FleetStartIntent = {
  requestId: string; scenario: string; vehicleIds: string[]; siteConfirmed: boolean; stationIds?: string[];
};

// A read can acknowledge an uncertain start only when it identifies the original
// request and its complete plan. A different run or no run never proves rejection.
export function observeFleetStart(pending: FleetStartIntent, response: unknown): 'Accepted' | 'Mismatch' | 'Unresolved' {
  if (!response || typeof response !== 'object') return 'Unresolved';
  const current = (response as { current?: { plan?: FleetStartIntent } }).current;
  const plan = current?.plan;
  if (!plan || typeof plan.requestId !== 'string' || key(plan.requestId) !== key(pending.requestId)) return 'Unresolved';
  const same = (a: unknown, b: string[]) => Array.isArray(a) && a.length === b.length && a.every((id, i) => id === b[i]);
  return plan.scenario === pending.scenario && plan.siteConfirmed === pending.siteConfirmed &&
    same(plan.vehicleIds, pending.vehicleIds) && same(plan.stationIds ?? [], pending.stationIds ?? []) ? 'Accepted' : 'Mismatch';
}
