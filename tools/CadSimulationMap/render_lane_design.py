"""Readable factory overview plus terminal detail for separated-lane designs."""
import argparse,json,math
from pathlib import Path
from PIL import Image,ImageDraw,ImageFont


def render(root):
    m=json.loads((root/'cad-simulation-map-definition.json').read_text(encoding='utf-8-sig'))
    ns={n['id']:n for n in m['nodes']}
    image=Image.new('RGB',(2400,1380),'#081522');d=ImageDraw.Draw(image)
    def f(n):return ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',n)
    d.text((35,20),'成都全厂 · 分离双车道设计',font=f(36),fill='#eaf5ff')
    d.text((36,76),'305 工位 / 5 辆 AGV / 双充电位 / 单倒料机｜箭头为本次设计方向，坐标沿用 CAD 米制',font=f(22),fill='#a3b8c9')
    def panel(box,bounds,title,terminal=False):
        x0,y0,x1,y1=box;wx0,wy0,wx1,wy1=bounds
        d.rounded_rectangle(box,12,fill='#0b1e30',outline='#31526a',width=2)
        d.text((x0+16,y0+12),title,font=f(23),fill='#d4ebff')
        scale=min((x1-x0-50)/(wx1-wx0),(y1-y0-85)/(wy1-wy0))
        ox=x0+25+((x1-x0-50)-(wx1-wx0)*scale)/2;oy=y0+58
        def pos(x,y):return ox+(x-wx0)*scale,oy+(wy1-y)*scale
        def arrow(a,b,color):
            ax,ay=pos(a['x'],a['y']);bx,by=pos(b['x'],b['y'])
            d.line((ax,ay,bx,by),fill=color,width=2)
            dist=math.hypot(bx-ax,by-ay)
            if dist<20:return
            ux,uy=(bx-ax)/dist,(by-ay)/dist
            for t in ([.5] if dist<150 else [.2,.5,.8]):
                x,y=ax+(bx-ax)*t,ay+(by-ay)*t
                d.polygon(((x+ux*5,y+uy*5),(x-ux*5-uy*3,y-uy*5+ux*3),(x-ux*5+uy*3,y-uy*5-ux*3)),fill=color)
        for e in m['edges']:
            a,b=ns[e['from']],ns[e['to']]
            if not all(wx0<=n['x']<=wx1 and wy0<=n['y']<=wy1 for n in (a,b)):continue
            role=e.get('laneRole','')
            color='#37d8cb' if role in ('eastbound','northbound','terminal-out','waiting-out') else '#ffa75c' if role in ('westbound','southbound','terminal-in','waiting-in') else '#7298b7'
            arrow(a,b,color)
        for n in ns.values():
            if not(wx0<=n['x']<=wx1 and wy0<=n['y']<=wy1):continue
            site=n.get('agvSiteId','');x,y=pos(n['x'],n['y'])
            if site.startswith('CNC-'):
                d.ellipse((x-2,y-2,x+2,y+2),fill='#cee9fa')
                if site in ('CNC-001','CNC-024','CNC-026','CNC-063','CNC-100','CNC-136','CNC-172','CNC-209','CNC-233','CNC-305'):
                    d.text((x+4,y-18),site,font=f(13),fill='#d4e8f2')
            elif terminal and site.startswith(('WAIT-','CHARGE-')) or terminal and site=='DUMPER-001':
                color='#ffc857' if site.startswith('CHARGE-') else '#ff778a' if site=='DUMPER-001' else '#44d7cf'
                d.rounded_rectangle((x-12,y-9,x+12,y+9),3,fill=color)
                label='倒料位' if site=='DUMPER-001' else '充电\n'+site[-2:] if site.startswith('CHARGE-') else '待命\n'+site[-2:]
                d.multiline_text((x-(21 if site=='DUMPER-001' else 14),y+12),label,font=f(14),fill=color,align='center',spacing=0)
        if terminal:
            # Equipment body remains a reference separate from AGV docking.
            x,y=pos(986.6155,-431.329)
            a=pos(985.028,-432.036);b=pos(988.203,-430.622)
            d.rectangle((a[0],b[1],b[0],a[1]),outline='#ef7899',width=2)
            d.text((x-35,y-5),'设备平台',font=f(16),fill='#ef7899')
            d.text((x0+20,y1-55),'设备本体与车辆对接位分别标注；不穿越机房设备区',font=f(15),fill='#a6bdcc')
    panel((25,125,2375,905),(909.8,-426.4,1032.5,-387.5),'全厂通道：六组上下双线，外围左右双线；中间连接为受控换道口')
    panel((25,925,1015,1350),(979.8,-434,989,-424),'终端放大：双通道接入、五待命位、双充电位及实际倒料区域',True)
    d.text((1070,960),'线路规则',font=f(27),fill='#d8edfc')
    for i,(color,text) in enumerate((('#37d8cb','青色箭头：横向向东 / 纵向向北'),('#ffa75c','橙色箭头：横向向西 / 纵向向南'),('#7298b7','灰蓝：工位支线、换道与进出泊位'),('#cee9fa','白点：机台取放箱位置，部分会占用近侧车道'))):
        d.text((1070,1015+i*50),text,font=f(22),fill=color)
    d.text((1070,1245),'按用户授权设计仿真方向；未作为实车可下发地图。',font=f(20),fill='#a8bfce')
    d.text((1070,1285),'换道短段双向互斥；本轮仅静态检查，未运行仿真。',font=f(20),fill='#a8bfce')
    out=root/'dual-lane-layout.png';image.save(out);print(str(out.resolve()))



