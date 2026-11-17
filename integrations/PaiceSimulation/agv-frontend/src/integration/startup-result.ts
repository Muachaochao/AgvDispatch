export function startupAccepted(
  value: { rootName?: string; phase?: string; startupPlan?: { schemeId?: string; status?: string } },
  previousRoot: string | undefined,
  schemeId: string,
) {
  return !!value.rootName && value.rootName !== previousRoot &&
    ['starting', 'running'].includes(value.phase ?? '') &&
    value.startupPlan?.schemeId === schemeId &&
    ['starting', 'submitted', 'ready'].includes(value.startupPlan?.status ?? '');
}
