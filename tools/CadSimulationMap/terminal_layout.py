"""Terminal geometry from reviewed CAD symbols, never text insertion anchors."""

BAY_HANDLES = {
    'WAIT-01': '855E2', 'WAIT-02': '855E3', 'WAIT-03': '855E5',
    'WAIT-04': '855E6', 'WAIT-05': '8CB67',
    'CHARGE-01': '855EC', 'CHARGE-02': '855ED',
}
# Synthetic unit fixtures only. Production reads the referenced closed symbols.
FIXTURE_CENTRES = {
    'WAIT-01': (984.748739,-428.437392), 'WAIT-02': (986.123555,-428.445034),
    'WAIT-03': (987.479304,-430.178507), 'WAIT-04': (986.116723,-430.169655),
    'WAIT-05': (987.507191,-428.460620),
    'CHARGE-01': (981.855052,-432.932171), 'CHARGE-02': (983.498024,-432.963488),
}

def terminal_layout(doc, allow_fixture=False):
    bays={}; evidence={}
    for name,handle in BAY_HANDLES.items():
        entity=doc.entitydb.get(handle)
        if entity is None:
            if not allow_fixture: raise ValueError(f'缺少CAD泊位轮廓{name}/{handle}，不能退回使用文字锚点')
            bays[name]=FIXTURE_CENTRES[name];continue
        if entity.dxftype()!='LWPOLYLINE' or not entity.closed:
            raise ValueError(f'CAD泊位轮廓未闭合{name}/{handle}')
        points=list(entity.get_points());xs=[p[0]/1000 for p in points];ys=[p[1]/1000 for p in points]
        if len(points)!=4 or not (.6<max(xs)-min(xs)<.9 and .4<max(ys)-min(ys)<.7):
            raise ValueError(f'CAD泊位矩形尺寸异常{name}/{handle}')
        bays[name]=(round((min(xs)+max(xs))/2,6),round((min(ys)+max(ys))/2,6))
        evidence[name]=dict(handle=handle,bounds=[min(xs),min(ys),max(xs),max(ys)],centre=bays[name])
    # The platform footprint is reference equipment; the AGV docks west of it.
    platform_handles=('8A6C5','8A6C7','8A6C9','8A6CA')
    outlines=[doc.entitydb.get(handle) for handle in platform_handles]
    if all(e is not None and e.dxftype()=='LINE' for e in outlines):
        points=[p for e in outlines for p in (e.dxf.start,e.dxf.end)]
        platform=(min(p.x for p in points)/1000,min(p.y for p in points)/1000,
                  max(p.x for p in points)/1000,max(p.y for p in points)/1000)
    elif allow_fixture:platform=(985.028,-432.036,988.203,-430.622)
    else:raise ValueError('倒料设备平台CAD轮廓缺失，停止建图')
    approach=(984.4,round((platform[1]+platform[3])/2,6))
    return dict(bays=bays,evidence=evidence,platformHandles=list(platform_handles),platformBounds=platform,dumperApproach=approach,
                centralY=-429.338663,spineX=[bays['CHARGE-01'][0],bays['CHARGE-02'][0]],
                fixtureOnly=allow_fixture and not evidence,
                authority='CAD symbol centres; directions and dumper approach are user-authorised design, docking poses pending')
