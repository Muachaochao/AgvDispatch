"""Static direction alternatives on the same CAD geometry; never runs vehicles."""
import argparse,heapq,json
from pathlib import Path
from check_parked_bypass import check


def evaluate(m):
    ns={n['id']:n for n in m['nodes']}
    columns=sorted({ns[e['from']]['x'] for e in m['edges'] if e.get('laneRole')=='crossover'})
    variants={}
    for mode in ('controlled-bidirectional','upper-to-lower-only','lower-to-upper-only','alternating-one-way'):
        edges=[]
        for e in m['edges']:
            if e.get('laneRole')=='crossover' and mode!='controlled-bidirectional':
                a,b=ns[e['from']],ns[e['to']];down=b['y']<a['y']
                wanted=mode=='upper-to-lower-only' or mode=='alternating-one-way' and columns.index(a['x'])%2==0
                if down!=wanted:continue
            edges.append(e)
        graph={**m,'edges':edges};bypass=check(graph)
        adj={n:[] for n in ns}
        for e in edges:adj[e['from']].append((e['to'],e['distance']))
        distances=[];unreachable=[]
        for start in ('DUMPER-001','WAIT-05'):
            dist={start:0};queue=[(0,start)]
            while queue:
                cost,n=heapq.heappop(queue)
                if cost>dist[n]:continue
                # Business bays are destinations, never transit shortcuts.
                if n!=start and ns[n].get('allowTransitThrough') is False:continue
                for nxt,weight in adj[n]:
                    c=cost+weight
                    if c<dist.get(nxt,float('inf')):dist[nxt]=c;heapq.heappush(queue,(c,nxt))
            for n in ns.values():
                if not n.get('agvSiteId','').startswith('CNC-'):continue
                if n['id'] not in dist:unreachable.append([start,n['agvSiteId']])
                else:distances.append(dist[n['id']])
        variants[mode]=dict(directedEdges=len(edges),singleOccupiedBypassPassed=bypass['passed'],
            bypassFailures=sum(not c['passed'] for c in bypass['checks']),
            historicalRegressionPassed=bypass['previousFailure']['passed'],unreachable=unreachable,
            reachableRoutes=len(distances),meanRouteMeters=round(sum(distances)/len(distances),3) if distances else None,
            maximumRouteMeters=round(max(distances),3) if distances else None)
    return dict(scope='Static graph comparison; not throughput, turning clearance, or dynamic deadlock proof',variants=variants)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--map',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    result=evaluate(json.loads(a.map.read_text(encoding='utf-8-sig')))
    a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
