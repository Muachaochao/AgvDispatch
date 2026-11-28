"""Derive simulation stopping points on straight lanes, without moving CAD stations.

This checks the known graph and station envelopes, not unmodelled site equipment.
Never apply the result to a real robot map without physical clearance review.
"""
import argparse
import copy
import hashlib
import json
import math
from collections import defaultdict
from pathlib import Path


def localize_split_intersections(edge, nodes, resources, clearance=1.13):
    """Drop only bounded junction guards provably outside a straight half-edge.

    The bounding box over-approximates the polygon. Unknown, shared road and
    corridor resources remain intact. Clearance includes two simulation vehicle
    envelopes; this does not certify a physical map or a larger load profile.
    """
    a, b = nodes[edge['from']], nodes[edge['to']]
    kept = []
    for key in edge.get('resourceIds', []):
        resource = resources.get(key, {})
        boundary = resource.get('boundary')
        if resource.get('type') != 'Intersection' or not boundary or len(boundary) < 3:
            kept.append(key); continue
        try:
            xs = [float(p['x']) for p in boundary]; ys = [float(p['y']) for p in boundary]
            coordinates = xs + ys + [a['x'], a['y'], b['x'], b['y']]
            if not all(math.isfinite(v) for v in coordinates):
                kept.append(key); continue
            # Distance between AABBs is a lower bound on segment/polygon distance.
            dx = max(min(xs)-max(a['x'], b['x']), min(a['x'], b['x'])-max(xs), 0)
            dy = max(min(ys)-max(a['y'], b['y']), min(a['y'], b['y'])-max(ys), 0)
            if math.hypot(dx, dy) <= clearance:
                kept.append(key)
        except (KeyError, TypeError, ValueError):
            kept.append(key)
    return kept


