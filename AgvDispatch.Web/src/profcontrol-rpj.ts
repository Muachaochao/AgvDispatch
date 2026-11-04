import type { SimulationEdge, SimulationMapDefinition, SimulationNode, SimulationResource } from './simulation.ts';

type JsonObject = Record<string, unknown>;

export interface RpjVehicle {
  id: string;
  name: string;
  widthMeters: number;
  heightMeters: number;
  online: boolean;
}

export interface RpjImportResult {
  mapDefinition: SimulationMapDefinition;
  sourceVehicles: RpjVehicle[];
  signalGenerators: { name: string; signalCount: number }[];
  unsupported: Record<string, number>;
  warnings: string[];
}

const object = (value: unknown): JsonObject => value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {};
const array = <T = unknown>(value: unknown): T[] => Array.isArray(value) ? value as T[] : [];
const text = (value: unknown): string => typeof value === 'string' ? value.replace(/^"+|"+$/g, '') : value == null ? '' : String(value);
const number = (value: unknown, fallback = 0): number => {
  const raw = text(value);
  if (!raw.trim()) return fallback;
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : fallback;
};
const bool = (value: unknown): boolean => text(value).toLowerCase() === 'true';
const nested = (value: unknown): unknown => {
  if (typeof value !== 'string' || !value.trim()) return value;
  try { return JSON.parse(value); } catch { return value; }
};
const pointText = (value: unknown): { x: number; y: number } | null => {
  const match = text(value).trim().replace(/^\(|\)$/g, '').match(/^\s*([-+.\dEe]+)\s*,\s*([-+.\dEe]+)\s*$/);
  if (!match) return null;
  const x = Number(match[1]), y = Number(match[2]);
  return Number.isFinite(x) && Number.isFinite(y) ? { x, y } : null;
};

