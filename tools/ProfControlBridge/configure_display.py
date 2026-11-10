"""Apply an appropriately scaled station glyph through the display's property API.

No navigation geometry, route direction, vehicle body, analyser or permissions
are disabled. Resizing changes the viewer key point, so restore and verify each
original world coordinate before analysis. Only loopback display port 10001.
"""
import argparse,json,math
from pathlib import Path
from urllib.request import Request,urlopen


def geometry_check(data,marker_m=.02):
    stations=data['stations'];duplicates=[];near=[]
    for i,a in enumerate(stations):
        for b in stations[i+1:]:
            dx=abs(a['position']['x']-b['position']['x']);dy=abs(a['position']['y']-b['position']['y'])
            if dx<.01 and dy<.01:duplicates.append((a['mark'],b['mark']))
    for a in stations:
        p=a['position'];own=0;nearby=0
        for e in data['lines']:
            distances=[math.hypot(p['x']-e[k]['x'],p['y']-e[k]['y']) for k in ('p1','p2')]
            # Viewer endpoint equality defaults to 1 pixel (0.01 m here).
            if min(distances)<.01:own+=1
            if min(distances)<marker_m:nearby+=1
        if nearby>own:near.append(dict(mark=a['mark'],connected=own,nearby=nearby))
    return dict(overlappingStations=duplicates,nearUnconnected=near,
                passed=not duplicates and not near,markerMeters=marker_m,
                scope='Endpoint-distance counterpart of SameMarkCheck at 0.01 m/pixel; native analysis must still run.')


def main():
    p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);p.add_argument('--check-only',action='store_true');a=p.parse_args()
    base='http://127.0.0.1:10001'
    def api(path,body=None):
        r=Request(base+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'})
        with urlopen(r,timeout=60) as response:
            raw=response.read()
            if not raw:raise RuntimeError('显示接口返回空结果：'+path)
            return json.loads(raw)
    before=api('/apiv2/getInfo/getMap')
    def set_property(mark,prop,value):
        result=api('/apiv2/setInfo/property',dict(select=dict(type='Station',name=mark),propertyName=prop,value=value))
        if result.get('status')!=200:raise RuntimeError(f'{mark} {prop}: {result}')
    if not a.check_only:
        for i,s in enumerate(before['stations']):
            set_property(s['mark'],'StationViewSize','"2,2"')
            xy=s['position']
            set_property(s['mark'],'StationCoordinate',f"({xy['x']:.9f},{xy['y']:.9f})")
            if i%200==0:print('styled',i,flush=True)
    after=api('/apiv2/getInfo/getMap');lookup={s['mark']:s for s in after['stations']}
    errors=[]
    for s in before['stations']:
        t=lookup[s['mark']]
        delta=math.hypot(s['position']['x']-t['position']['x'],s['position']['y']-t['position']['y'])
        if delta>.00002:errors.append(dict(mark=s['mark'],delta=delta))
    check=geometry_check(after)
    result=dict(stations=len(after['stations']),texts=len(after['texts']),lines=len(after['lines']),
                coordinateChangesOutsideRounding=errors,endpointCheck=check,
                nativeAnalysis=None,stationGlyphSamples=[])
    for s in after['stations'][::max(1,len(after['stations'])//5)]:
        result['stationGlyphSamples'].append(api('/apiv2/getInfo/getProperty',dict(select=dict(type='Station',name=s['mark']),propertyName='StationViewSize')))
    if not errors and check['passed'] and not a.check_only:
        result['nativeAnalysis']=api('/apiv2/setInfo/mapAnalysis',{'global':True})
    a.output.parent.mkdir(parents=True,exist_ok=True)
    a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(endpointCheckPassed=check['passed'],coordinateErrors=len(errors),nativeAnalysis=result['nativeAnalysis']),ensure_ascii=False))
    if errors or not check['passed']:raise SystemExit(1)


if __name__=='__main__':main()
