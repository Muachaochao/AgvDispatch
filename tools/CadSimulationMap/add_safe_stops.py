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
    split_ids = set(); additions = []
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
            # Keep existing protections on both halves, including opposite-direction capacity.
            # A window split is never permission to delete a shared corridor resource.
            additions.extend((first,second))
    if split_ids:
        result['edges']=[e for e in result['edges'] if e['id'] not in split_ids]+additions
    audit = dict(scope='Simulation graph/station clearance only; not physical site acceptance',
                 clearanceMeters=clearance, spacingMeters=spacing,
                 vehicleLengthMeters=.8, vehicleWidthMeters=.52,
                 addedStops=[dict(id=n['id'], x=n['x'], y=n['y'], resourceId='SIM-STOP-'+n['id']) for n in chosen],
                 originalNodeCount=len(source['nodes']), addedNodeCount=len(result['nodes'])-len(source['nodes']),
                 splitEdges=sorted(split_ids), existingCoordinatesUnchanged=True)
    return result, audit


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--map', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    raw = args.map.read_bytes()
    updated, audit = add_stops(json.loads(raw.decode('utf-8-sig')))
    audit['sourceSha256'] = hashlib.sha256(raw).hexdigest()
    encoded = json.dumps(updated, ensure_ascii=False, indent=2).encode('utf-8')
    audit['mapSha256'] = hashlib.sha256(encoded).hexdigest()
    (args.output/'map.json').write_bytes(encoded)
    (args.output/'safe-stop-audit.json').write_text(json.dumps(audit,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(addedStops=len(audit['addedStops']), output=str(args.output))))