def add_stops(source, clearance=1.5, spacing=6.0):
    if source.get('unit') != 'm':
        raise ValueError('Only metre maps are supported.')
    if clearance < 1.13 or spacing < clearance * 2:
        raise ValueError('Stopping clearance/spacing is below the vehicle envelope policy.')
    result = copy.deepcopy(source)
    nodes = {n['id']: n for n in result['nodes']}
    neighbors = defaultdict(set)
    incident = defaultdict(list)
    for edge in result['edges']:
        if not edge.get('enabled', True):
            continue
        a, b = edge['from'], edge['to']
        neighbors[a].add(b); neighbors[b].add(a)
        incident[a].append(edge); incident[b].append(edge)
    def point(n): return (n['x'], n['y'])
    def straight(key):
        if len(neighbors[key]) != 2 or any(e.get('geometryKind', 'Line') not in ('Line', 0) for e in incident[key]):
            return False
        a, b = (nodes[k] for k in sorted(neighbors[key]))
        p = nodes[key]
        va = (a['x']-p['x'], a['y']-p['y']); vb = (b['x']-p['x'], b['y']-p['y'])
        length = math.hypot(*va) * math.hypot(*vb)
        return length > 1e-8 and (va[0]*vb[0]+va[1]*vb[1])/length < -.9999
    # Treat every branch/turn, business dock and charging/waiting place as protected.
    protected = [n for key,n in nodes.items() if n.get('enabled', True) and
                 (n.get('type') not in ('Waypoint', 0) or not straight(key))]
    chosen = []
    for key, node in sorted(nodes.items()):
        if not node.get('enabled', True) or node.get('type') not in ('Waypoint', 0) or not straight(key):
            continue
        if any(math.dist(point(node), point(other)) < clearance for other in protected):
            continue
        if any(math.dist(point(node), point(other)) < spacing for other in chosen):
            continue
        resource = 'SIM-STOP-' + key
        if any(r['id'] == resource for r in result['resources']):
            raise ValueError('Stopping resource ID collision: ' + resource)
        # Existing graph coordinate and Robokit site identity remain unchanged.
        node.update(type='WaitingPoint', allowTrafficStop=True, allowTransitThrough=True)
        node['resourceIds'] = sorted(set(node.get('resourceIds', []) + [resource]))
        result['resources'].append(dict(id=resource, type='Station', capacity=1, layerId='navigation'))
        chosen.append(node)
    # Dense CAD graphs often contain no existing vertex in a clear straight.
    # Add an explicit midpoint only on a sufficiently long straight segment,
    # splitting both directed copies through the SAME physical stop identity.
    groups = defaultdict(list)
    for edge in result['edges']:
        if edge.get('enabled', True) and edge.get('geometryKind', 'Line') in ('Line',0) and not edge.get('vendorPath'):
            groups[tuple(sorted((edge['from'], edge['to'])))].append(edge)
    split_ids = set(); additions = []; localized = []
    resource_definitions = {r['id']: r for r in result['resources']}
    for (a,b), edges in sorted(groups.items()):
        start, end = nodes[a], nodes[b]
        if math.dist(point(start), point(end)) < clearance * 2:
            continue
        node = dict(id='SIM-SAFE-'+min(e['id'] for e in edges), x=(start['x']+end['x'])/2,
                    y=(start['y']+end['y'])/2, type='WaitingPoint', enabled=True,
                    allowTrafficStop=True, allowTransitThrough=True, layerId='navigation')
        if any(math.dist(point(node), point(other)) < clearance for other in protected):
            continue
        if any(math.dist(point(node), point(other)) < spacing for other in chosen):
            continue
        if node['id'] in nodes:
            raise ValueError('Stop ID collision')
        node['agvSiteId'] = node['id']
        resource='SIM-STOP-'+node['id']; node['resourceIds']=[resource]
        result['nodes'].append(node); nodes[node['id']]=node; chosen.append(node)
        result['resources'].append(dict(id=resource,type='Station',capacity=1,layerId='navigation'))
        for edge in edges:
            split_ids.add(edge['id'])
            first=copy.deepcopy(edge); second=copy.deepcopy(edge)
            first.update(id=edge['id']+'-S1',to=node['id'],distance=edge['distance']/2)
            second.update(id=edge['id']+'-S2',**{'from':node['id']},distance=edge['distance']/2)
            # Shared road capacity stays on BOTH halves. Local junction protection
            # follows its actual boundary rather than occupying the far junction
            # from a stopped vehicle on the other half of the original edge.
            for half in (first, second):
                before = half.get('resourceIds', [])
                half['resourceIds'] = localize_split_intersections(half, nodes, resource_definitions)
                if before != half['resourceIds']:
                    localized.append(dict(edgeId=half['id'], removed=sorted(set(before)-set(half['resourceIds']))))
            additions.extend((first,second))
    if split_ids:
        result['edges']=[e for e in result['edges'] if e['id'] not in split_ids]+additions
    audit = dict(scope='Simulation graph/station clearance only; not physical site acceptance',
                 clearanceMeters=clearance, spacingMeters=spacing,
                 vehicleLengthMeters=.8, vehicleWidthMeters=.52,
                 addedStops=[dict(id=n['id'], x=n['x'], y=n['y'], resourceId='SIM-STOP-'+n['id']) for n in chosen],
                 originalNodeCount=len(source['nodes']), addedNodeCount=len(result['nodes'])-len(source['nodes']),
                 splitEdges=sorted(split_ids), localizedIntersectionBindings=localized,
                 junctionClearanceMeters=1.13, existingCoordinatesUnchanged=True)
    return result, audit


MAIN_LANE_ROLES = {'eastbound','westbound','northbound','southbound'}


def _point_distance(p, a, b):
    dx, dy = b[0]-a[0], b[1]-a[1]
    span = dx*dx + dy*dy
    t = 0 if span == 0 else max(0, min(1, ((p[0]-a[0])*dx+(p[1]-a[1])*dy)/span))
    return math.dist(p, (a[0]+t*dx, a[1]+t*dy))


def _segment_distance(a, b, c, d):
    def side(x, y, z):
        return (y[0]-x[0])*(z[1]-x[1])-(y[1]-x[1])*(z[0]-x[0])
    if min(max(a[0],b[0]),max(c[0],d[0])) < max(min(a[0],b[0]),min(c[0],d[0])) or \
            min(max(a[1],b[1]),max(c[1],d[1])) < max(min(a[1],b[1]),min(c[1],d[1])):
        return min(_point_distance(a,c,d), _point_distance(b,c,d),
                   _point_distance(c,a,b), _point_distance(d,a,b))
    if side(a,b,c)*side(a,b,d) <= 0 and side(c,d,a)*side(c,d,b) <= 0:
        return 0.0
    return min(_point_distance(a,c,d), _point_distance(b,c,d),
               _point_distance(c,a,b), _point_distance(d,a,b))