export function importProfControlRpj(source: string, sourceName = 'ProfControl project'): RpjImportResult {
  let project: JsonObject;
  try { project = object(JSON.parse(source.replace(/^\uFEFF/, ''))); }
  catch (error) { throw new Error('ProfControl .rpj 不是有效JSON：' + (error instanceof Error ? error.message : String(error))); }
  if (!Array.isArray(project.ItemsInNet)) throw new Error('.rpj 缺少 ItemsInNet。');
  const scale = number(object(project.Global).Scale, 0.01);
  if (!(scale > 0) || scale > 1000) throw new Error('.rpj 坐标比例无效。');
  const convert = (point: { x: number; y: number }) => ({ x: +(point.x * scale).toFixed(6), y: +(point.y * scale).toFixed(6) });
  const nodes: SimulationNode[] = [];
  const edges: SimulationEdge[] = [];
  const resources: SimulationResource[] = [];
  const coordinateNodes = new Map<string, string>();
  const usedIds = new Set<string>();
  const unsupported: Record<string, number> = {};
  const warnings: string[] = [];
  const sourceVehicles: RpjVehicle[] = [];
  const signalGenerators: { name: string; signalCount: number }[] = [];
  let sequence = 0;

  const safeId = (prefix: string) => {
    let candidate = `${prefix}-${String(++sequence).padStart(4, '0')}`;
    while (usedIds.has(candidate)) candidate += '-x';
    usedIds.add(candidate);
    return candidate;
  };
  const addNode = (id: string, site: string, point: { x: number; y: number }, type: string) => {
    const world = convert(point);
    const existing = coordinateNodes.get(`${world.x.toFixed(5)},${world.y.toFixed(5)}`);
    if (existing) return existing;
    let unique = id || safeId('RPJ-NODE');
    while (usedIds.has(unique)) unique += '-N';
    usedIds.add(unique);
    coordinateNodes.set(`${world.x.toFixed(5)},${world.y.toFixed(5)}`, unique);
    nodes.push({ id: unique, agvSiteId: site || unique, x: world.x, y: world.y, type, enabled: true, resourceIds: [] });
    return unique;
  };
  const endpoint = (value: unknown, type = 'Waypoint') => {
    const parsed = pointText(value);
    return parsed ? addNode('', '', parsed, type) : null;
  };
  const addEdge = (from: string | null, to: string | null, id: string, bidirectional: boolean, speed: number,
                    geometryKind = 'Line', controlPoints: { x: number; y: number }[] = []) => {
    if (!from || !to || from === to) return;
    const key = `${from}|${to}|${geometryKind}|${controlPoints.map(point => `${point.x},${point.y}`).join(';')}`;
    if (edges.some(edge => edge.id === id) || edges.some(edge => `${edge.from}|${edge.to}|${edge.geometryKind}|${(edge.controlPoints ?? []).map(point => `${point.x},${point.y}`).join(';')}` === key)) return;
    edges.push({ id, from, to, enabled: true, bidirectional, speedLimit: speed > 0 ? speed : 0.5,
      geometryKind, controlPoints });
  };

  const networks = array<JsonObject>(project.ItemsInNet).flatMap(item => array<JsonObject>(item.network));
  for (const objectItem of networks) {
    const view = object(objectItem.view), model = object(objectItem.model), type = text(view.Type);
    if (type === 'StationView') {
      const center = pointText(view.Center);
      if (!center) { warnings.push('忽略缺少中心坐标的站点。'); continue; }
      const site = text(model.MarkPointValueMirror) || text(model.ID) || '';
      const stationId = site ? `LM-${site.replace(/[^\w-]/g, '_')}` : safeId('RPJ-STATION');
      addNode(stationId, site, center, 'Station');
    } else if (type === 'AgvView') {
      const name = text(model.Name) || safeId('AGV');
      sourceVehicles.push({ id: name, name, widthMeters: number(model.Width, 1), heightMeters: number(model.Height, 1),
        online: bool(model.OnLine) });
    } else if (type === 'SignalGeneratorView') {
      const signals = object(nested(model.Signals));
      signalGenerators.push({ name: text(model.Name), signalCount: array(signals.SignalType).length || number(model.SignalCount) });
    }
  }

  for (const objectItem of networks) {
    const view = object(objectItem.view), model = object(objectItem.model), type = text(view.Type);
    if (type === 'StationView' || type === 'AgvView') continue;
    if (!['LineView','BezierView','PolygonView','ControlAreaView','SignalGeneratorView'].includes(type)) {
      unsupported[type || 'Unknown'] = (unsupported[type || 'Unknown'] ?? 0) + 1;
      continue;
    }
    if (type === 'SignalGeneratorView') { unsupported[type] = (unsupported[type] ?? 0) + 1; continue; }
    const bidirectional = text(view.Direction || model.RotateDirection).toLowerCase() !== 'forward';
    const enabled = !('RouteEnable' in view) || bool(view.RouteEnable);
    const speed = number(model.AgvSpeed, 0.5);
    const baseId = text(model.ID) || text(view.ID) || safeId('RPJ');
    if (type === 'LineView') {
      if (!enabled) continue;
      addEdge(endpoint(view.P1), endpoint(view.P2), `RPJ-${baseId}`, bidirectional, speed);
    } else if (type === 'BezierView') {
      if (!enabled) continue;
      const from = endpoint(view.P1), to = endpoint(view.P2);
      const c1 = pointText(view.C1), c2 = pointText(view.C2);
      addEdge(from, to, `RPJ-${baseId}`, bidirectional, speed, 'CubicBezier', c1 && c2 ? [convert(c1), convert(c2)] : []);
    } else if (type === 'ControlAreaView') {
      const points = array<string>(nested(view.Points)).map(pointText).filter((point): point is { x: number; y: number } => !!point).map(convert);
      if (points.length >= 3) resources.push({ id: `R-${baseId}`, capacity: Math.max(1, number(model.CollisionMaxCapacity, 1)), boundary: points });
    } else if (type === 'PolygonView') {
      if (!enabled) continue;
      const chips = array<JsonObject>(object(nested(view.Polygons)).Polygons);
      let previous = chips.length ? endpoint(chips[0].P1) : null;
      chips.forEach((chip, index) => {
        const kind = text(chip.ChipType), p1 = endpoint(chip.P1), p2 = endpoint(chip.P2);
        if (kind === 'CubicClip') {
          const c1 = pointText(chip.C1), c2 = pointText(chip.C2);
          addEdge(previous ?? p1, p2, `RPJ-${baseId}-${index}`, bidirectional, speed, 'CubicBezier', c1 && c2 ? [convert(c1), convert(c2)] : []);
        } else if (kind === 'ArcClip') {
          const center = pointText(chip.Center), start = pointText(chip.P1), end = pointText(chip.P2);
          if (!center || !start || !end) addEdge(previous ?? p1, p2, `RPJ-${baseId}-${index}`, bidirectional, speed);
          else {
            const radius = number(chip.Radius, Math.hypot(start.x-center.x, start.y-center.y));
            const startAngle = Math.atan2(start.y-center.y, start.x-center.x);
            const endAngle = Math.atan2(end.y-center.y, end.x-center.x);
            let sweep = endAngle - startAngle;
            if (bool(chip.ArcOtherSide)) sweep = sweep > 0 ? sweep - Math.PI * 2 : sweep + Math.PI * 2;
            else while (sweep < 0) sweep += Math.PI * 2;
            let prior = previous ?? p1;
            for (let step = 1; step <= 24; step++) {
              const angle = startAngle + sweep * step / 24;
              const sampled = addNode('', '', { x: center.x + Math.cos(angle) * radius, y: center.y + Math.sin(angle) * radius }, 'Waypoint');
              addEdge(prior, sampled, `RPJ-${baseId}-${index}-${step}`, bidirectional, speed);
              prior = sampled;
            }
          }
        } else addEdge(previous ?? p1, p2, `RPJ-${baseId}-${index}`, bidirectional, speed);
        previous = p2;
      });
    }
  }
  if (!nodes.length) throw new Error('.rpj 中没有可导入的站点或路径节点。');
  if (!edges.length) warnings.push('没有导入可调度路径；仅站点或控制区可用，不能直接运行运输任务。');
  if (sourceVehicles.length > 8) warnings.push(`源项目有 ${sourceVehicles.length} 辆车，仿真页面最多保留前8辆元数据。`);
  if (unsupported.SignalGeneratorView) warnings.push('信号发生器只导入元数据；运行信号需在仿真页另行配置。');
  return {
    mapDefinition: { name: sourceName.replace(/\.rpj$/i, '') || 'ProfControl import', nodes, edges, resources },
    sourceVehicles: sourceVehicles.slice(0, 8), signalGenerators, unsupported, warnings
  };
}
