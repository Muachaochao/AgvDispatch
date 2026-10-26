export function summarizeBusinessBlocks(stations: { stationId: string; explanation: string }[]) {
  const groups = new Map<string, { reason: string; stations: string[]; count: number; permission: boolean }>();
  for (const station of stations) for (const issue of station.explanation.split('；').map(s => s.trim()).filter(Boolean)) {
    const permission = /^第\s*\d+\s*步[（(][^）)]*[）)]：PLC [^：]+：所属 PLC 写入开关未开启$/.test(issue);
    const reason = permission ? 'PLC 写入权限未开启' : issue;
    const group = groups.get(reason) ?? { reason, stations: [], count: 0, permission };
    if (!group.stations.includes(station.stationId)) group.stations.push(station.stationId);
    group.count++; groups.set(reason, group);
  }
  return [...groups.values()];
}
