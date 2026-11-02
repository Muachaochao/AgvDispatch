export interface ChargingRequestIntent {
  requestId: string; vehicleId: string; requiredPositiveSeconds: number | null; returnSiteId: string | null;
}

export const chargingRequestStorageKey = 'charging-request-intent';

export function readChargingRequestIntent(raw: string | null): ChargingRequestIntent | undefined {
  if (!raw) return undefined;
  try {
    const value = JSON.parse(raw);
    if (!value || typeof value !== 'object' ||
      typeof value.requestId !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.requestId) ||
      typeof value.vehicleId !== 'string' || !value.vehicleId.trim() || value.vehicleId.length > 128 ||
      (value.requiredPositiveSeconds !== null && value.requiredPositiveSeconds !== 60) ||
      (value.returnSiteId !== null && (typeof value.returnSiteId !== 'string' || !value.returnSiteId.trim() || value.returnSiteId.length > 128))) return undefined;
    return { requestId: value.requestId, vehicleId: value.vehicleId, requiredPositiveSeconds: value.requiredPositiveSeconds, returnSiteId: value.returnSiteId };
  } catch { return undefined; }
}

export function findChargingRequestSession<T extends { requestId?: string | null }>(requestId: string, sessions: T[]) {
  const matches = sessions.filter(row => row.requestId === requestId);
  return matches.length === 1 ? matches[0] : undefined;
}
