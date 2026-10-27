import type { CreationOptions } from './types';

// IDs and counts come from the active configuration, never from a sample installation.
export function businessChoices(options: CreationOptions | null, stations: string[]) {
  const rows = options?.stations ?? [];
  const selected = stations.map(id => rows.find(s => s.stationId === id));
  const valid = stations.length > 0 && new Set(stations).size === stations.length && selected.every(Boolean);
  const vehicles = (options?.vehicles ?? []).filter(v => selected.every(s =>
    s && (s.allowedVehicleIds == null || s.allowedVehicleIds.includes(v.vehicleId))));
  return { valid, vehicles };
}
export function standbyChoices(options: CreationOptions | null, vehicle: string) {
  return (options?.standbys ?? []).filter(s => s.allowedVehicleIds.includes(vehicle))
    .sort((a,b) => b.priority-a.priority || a.id.localeCompare(b.id));
}