def render_crossover_detail(root):
    m=json.loads((root/'cad-simulation-map-definition.json').read_text(encoding='utf-8-sig'))
    ns={n['id']:n for n in m['nodes']};anchor=next(n for n in ns.values() if n.get('agvSiteId')=='CNC-209')
    near=min((e for e in m['edges'] if e.get('laneRole')=='eastbound'),key=lambda e:abs(ns[e['from']]['y']-anchor['y']))
    upper=ns[near['from']]['y'];lower=max(n['y'] for e in m['edges'] if e.get('laneRole')=='westbound' for n in [ns[e['from']]] if n['y']<upper)
    left=anchor['x']-2;right=left+16;top=upper+1.3;bottom=lower-1.3
    im=Image.new('RGB',(2200,800),'#071522');d=ImageDraw.Draw(im)
    def f(n):return ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',n)
    def pos(n):return 80+(n['x']-left)*125,180+(top-n['y'])*125
    d.text((60,25),'上下排连接详图 · 主线单向，换道短段双向互斥',font=f(38),fill='#edf6ff')
    # Clip main lanes to this viewport; don't omit long segments crossing bounds.
    for e in m['edges']:
        a,b=ns[e['from']],ns[e['to']]
        if not all(bottom<=n['y']<=top for n in (a,b)):continue
        if max(a['x'],b['x'])<left or min(a['x'],b['x'])>right:continue
        a=dict(a,x=max(left,min(right,a['x'])));b=dict(b,x=max(left,min(right,b['x'])))
        ax,ay=pos(a);bx,by=pos(b);length=math.hypot(bx-ax,by-ay)
        if length<1:continue
        color='#37d8c2' if e.get('laneRole')=='eastbound' else '#ffb064' if e.get('laneRole')=='westbound' else '#82b8e8'
        d.line((ax,ay,bx,by),fill=color,width=5);ux,uy=(bx-ax)/length,(by-ay)/length
        for t in ([.4] if length<180 else [.3,.7]):
            x,y=ax+t*(bx-ax),ay+t*(by-ay)
            d.polygon([(x+ux*11,y+uy*11),(x-ux*9-uy*6,y-uy*9+ux*6),(x-ux*9+uy*6,y-uy*9-ux*6)],fill=color)
    for n in ns.values():
        if left<=n['x']<=right and bottom<=n['y']<=top:
            x,y=pos(n);d.ellipse((x-4,y-4,x+4,y+4),fill='#daeafa')
            if n.get('agvSiteId') in ('CNC-209','CNC-233'):
                d.text((x+8,y+18 if n['y']<lower else y-38),n['agvSiteId'],font=f(24),fill='#edf6ff')
    d.text((70,650),'灰蓝短段：允许上→下和下→上；同一时刻只放行一辆，交叉口共用节点。',font=f(29),fill='#b8d3e9')
    d.text((70,705),'双向是道路许可；不代表车端会自动倒车或已通过转弯净空验证。当前为静态设计。',font=f(26),fill='#a4becf')
    im.save(root/'lane-crossover-detail.png')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True);root=p.parse_args().root;render(root);render_crossover_detail(root)
