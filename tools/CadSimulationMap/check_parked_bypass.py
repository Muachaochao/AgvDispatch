"""Check alternate graph paths while each CNC bay remains physically occupied.

This supplements, not replaces, swept-body dynamic checks. Occupied station
resource bindings must have been generated before this check is run.
"""
import argparse,json
from collections import defaultdict,deque
from pathlib import Path


def check(m):
    nodes={n['id']:n for n in m['nodes']}
    stations=sorted((n for n in m['nodes'] if n.get('agvSiteId','').startswith('CNC-')),key=lambda n:n['agvSiteId'])
    protected={n['id'] for n in m['nodes'] if n['type'] in ('Station','WaitingPoint','ChargingPoint') and not n.get('allowTransitThrough')}
    def path(source,target,occupied):
        source=next((n['id'] for n in m['nodes'] if n.get('agvSiteId')==source),source)
        target=next((n['id'] for n in m['nodes'] if n.get('agvSiteId')==target),target)
        graph=defaultdict(list)
        for e in m['edges']:
            if occupied in e.get('resourceIds',[]):continue
            if occupied in nodes[e['from']].get('resourceIds',[]) or occupied in nodes[e['to']].get('resourceIds',[]):continue
            if e.get('enabled',True):graph[e['from']].append((e['to'],e['id']))
        pending=deque([source]);prev={source:None}
        while pending:
            cur=pending.popleft()
            if cur==target:
                route=[]
                while prev[cur] is not None:
                    last,eid=prev[cur];route.append(eid);cur=last
                return list(reversed(route))
            for nxt,eid in graph[cur]:
                if nxt not in prev and (nxt not in protected or nxt==target):prev[nxt]=(cur,eid);pending.append(nxt)
        return None
    checks=[]
    for i,n in enumerate(stations):
        target=stations[(i+1)%len(stations)]['agvSiteId']
        route=path('WAIT-05',target,n['agvSiteId'])
        checks.append(dict(occupied=n['agvSiteId'],target=target,passed=route is not None,edges=route))
    regression=path('DUMPER-001','CNC-233','CNC-209')
    return dict(passed=all(r['passed'] for r in checks) and regression is not None,
                checks=checks,previousFailure=dict(occupied='CNC-209',source='DUMPER-001',target='CNC-233',passed=regression is not None,edges=regression),
                scope='Graph bypass with one occupied bay, actual edge/resource exclusions and business no-through. Not dynamic body/turning clearance or all-bays-occupied proof.')


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--map',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    result=check(json.loads(a.map.read_text(encoding='utf-8-sig')))
    a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(passed=result['passed'],checks=len(result['checks']),failures=[r['occupied'] for r in result['checks'] if not r['passed']],previousFailurePassed=result['previousFailure']['passed'])))
    raise SystemExit(0 if result['passed'] else 1)
