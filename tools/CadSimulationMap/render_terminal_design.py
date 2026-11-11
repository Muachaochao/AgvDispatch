"""Static, dimensionally scaled terminal plan; no simulator or device connection."""
import argparse,json,math
from pathlib import Path
from PIL import Image,ImageDraw,ImageFont

def render(root):
    m=json.loads((root/'cad-simulation-map-definition.json').read_text(encoding='utf-8-sig'))
    manifest=json.loads((root/'source-manifest.json').read_text(encoding='utf-8-sig'))
    layout=manifest['summary']['physicalLanes']['terminalLayout'];ns={n['id']:n for n in m['nodes']}
    im=Image.new('RGB',(2800,2000),'#071522');d=ImageDraw.Draw(im)
    def f(n):return ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',n)
    d.text((60,28),'成都 B07-1F · 终端地图修订',font=f(48),fill='#edf6ff')
    d.text((62,98),'按 11月11日现场标注核对｜五待命位、双充电位、单倒料设备｜静态设计，本轮未运行仿真',font=f(27),fill='#a3becf')
    scale=155;ox=90;oy=180;left=979.6;top=-423.3
    def pos(x,y):return ox+(x-left)*scale,oy+(top-y)*scale
    for x in range(980,991):
        a=pos(x,-423.5);b=pos(x,-433.8);d.line((*a,*b),fill='#142d40',width=1)
    for y in range(-433,-423):
        a=pos(979.8,y);b=pos(989.8,y);d.line((*a,*b),fill='#142d40',width=1)
    x0,y0,x1,y1=layout['platformBounds'];a=pos(x0,y1);b=pos(x1,y0)
    d.rectangle((*a,*b),fill='#382638',outline='#df7997',width=4)
    for x in range(int(a[0]),int(b[0]),24):d.line((x,a[1],min(x+70,b[0]),b[1]),fill='#684052',width=1)
    d.text((a[0]+18,a[1]+28),'提升倒料设备区',font=f(29),fill='#f7c2d3')
    d.text((a[0]+18,a[1]+78),'道路不得穿越',font=f(24),fill='#e3a6bf')
    colors={'terminal-in':'#ffb064','terminal-out':'#37d8c2','eastbound':'#37d8c2','westbound':'#ffb064',
            'waiting-collector':'#77baff','waiting-bay':'#77baff','charging-access':'#ffd76a','dumper-access':'#ff7e9a'}
    def arrow(a,b,color,t=.4):
        ax,ay=pos(a['x'],a['y']);bx,by=pos(b['x'],b['y']);d.line((ax,ay,bx,by),fill=color,width=5)
        length=math.hypot(bx-ax,by-ay)
        if length<25:return
        ux,uy=(bx-ax)/length,(by-ay)/length;x,y=ax+(bx-ax)*t,ay+(by-ay)*t
        d.polygon([(x+ux*12,y+uy*12),(x-ux*10-uy*7,y-uy*10+ux*7),(x-ux*10+uy*7,y-uy*10-ux*7)],fill=color)
    for e in m['edges']:
        a,b=ns[e['from']],ns[e['to']]
        if all(979.8<=n['x']<=989.8 and -433.8<=n['y']<=-423.5 for n in (a,b)):
            arrow(a,b,colors.get(e.get('laneRole'),'#8296ab'))
    # Full-factory lines continue beyond this detail viewport.
    ys=sorted({round(n['y'],6) for e in m['edges'] if e.get('laneRole') in ('eastbound','westbound')
               for n in [ns[e['from']]] if -426<n['y']<-423},reverse=True)
    for i,y in enumerate(ys):
        a=dict(x=979.8,y=y);b=dict(x=989.8,y=y)
        arrow(a,b,'#37d8c2',.83) if i==0 else arrow(b,a,'#ffb064',.8)
        px,py=pos(980,y);d.text((px,py-45),'上排向东 →' if i==0 else '← 下排向西',font=f(24),fill='#b5d5e6')
    for name in [*(f'WAIT-{i:02}' for i in range(1,6)), 'CHARGE-01','CHARGE-02','DUMPER-001']:
        n=ns[name];x,y=pos(n['x'],n['y']);wait=name.startswith('WAIT');charge=name.startswith('CHARGE')
        color='#4cddd0' if wait else '#ffd76a' if charge else '#ff7e9a' if name=='DUMPER-001' else '#a3b7ca'
        d.rounded_rectangle((x-.4*scale,y-.26*scale,x+.4*scale,y+.26*scale),8,fill='#103244',outline=color,width=4)
        label='待命 '+name[-2:] if wait else '充电 '+name[-2:] if charge else '倒料对接' if name=='DUMPER-001' else '处置设计位'
        size=24 if wait or charge else 20;d.text((x-49,y-16),label,font=f(size),fill=color)
    # Every intersection gets a visible dot; business symbols are separate.
    for n in ns.values():
        if n['id'].startswith('CAD2-') and 979.8<=n['x']<=989.8 and -433.8<=n['y']<=-423.5:
            x,y=pos(n['x'],n['y']);d.ellipse((x-4,y-4,x+4,y+4),fill='#e3f3ff')
    x=1930;d.line((x-35,185,x-35,1850),fill='#29465e',width=2)
    lines=[('线路安排','#edf6ff'),('橙色：左侧进区','#ffb064'),('青色：右侧出区','#37d8c2'),('蓝色：待命横道与泊位支线','#77baff'),('黄色：两个独立充电支线','#ffd76a'),('红色：设备西侧倒料对接','#ff7e9a'),
        ('换道短段双向互斥，主线单向','#b4cadd'),('底部横段单向连接进区与返回线','#b4cadd'),('', '#b4cadd'),('点位修正','#edf6ff'),('五个待命位、两个充电位：','#b4cadd'),('使用 CAD 泊位矩形中心','#b4cadd'),('倒料短支线原路倒退至返回线','#b4cadd'),('倒料：区分设备轮廓与车停点','#b4cadd'),('车辆停在设备西侧专用支线','#b4cadd'),('', '#b4cadd'),('当前边界','#edf6ff'),('保留 305 个 CNC 和六组双横线','#b4cadd'),('方向为本次设计，未下发现场','#b4cadd'),('倒料对接姿态、转弯净空待核定','#b4cadd'),('本图未声称动态仿真已通过','#b4cadd')]
    for i,(text,color) in enumerate(lines):d.text((x,210+i*65),text,font=f(28 if i in (0,9,16) else 25),fill=color)
    d.text((80,1880),'车体符号按 800×520 mm 绘制；灰网格 1 m。CAD 原始坐标 / 1000，不旋转、不镜像。',font=f(26),fill='#a2bdcf')
    output=root/'terminal-redesign.png';im.save(output);return output

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True)
    print(render(p.parse_args().root).resolve())