def add_main_lane_pockets(source, clearance=1.5, spacing=6.0, lateral_offset=1.8):
    """Add one-car simulation lay-bys beside straight main-lane nodes.

    A pocket is only emitted where two distinct main-lane neighbours lie on
    opposite sides of the anchor. Both lateral sides are tested against every
    other road, junction branch and fixed station. One clear side is selected;
    the original node, edge and direction attributes are never modified.
    Crucially, the returned edges are dead-end lay-bys. The service must use
    them explicitly as rolling-window stops; merely adding them is not enough.
    """
    if source.get('unit') != 'm' or clearance < 1.23 or spacing < clearance * 2:
        raise ValueError('Main-lane pockets require metre coordinates and fleet clearance.')
    if lateral_offset < clearance:
        raise ValueError('The pocket centre must clear the main-lane envelope.')
    result = copy.deepcopy(source)
    nodes = {n['id']: n for n in source['nodes'] if n.get('enabled', True)}
    incident = defaultdict(list)
    segments = []
    for edge in source['edges']:
        if not edge.get('enabled', True) or edge['from'] not in nodes or edge['to'] not in nodes:
            continue
        if edge.get('geometryKind') not in (None, 'Line', 0):
            raise ValueError('Main-lane pocket audit requires straight-line navigation edges.')
        segments.append((edge['id'], edge['from'], edge['to']))
        incident[edge['from']].append(edge)
        incident[edge['to']].append(edge)

    def point(key):
        return nodes[key]['x'], nodes[key]['y']

    roads = [(edge_id, point(start), point(end)) for edge_id, start, end in segments]
    segment_nodes = {edge_id: (start, end) for edge_id, start, end in segments}
    protected_nodes = [
        (key, point(key)) for key, node in nodes.items()
        if node.get('type') not in ('Waypoint', 0)
        and not (str(key).startswith('SIM-SAFE-') and node.get('allowTrafficStop') is True)
    ]
    existing = {n['id'] for n in source['nodes']}
    occupied = {r['id'] for r in source['resources']}
    added = []
    rejected = defaultdict(int)

    def eligible_anchor(anchor, node):
        if str(anchor).startswith(('WAIT-', 'DUMPER-', 'CHARGE-')):
            return False
        if node.get('type') not in ('Waypoint', 0, 'Intersection', 'WaitingPoint'):
            return False
        if node.get('type') == 'WaitingPoint' and not str(anchor).startswith('SIM-SAFE-'):
            return False
        return 'X-' + anchor in node.get('resourceIds', [])

    for anchor in sorted(incident):
        node = nodes[anchor]
        if not eligible_anchor(anchor, node):
            rejected['not-a-main-lane-anchor'] += 1
            continue
        start = point(anchor)
        by_neighbour = {}
        for edge in incident[anchor]:
            if edge.get('laneRole') not in MAIN_LANE_ROLES or edge.get('vendorPath'):
                continue
            other = edge['to'] if edge['from'] == anchor else edge['from']
            by_neighbour.setdefault(other, []).append(edge)
        if len(by_neighbour) != 2:
            rejected['not-a-straight-through-node'] += 1
            continue
        neighbours = sorted(by_neighbour)
        vectors = []
        for other in neighbours:
            x, y = point(other)
            vectors.append((x-start[0], y-start[1]))
        lengths = [math.hypot(*vector) for vector in vectors]
        if min(lengths) < 1e-3:
            rejected['zero-length-road'] += 1
            continue
        cosine = sum(a*b for a, b in zip(*vectors)) / (lengths[0] * lengths[1])
        if cosine > -.995:
            rejected['turn-or-junction'] += 1
            continue
        tangent = ((vectors[0][0]/lengths[0])-(vectors[1][0]/lengths[1]),
                   (vectors[0][1]/lengths[0])-(vectors[1][1]/lengths[1]))
        tangent_length = math.hypot(*tangent)
        if tangent_length < 1e-6:
            rejected['ambiguous-road-direction'] += 1
            continue
        tangent = (tangent[0]/tangent_length, tangent[1]/tangent_length)
        normal = (-tangent[1], tangent[0])
        if any(math.dist(start, row['anchorPosition']) < spacing for row in added):
            rejected['spaced-too-close'] += 1
            continue

        choices = []
        for side in (1, -1):
            end = (start[0] + normal[0] * lateral_offset * side,
                   start[1] + normal[1] * lateral_offset * side)
            nearest_lane = min(_point_distance(end, a, b) for _, a, b in roads)
            nearest_fixed = min((math.dist(end, position) for key, position in protected_nodes if key != anchor),
                                default=float('inf'))
            if nearest_lane < clearance:
                rejected['lateral-clearance'] += 1
                continue
            if nearest_fixed < clearance:
                rejected['fixed-equipment-clearance'] += 1
                continue
            branch_on_side = False
            for edge in incident[anchor]:
                if edge.get('laneRole') in MAIN_LANE_ROLES and not edge.get('vendorPath'):
                    continue
                other = edge['to'] if edge['from'] == anchor else edge['from']
                ox, oy = point(other)
                if (ox-start[0])*normal[0]*side + (oy-start[1])*normal[1]*side > 0:
                    branch_on_side = True
                    break
            if branch_on_side:
                rejected['branch-on-pocket-side'] += 1
                continue
            if any(_segment_distance(start, end, a, b) <= .01
                   for edge_id, a, b in roads if anchor not in segment_nodes[edge_id]):
                rejected['connector-crosses-road'] += 1
                continue
            if any(math.dist(end, row['position']) < spacing for row in added):
                rejected['pocket-spacing'] += 1
                continue
            choices.append((nearest_lane - clearance + nearest_fixed - clearance, side, end,
                            nearest_lane, nearest_fixed))
        if not choices:
            rejected['no-clear-lateral-side'] += 1
            continue
        _, side, end, nearest_lane, nearest_fixed = max(choices, key=lambda row: (row[0], row[1]))
        site = 'SIM-ROAD-HOLD-'+anchor
        resource = 'SIM-STOP-'+site
        if site in existing or resource in occupied:
            raise ValueError('Pocket identity collision: '+site)
        result['nodes'].append(dict(id=site,agvSiteId=site,x=end[0],y=end[1],type='WaitingPoint',
            enabled=True,allowTrafficStop=True,allowDumperWaiting=False,allowChargingWaiting=False,
            allowTransitThrough=False,resourceIds=[resource],layerId='navigation'))
        result['resources'].append(dict(id=resource,type='Station',capacity=1,layerId='navigation'))
        for suffix, a, b in [('IN',anchor,site),('OUT',site,anchor)]:
            result['edges'].append(dict(id=site+'-'+suffix,**{'from':a,'to':b},enabled=True,
                bidirectional=False,geometryKind='Line',distance=math.dist(start,end),speedLimit=.4,
                resourceIds=[resource],laneRole='traffic-holding',layerId='navigation'))
        added.append(dict(id=site,anchor=anchor,side=side,anchorPosition=start,position=end,
                          resourceId=resource,nearestLaneMeters=round(nearest_lane,3),
                          nearestFixedEquipmentMeters=round(nearest_fixed,3),
                          connectorMeters=round(lateral_offset,3)))
    return result, dict(scope='Simulation graph only; not physical site acceptance',
                        clearanceMeters=clearance, spacingMeters=spacing,
                        lateralOffsetMeters=lateral_offset, addedStops=added,
                        rejectedAnchorCounts=dict(sorted(rejected.items())),
                        originalNodeCount=len(source['nodes']), addedNodeCount=len(added),
                        originalEdgesUnchanged=True, existingCoordinatesUnchanged=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--map', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--main-lanes', action='store_true')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    raw = args.map.read_bytes()
    make = add_main_lane_pockets if args.main_lanes else add_stops
    updated, audit = make(json.loads(raw.decode('utf-8-sig')))
    audit['sourceSha256'] = hashlib.sha256(raw).hexdigest()
    encoded = json.dumps(updated, ensure_ascii=False, indent=2).encode('utf-8')
    audit['mapSha256'] = hashlib.sha256(encoded).hexdigest()
    (args.output/'map.json').write_bytes(encoded)
    (args.output/'safe-stop-audit.json').write_text(json.dumps(audit,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(addedStops=len(audit['addedStops']), output=str(args.output))))
