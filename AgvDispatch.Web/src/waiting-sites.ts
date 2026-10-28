export interface WaitingSiteFields {
  waitingSite?: string;
  waitingSites?: readonly string[];
}

export function effectiveWaitingSites(configuration: WaitingSiteFields | null | undefined): string[] {
  if (configuration?.waitingSites?.length) return [...configuration.waitingSites];
  return configuration?.waitingSite ? [configuration.waitingSite] : [];
}

// Keep the rest of the saved profile intact, including vehicle pools and engineering identity.
export function withWaitingSites<T extends WaitingSiteFields & { dumpingSite: string }>(configuration: T, text: string): T {
  const sites = text.trim().split(/[,，;；\s]+/u).filter(Boolean);
  if (sites.length < 1 || sites.length > 1000) throw new Error('请填写1至1000个候车点，以逗号分隔。');
  if (new Set(sites.map(site => site.toUpperCase())).size !== sites.length) throw new Error('候车点不能重复，请核对后再保存。');
  if (sites.some(site => site.toUpperCase() === configuration.dumpingSite.trim().toUpperCase())) throw new Error('候车点不能与倒料作业点相同。');
  return { ...configuration, waitingSite: sites[0], waitingSites: sites };
}
