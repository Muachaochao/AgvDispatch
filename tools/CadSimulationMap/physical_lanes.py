"""Separated CAD linework for the user-authorised two-lane simulation design.

Magenta CAD lines are design references, not certified vendor navigation paths.
Direction, crossovers and terminal connections are explicitly simulation choices.
All intersections are split before publication, including terminal bay access.
"""
from __future__ import annotations
from collections import defaultdict
import math
import ezdxf


def build_physical_graph(stations, cad_dxf, base_builder, *, allow_terminal_fixture=False):
    definition, summary = base_builder(stations)
    original = {n['id']: n for n in definition['nodes']}
    doc = ezdxf.readfile(cad_dxf)
    lines = [e for e in doc.modelspace().query('LINE') if e.dxf.layer == '管道']
    horizontal = sorted((e for e in lines if abs(e.dxf.start.y-e.dxf.end.y)<.1
                         and abs(e.dxf.start.x-e.dxf.end.x)>120000
                         and -426000<e.dxf.start.y<-388000), key=lambda e:-e.dxf.start.y)
    if len(horizontal)!=12:
        raise ValueError(f'需找到CAD的六组平行线，实际{len(horizontal)}条')
    ys = [round(e.dxf.start.y/1000,6) for e in horizontal]
    west,east = 910.916525,1031.462795
    inner_w,inner_e=west+1,east-1
    segments=[]
    # Every tuple is an actual geometric segment, not a visual offset.
    def line(a,b,kind,both=False):
        if math.dist(a,b)>1e-5: segments.append((tuple(a),tuple(b),kind,both))
    for i,y in enumerate(ys):
        a,b=(west,y),(east,y)
        line(a,b,'eastbound' if i%2==0 else 'westbound',False) if i%2==0 else line(b,a,'westbound')
    # Paired perimeter spines, all four captured in the CAD.
    for x,northbound in ((west,False),(inner_w,True),(inner_e,False),(east,True)):
        a,b=(x,ys[0]),(x,ys[-1])
        line(b,a,'northbound') if northbound else line(a,b,'southbound')
    crossovers=[]
    # Keep the middle connector away from near-coincident machine branches;
    # a 3 cm segment cannot represent an executable turn and some viewers
    # merge its endpoints. Move the connector, never the machine/docking point.
    machine_x=[s.cad_x_mm/1000 for s in stations]
    middle=max((969+i*.1 for i in range(61)),key=lambda x:min(abs(x-sx) for sx in machine_x))
    for i in range(0,12,2):
        # The supplied drawing also has repeated cross-connections, not just
        # end turns. Provide a controlled connector in every usable gap between
        # CNC approaches. Otherwise a parked bay on the eastbound lane isolates
        # the next CNC even though an unobstructed westbound lane exists.
        branch_x=sorted(set(s.cad_x_mm/1000 for s in stations
            if min(range(len(ys)),key=lambda j:abs(ys[j]-s.cad_y_mm/1000))//2==i//2))
        gaps=[(a+b)/2 for a,b in zip(branch_x,branch_x[1:]) if b-a>=.6]
        extras=[middle] if all(abs(middle-x)>.3 for x in gaps) else []
        for x in sorted(set((inner_w,inner_e,*extras,*gaps))):
            line((x,ys[i]),(x,ys[i+1]),'crossover',True)
            crossovers.append(dict(pair=i//2+1,x=x,upper=ys[i],lower=ys[i+1]))
    business=[]
    for s in stations:
        sx,sy=s.cad_x_mm/1000,s.cad_y_mm/1000
        closest=min(ys,key=lambda y:abs(y-sy))
        py=round(sy+math.copysign(1.1,closest-sy),6)
        node=dict(original['BUS-'+s.station_id],x=sx,y=py)
        business.append(node)
        line((sx,py),(sx,closest),'cnc-access',True)
        summary['stationRelations'][s.station_id].update(cadPickupMm=dict(x=sx*1000,y=py*1000),
                                                        approachNode='',laneY=closest)
    # The user's 2026-11-11 markup takes priority over the old rectangular loop.
    # Preserve actual bay-symbol centres; text insertion points are labels only.
    from terminal_layout import terminal_layout
    terminal=terminal_layout(doc, allow_terminal_fixture)
    tw,te=terminal['spineX']
    top,bottom=ys[-1],terminal['dumperApproach'][1]
    line((tw,top),(tw,bottom),'terminal-in')
    # Join the main pair in a machine gap, not alongside a parked CNC bay.
    # Keep the east return riser well separated from the west entry rail.
    merge_x=min((c['x'] for c in crossovers if c['pair']==6 and c['x']>te+1),key=lambda x:x-te)
    bend_y=round(ys[-1]-.9,6)
    line((te,bottom),(te,bend_y),'terminal-out')
    line((te,bend_y),(merge_x,bend_y),'terminal-out')
    line((merge_x,bend_y),(merge_x,ys[-2]),'terminal-out')
    terminal['returnMerge']=dict(x=merge_x,bendY=bend_y,reason='Join between CNC bays; avoid occupied CNC-291 blocking the merge')
    line((tw,bottom),(te,bottom),'terminal-turn')
    mid=terminal['centralY']
    # One controlled reversible waiting collector between the three/two bays.
    # Do not add an east/south bypass through the lifting platform footprint.
    hub_x={1:terminal['bays']['WAIT-01'][0],
           2:round((terminal['bays']['WAIT-02'][0]+terminal['bays']['WAIT-04'][0])/2,6),
           3:round((terminal['bays']['WAIT-05'][0]+terminal['bays']['WAIT-03'][0])/2,6)}
    line((tw,mid),(hub_x[3],mid),'waiting-collector',True)
    adjustments=[]
    def terminal_node(name, point):
        node=dict(original[name]);dx,dy=point[0]-node['x'],point[1]-node['y']
        node.update(x=point[0],y=point[1]);business.append(node)
        for resource in definition['resources']:
            if resource['id'] in node['resourceIds']:
                for p in resource.get('boundary',[]):p['x']+=dx;p['y']+=dy
        adjustments.append(dict(id=name,old=[original[name]['x'],original[name]['y']],
                                point=list(point),distanceMeters=round(math.hypot(dx,dy),6)))
        return node
    for i in range(1,6):
        name=f'WAIT-{i:02}';node=terminal_node(name,terminal['bays'][name])
        column={1:1,2:2,3:3,4:2,5:3}[i]
        line((node['x'],node['y']),(hub_x[column],mid),'waiting-bay',True)
    for i,x in ((1,tw),(2,te)):
        name=f'CHARGE-{i:02}';node=terminal_node(name,terminal['bays'][name])
        line((x,bottom),(node['x'],node['y']),'charging-access',True)
    dump=terminal_node('DUMPER-001',terminal['dumperApproach'])
    line((te,bottom),(dump['x'],dump['y']),'dumper-access',True)
    # No surveyed handling bay exists west of the entry lane. Do not invent one.
    summary['inspectionSite']='WAIT-01'
    terminal['inspectionPolicy']='Reuse existing WAIT-01 for software topology only; not a certified emergency safe point'
    terminal['dumperManeuver']={'entry':'eastbound short spur', 'exit':'reverse along same short spur to terminal-out',
        'reverseDistanceMeters':round(dump['x']-te,6), 'requires':'vehicle reverse support, confirmed docking heading, clear spur and reserved exit junction',
        'navigationCommandValidated':False}
    terminal['adjustments']=adjustments
    terminal['directionRules']=['上横线向东、下横线向西','左竖线进区、右竖线出区',
        '底部横段仅由进区线向返回线单向连接','各组上下排换道短段双向互斥','待命中间横道和泊位支线双向互斥','双充电位各自独立尽端支线','倒料从西侧专用支线进退，容量1']
    # Split arbitrary straight segments as well as orthogonal crossings. Slight
    # offsets between upper/lower CAD bay symbols must not create 7mm junctions.
    def key(p):return (round(p[0],6),round(p[1],6))
    def cross(a,b):return a[0]*b[1]-a[1]*b[0]
    points={key(p) for a,b,_,_ in segments for p in (a,b)}
    for a,b,_,_ in segments:
        r=(b[0]-a[0],b[1]-a[1])
        for c,d,_,_ in segments:
            v=(d[0]-c[0],d[1]-c[1]);den=cross(r,v)
            if abs(den)<1e-10:continue
            delta=(c[0]-a[0],c[1]-a[1]);t=cross(delta,v)/den;u=cross(delta,r)/den
            if -1e-8<=t<=1+1e-8 and -1e-8<=u<=1+1e-8:
                points.add(key((a[0]+t*r[0],a[1]+t*r[1])))
    nodes=[];by_point={}
    business_points={key((n['x'],n['y'])):n for n in business}
    for i,p in enumerate(sorted(points)):
        n=business_points.get(p)
        if n is None:
            nid=f'CAD2-J{i:04}'
            n=dict(id=nid,agvSiteId=nid,x=p[0],y=p[1],type='Waypoint',enabled=True,
                   layerId='navigation',resourceIds=[],allowTransitThrough=True,allowTrafficStop=False)
        else:n=dict(n,x=p[0],y=p[1])
        by_point[p]=n;nodes.append(n)
    edges=[];seen=set()
    for a,b,kind,both in segments:
        a,b=key(a),key(b)
        dx,dy=b[0]-a[0],b[1]-a[1];squared=dx*dx+dy*dy
        def progress(p):return ((p[0]-a[0])*dx+(p[1]-a[1])*dy)/squared
        ps=sorted((p for p in points if abs((p[0]-a[0])*dy-(p[1]-a[1])*dx)<=2e-6*math.sqrt(squared)
                   and -1e-8<=progress(p)<=1+1e-8),key=progress)
        for p,q in zip(ps,ps[1:]):
            for src,dst in ((p,q),(q,p)) if both else ((p,q),):
                sid,tid=by_point[src]['id'],by_point[dst]['id']
                if (sid,tid) in seen:continue
                seen.add((sid,tid));edges.append(dict(id=f'CAD2-E{len(edges):05}',**{'from':sid,'to':tid},
                    distance=round(math.dist(src,dst),6),speedLimit=.75,bidirectional=False,enabled=True,
                    geometryKind='Line',layerId='navigation',resourceIds=[],displayName=kind,laneRole=kind))
    resources=[r for r in definition['resources'] if r['id'] in {r for n in business for r in n['resourceIds']}]
    # A shared junction/segment lease covers crossing and reciprocal branches.
    # Capacity is one at junctions and business bays, without global lane locks.
    neighbours=defaultdict(set)
    for e in edges:
        neighbours[e['from']].add(e['to']);neighbours[e['to']].add(e['from'])
    by_id={n['id']:n for n in nodes}
    for n in nodes:
        if len(neighbours[n['id']])>=3 and not n['resourceIds']:
            rid='X-'+n['id'];n['resourceIds']=[rid]
            resources.append(dict(id=rid,type='Intersection',capacity=1,layerId='navigation',boundary=[
                dict(x=n['x']+dx,y=n['y']+dy) for dx,dy in ((-.55,-.55),(.55,-.55),(.55,.55),(-.55,.55))]))
    # Reserve the first return-lane crossing before admitting a vehicle to dump.
    # A following full-box route crosses this same junction on its way to WAIT;
    # without the guard it can reserve the front vehicle's exit for minutes.
    exit_junction=by_point[key((te,mid))]
    exit_resources=list(exit_junction['resourceIds'])
    if not exit_resources:raise ValueError('倒料返回交叉口缺少互斥资源')
    by_id['DUMPER-001']['resourceIds']=sorted(set(by_id['DUMPER-001']['resourceIds']+exit_resources))
    terminal['protectedExitJunction']=dict(nodeId=exit_junction['id'],resourceIds=exit_resources,
        policy='Acquire with dumper approach; retain until body-clear release evidence')
    pair_resources={}
    for e in edges:
        reverse=(e['to'],e['from']) in seen
        if reverse:
            pair=tuple(sorted((e['from'],e['to'])))
            if pair not in pair_resources:
                rid=f'SEG-{len(pair_resources):04}';pair_resources[pair]=rid
                a,b=by_id[pair[0]],by_id[pair[1]]
                x0,x1=min(a['x'],b['x'])-.55,max(a['x'],b['x'])+.55
                y0,y1=min(a['y'],b['y'])-.55,max(a['y'],b['y'])+.55
                resources.append(dict(id=rid,type='Intersection',capacity=1,layerId='navigation',boundary=[
                    dict(x=x,y=y) for x,y in ((x0,y0),(x1,y0),(x1,y1),(x0,y1))]))
            e['resourceIds'].append(pair_resources[pair])
        for nid in (e['from'],e['to']):e['resourceIds'].extend(by_id[nid]['resourceIds'])
        e['resourceIds']=sorted(set(e['resourceIds']))
    for sid,r in summary['stationRelations'].items():
        n=next(n for n in business if n['agvSiteId']==sid)
        r['approachNode']=by_point[key((n['x'],r['laneY']))]['id']
    definition.update(name='B07-1F·305CNC·现场双线终端修订',nodes=nodes,edges=edges,resources=resources)
    definition['areaAnnotations']=[a for a in definition.get('areaAnnotations',[]) if not a['id'].startswith('SIM-AREA-G')]
    for i in range(6):
        definition['areaAnnotations'].append(dict(id=f'SIM-LANES-{i+1}',sourceType='CadSimulationEvidence',
            text=f'通道{i+1} · 上行向东 / 下行向西',boundary=[dict(x=x,y=y) for x,y in (
                (west,ys[i*2]+.2),(east,ys[i*2]+.2),(east,ys[i*2+1]-.2),(west,ys[i*2+1]-.2))],
            strokeColor='#37d8cb',fillColor='#0b1e30',fillOpacity=.02,layerId='cad-zones',sourceJson=''))
    x0,y0,x1,y1=terminal['platformBounds']
    definition['areaAnnotations'].append(dict(id='CAD-TERMINAL-PLATFORM',sourceType='CadSimulationEvidence',
        text='倒料设备平台轮廓 · 禁止穿行',boundary=[dict(x=x,y=y) for x,y in ((x0,y0),(x1,y0),(x1,y1),(x0,y1))],
        strokeColor='#ff778a',fillColor='#532b3b',fillOpacity=.25,layerId='terminal-zones',sourceJson=''))
    summary.update(nodeCount=len(nodes),edgeCount=len(edges),resourceCount=len(resources),
                   physicalLanes=dict(pairs=6,crossovers=crossovers,minimumSeparationMeters=min(abs(ys[i]-ys[i+1]) for i in range(0,12,2)),
                                      cadLineHandles=[e.dxf.handle for e in sorted((e for e in lines if e.dxf.handle in {f'843B{i:X}' for i in range(4,16)}),key=lambda e:e.dxf.handle)],
                                      terminalSpineX=[tw,te],terminalLayout=terminal,directionAuthority='user-authorised simulation design'))
    return definition,summary
