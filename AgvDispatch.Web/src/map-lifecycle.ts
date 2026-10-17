// WebHost serializes enums as names; accept numeric legacy responses explicitly.
export function isMapSwitchPhase(value: unknown, phase: 10 | 20 | 90 | 100): boolean {
  const names = { 10: 'Draining', 20: 'AwaitingResume', 90: 'Completed', 100: 'Cancelled' } as const;
  return value === phase || value === names[phase];
}
