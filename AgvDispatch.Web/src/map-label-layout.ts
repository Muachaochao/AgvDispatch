export type MapLabelMode = 'auto' | 'all' | 'none';
export type LabelRect = { left: number; top: number; right: number; bottom: number };
export type MapLabelInput = {
  id: string; x: number; y: number; width: number; height: number;
  kind: 'vehicle' | 'station' | 'area'; priority: number; focused?: boolean;
};
export type MapLabelPlacement = { id: string; rect: LabelRect; leader?: { x: number; y: number; length: number; angle: number } };
const intersects = (a: LabelRect, b: LabelRect, gap = 3) =>
  a.left < b.right + gap && a.right > b.left - gap && a.top < b.bottom + gap && a.bottom > b.top - gap;

// Keep each label close to its real node; omit lower-priority labels in dense views.
export function placeMapLabels(labels: MapLabelInput[], width: number, height: number,
  reserved: LabelRect[] = [], mode: MapLabelMode = 'auto'): MapLabelPlacement[] {
  const occupied = [...reserved], placed: MapLabelPlacement[] = [];
  const ordered = [...labels].filter(l => l.kind === 'vehicle' || mode !== 'none' || l.focused)
    .sort((a, b) => Number(!!b.focused) - Number(!!a.focused) || b.priority - a.priority || a.y - b.y || a.x - b.x || a.id.localeCompare(b.id));
  for (const l of ordered) {
    if (![l.x, l.y, l.width, l.height].every(Number.isFinite) || l.width < 1 || l.height < 1) continue;
    const w = l.width, h = l.height, g = l.kind === 'vehicle' ? 12 : 7;
    const candidates = l.kind === 'area'
      ? [[-w / 2, -h / 2], [-w / 2, -h - 10], [-w / 2, 10]]
      : l.kind === 'vehicle'
        ? [[-w / 2, -h - 20], [18, -h / 2], [-w - 18, -h / 2], [-w / 2, 22]]
        : [[g, -h / 2], [-w - g, -h / 2], [-w / 2, g], [-w / 2, -h - g],
          [g, g], [-w - g, g], [g, -h - g], [-w - g, -h - g]];
    if (l.kind !== 'area') for (const extra of [10, 20])
      candidates.push([g + extra, -h / 2], [-w - g - extra, -h / 2], [-w / 2, g + extra], [-w / 2, -h - g - extra]);
    let chosen: LabelRect | undefined;
    for (const [dx, dy] of candidates) {
      const r = { left: l.x + dx!, top: l.y + dy!, right: l.x + dx! + w, bottom: l.y + dy! + h };
      if (r.left < 5 || r.top < 5 || r.right > width - 5 || r.bottom > height - 5 || occupied.some(o => intersects(r, o))) continue;
      chosen = r; break;
    }
    if (!chosen && (l.focused || l.kind === 'vehicle' || (mode === 'all' && l.kind === 'station'))) {
      const left = Math.max(5, Math.min(width - w - 5, l.x + g)), top = Math.max(5, Math.min(height - h - 5, l.y - h / 2));
      chosen = { left, top, right: left + w, bottom: top + h };
    }
    if (!chosen) continue;
    occupied.push(chosen);
    const endX = Math.max(chosen.left, Math.min(chosen.right, l.x)), endY = Math.max(chosen.top, Math.min(chosen.bottom, l.y));
    const length = Math.hypot(endX - l.x, endY - l.y);
    placed.push({ id: l.id, rect: chosen,
      leader: l.kind !== 'area' && length > 16 && length <= 48 ? { x: l.x, y: l.y, length, angle: Math.atan2(endY - l.y, endX - l.x) } : undefined });
  }
  return placed;
}
