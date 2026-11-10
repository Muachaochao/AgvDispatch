"""Stream a preserved factory run into a Chinese MP4 and sampled replay.

This is an evidence replay from our simulator, not a ProfControl screen recording.
Original observations are kept intact. Geometry comes from this run's own map
and fleet configuration, never from hard-coded vehicle IDs or the current map.
"""
import argparse
from collections import Counter, defaultdict, deque
import json
import math
from pathlib import Path
import subprocess

from PIL import Image, ImageDraw, ImageFont


STAGES = {'WaitingForVehicle': '等待派车', 'SubmittingToAgv': '提交导航',
          'GoingToPickup': '前往取箱', 'PickingUp': '取箱确认', 'GoingToDumper': '送满箱',
          'WaitingForDumper': '候车倒料', 'Dumping': '倒料交接', 'ReturningEmptyBox': '送回空箱',
          'WaitingForPlcConfirmation': '工位收尾', 'Completed': '完成', 'Faulted': '故障',
          'Cancelled': '取消', 'CancellationReview': '取消核对'}
COLORS = ['#38bdf8', '#fbbf24', '#a78bfa', '#34d399', '#fb7185', '#f97316', '#e879f9', '#94a3b8']


def load(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def font(size):
    return ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', size)


def sample_run(run, maximum):
    source = run / 'observations.jsonl'
    with source.open('rb') as stream:
        count = sum(1 for _ in stream)
    if not count:
        raise ValueError('没有采样，不能生成运行视频。')
    stride = max(1, math.ceil(count / maximum))
    frames = []
    with source.open('rb') as stream:
        for index, line in enumerate(stream):
            if index % stride and index != count-1:
                continue
            row = json.loads(line)
            cars = [dict(vehicleId=k, **{key: v.get(key) for key in
                    ('x', 'y', 'angle', 'station', 'loaded', 'blocked', 'blockedByVehicle',
                     'waitReason', 'current_task', 'speedScale', 'batteryPercent', 'motionEdge')})
                    for k, v in row.get('device', {}).get('cars', {}).items()]
            frames.append(dict(time=row['time'], vehicles=cars, tasks=row.get('tasks', {}),
                               leaseCount=len(row.get('leases', []))))
    return count, frames


def transform(bounds, viewport):
    xmin, ymin, xmax, ymax = bounds
    left, top, width, height = viewport
    scale = min(width / max(.01, xmax-xmin), height / max(.01, ymax-ymin))
    ox, oy = left + (width - (xmax-xmin)*scale)/2, top + (height - (ymax-ymin)*scale)/2
    return lambda x, y: (ox+(x-xmin)*scale, oy+(ymax-y)*scale), scale


def clipped_segment(a, b, bounds):
    dx, dy = b['x']-a['x'], b['y']-a['y']
    low, high = 0., 1.
    for p, q in ((-dx,a['x']-bounds[0]),(dx,bounds[2]-a['x']),
                 (-dy,a['y']-bounds[1]),(dy,bounds[3]-a['y'])):
        if abs(p) < 1e-12:
            if q < 0: return None
        elif p < 0: low = max(low,q/p)
        else: high = min(high,q/p)
        if low > high: return None
    return ((a['x']+low*dx,a['y']+low*dy),(a['x']+high*dx,a['y']+high*dy))


def short_site(site):
    site = site or '位置待核对'
    return site.replace('DUMPER-001', '倒料位').replace('WAIT-', '候车位').replace('CHARGE-', '充电位')


def frame_image(navigation, fleet, frames, index, report, trails):
    frame = frames[index]
    img = Image.new('RGB', (1920, 1080), '#07111d')
    draw = ImageDraw.Draw(img)
    nodes = {n['id']: n for n in navigation['nodes']}
    tasks = frame['tasks'] if 'sampleMode' not in frame['tasks'] else {}
    total_cnc = sum(n.get('agvSiteId', '').startswith('CNC-') for n in nodes.values())
    status = {'passed': '通过', 'failed': '未通过', 'aborted': '已中止', 'running': '运行中'}.get(report.get('status'), '未判定')
    draw.text((24, 12), f'成都全厂仿真  |  {total_cnc} 个 CNC · {len(frame["vehicles"])} 辆车 · 本批 {len(tasks)} 单', font=font(27), fill='#e2edf7')
    draw.text((24, 51), '按本轮地图与车辆尺寸回放 · 仿真设计道路 · 加速回看，不作为现场产能结论', font=font(17), fill='#8da9c0')

    def map_panel(bounds, viewport, label):
        x, y, w, h = viewport
        draw.rounded_rectangle((x, y, x+w, y+h), radius=12, fill='#0b1b2b', outline='#25425b', width=2)
        draw.text((x+12, y+8), label, font=font(17), fill='#9fbcd3')
        point, scale = transform(bounds, (x+16, y+38, w-32, h-54))
        def inside(n):
            return bounds[0] <= n['x'] <= bounds[2] and bounds[1] <= n['y'] <= bounds[3]
        for edge in navigation['edges']:
            a, b = nodes[edge['from']], nodes[edge['to']]
            segment = clipped_segment(a,b,bounds)
            if segment:
                path = (*point(*segment[0]), *point(*segment[1]))
                draw.line(path, fill='#35566d', width=2)
                if any(c.get('blocked') and c.get('motionEdge') == edge['id'] for c in frame['vehicles']):
                    draw.line(path, fill='#fb923c', width=4)
        label_boxes = []
        def place_label(px, py, text, fill, size):
            f = font(size)
            for dx, dy in ((6,-20),(6,7),(-92,-20),(-92,7),(6,-39),(-92,-39),(6,26)):
                box = draw.textbbox((px+dx, py+dy), text, font=f)
                if box[0] < x+8 or box[2] > x+w-8 or box[1] < y+34 or box[3] > y+h-8:
                    continue
                if any(box[0] < b[2]+3 and box[2] > b[0]-3 and box[1] < b[3]+2 and box[3] > b[1]-2 for b in label_boxes):
                    continue
                label_boxes.append(box)
                draw.text((px+dx,py+dy),text,font=f,fill=fill)
                return
        for n in nodes.values():
            if not inside(n):
                continue
            px, py = point(n['x'], n['y'])
            kind = n.get('type')
            color = '#9b8af5' if kind == 'ChargingPoint' else '#25bfb8' if kind == 'WaitingPoint' else '#56a7c5'
            radius = 3 if kind in ('Station', 'WaitingPoint', 'ChargingPoint') else 1
            draw.ellipse((px-radius, py-radius, px+radius, py+radius), fill=color)
            sid = n.get('agvSiteId', '')
            if (sid.startswith(('WAIT-', 'CHARGE-')) and len(sid.split('-')) == 2 or sid == 'DUMPER-001') and not label.startswith('全厂'):
                place_label(px, py, short_site(sid), color, 14)
            elif kind == 'Station' and sid.startswith('CNC-') and (label.startswith('受阻') or sid.endswith(('001','024','305')) or int(sid[-3:]) % 25 == 0):
                place_label(px, py, sid, '#a6cadb', 12)
        for number, car in enumerate(frame['vehicles']):
            if not all(type(car.get(k)) in (int, float) and math.isfinite(car[k]) for k in ('x','y','angle')) or not inside(car):
                continue
            profile = fleet.get(car['vehicleId'])
            if profile is None:
                raise ValueError('缺少本轮车体尺寸，拒绝用固定车型代替。')
            px, py = point(car['x'], car['y'])
            color = COLORS[number % len(COLORS)]
            if label.startswith('全厂'):
                pts = [point(a, b) for a, b in trails[car['vehicleId']]]
                # Sample decimation can straddle several turns; a straight
                # chord would invent movement across machines or other lanes.
                for tx, ty in pts:
                    draw.ellipse((tx-1,ty-1,tx+1,ty+1),fill=color)
            angle = -car['angle']
            c, s = math.cos(angle), math.sin(angle)
            length, width = profile['lengthMeters']*scale, profile['widthMeters']*scale
            def rotate(a,b): return px+a*c-b*s, py+a*s+b*c
            polygon = [rotate(a,b) for a,b in [(-length/2,-width/2),(length/2,-width/2),(length/2,width/2),(-length/2,width/2)]]
            draw.polygon(polygon, fill=color, outline='#f5faff')
            draw.line((*rotate(0,0), *rotate(length/2,0)), fill='#07111d', width=2)
            if car.get('loaded'):
                draw.ellipse((px-2,py-2,px+2,py+2), fill='#fff7ce')
            place_label(px, py, car['vehicleId'], color, 14)

    bounds = (min(n['x'] for n in nodes.values())-2, min(n['y'] for n in nodes.values())-2,
              max(n['x'] for n in nodes.values())+2, max(n['y'] for n in nodes.values())+2)
    for car in frame['vehicles']:
        trails[car['vehicleId']].append((car['x'], car['y']))
    map_panel(bounds, (20, 90, 1460, 620), '全厂路网与实际轨迹（保留本轮方向与站点）')
    terminals = [n for n in nodes.values() if n['id'].startswith(('WAIT-', 'CHARGE-', 'DUMP')) or n['id']=='DUMPER-001']
    tb = (min(n['x'] for n in terminals)-1, min(n['y'] for n in terminals)-1,
          max(n['x'] for n in terminals)+1, max(n['y'] for n in terminals)+1)
    blocked_car = next((c for c in frame['vehicles'] if c.get('blocked')), None)
    if blocked_car:
        peer = next((c for c in frame['vehicles'] if c['vehicleId'] == blocked_car.get('blockedByVehicle')), blocked_car)
        tb = (min(blocked_car['x'],peer['x'])-2, min(blocked_car['y'],peer['y'])-2,
              max(blocked_car['x'],peer['x'])+2, max(blocked_car['y'],peer['y'])+2)
        map_panel(tb, (20, 726, 1460, 330), '受阻位置放大：'+blocked_car['vehicleId']+' 等待 '+str(blocked_car.get('blockedByVehicle') or '障碍清除'))
    else:
        map_panel(tb, (20, 726, 1460, 330), '终端放大：候车、倒料交接与双充电位')
    draw.rounded_rectangle((1496,90,1900,1056), radius=12, fill='#0d2032', outline='#25425b', width=2)
    elapsed, duration = frame['time']-frames[0]['time'], frames[-1]['time']-frames[0]['time']
    draw.text((1514,110), f'仿真 {int(elapsed)//60:02}:{int(elapsed)%60:02} / {int(duration)//60:02}:{int(duration)%60:02}', font=font(24), fill='#e2edf7')
    draw.text((1514,150), f'本轮最终状态：{status}', font=font(21), fill='#34d399' if status=='通过' else '#fbbf24')
    by_vehicle = {}
    for task in tasks.values():
        vid = task.get('vehicleId')
        if vid and (vid not in by_vehicle or task.get('stage') not in ('Completed','Cancelled')):
            by_vehicle[vid] = task
    y = 208
    for i, car in enumerate(frame['vehicles']):
        task = by_vehicle.get(car['vehicleId'], {})
        draw.text((1514,y), car['vehicleId']+'  '+('车上有箱' if car.get('loaded') else '空载' if car.get('loaded') is False else '载荷待核对'), font=font(21), fill=COLORS[i % len(COLORS)])
        draw.text((1514,y+31), short_site(car.get('station'))[:28], font=font(16), fill='#b1c5d6')
        stage = STAGES.get(task.get('stage'), '空闲')
        draw.text((1514,y+57), f'{stage} · 车辆倍率 {car.get("speedScale", "待核对")}', font=font(16), fill='#b1c5d6')
        reason = ('被 '+str(car['blockedByVehicle'])+' 阻挡' if car.get('blockedByVehicle') else
                  '存在障碍，等待恢复' if car.get('blocked') else
                  '车辆已暂停' if car.get('paused') else '')
        if reason: draw.text((1514,y+78), reason, font=font(14), fill='#fb923c')
        y += 98
    counts = Counter(t.get('stage') for t in tasks.values())
    draw.text((1514,720), f'完成 {counts.get("Completed",0)} / {len(tasks)} · 路权 {frame["leaseCount"]}', font=font(21), fill='#e2edf7')
    y = 765
    for stage, count in counts.items():
        draw.text((1514,y), f'{STAGES.get(stage,stage)}：{count}', font=font(17), fill='#b1c5d6'); y += 28
    draw.text((1514,995), '画面来自调度仿真采样', font=font(17), fill='#8da9c0')
    draw.text((1514,1020), '不是第三方软件屏幕录像', font=font(17), fill='#8da9c0')
    return img


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--frames', type=int, default=480)
    parser.add_argument('--fps', type=int, default=12)
    args=parser.parse_args()
    report=load(args.run/'report.json')
    navigation=load(args.run/'map.json')
    config=load(args.run/'simulation-configuration.json')
    fleet={v['id']:v for v in config['vehicles']}
    count, frames=sample_run(args.run, max(2,args.frames))
    args.output.parent.mkdir(exist_ok=True, parents=True)
    replay=dict(sourceRun=str(args.run.resolve()), reportStatus=report['status'], totalSamples=count,
                sampledFrames=len(frames), map=navigation, vehicles=config['vehicles'], timeline=frames)
    args.output.with_suffix('.json').write_text(json.dumps(replay,ensure_ascii=False),encoding='utf-8')
    process=subprocess.Popen(['ffmpeg','-hide_banner','-loglevel','error','-y','-f','image2pipe',
        '-framerate',str(args.fps),'-i','-','-c:v','libx264','-preset','fast','-crf','22','-pix_fmt','yuv420p',
        '-movflags','+faststart',str(args.output)],stdin=subprocess.PIPE)
    trails=defaultdict(lambda:deque(maxlen=90))
    try:
        for index in range(len(frames)):
            image=frame_image(navigation,fleet,frames,index,report,trails)
            if index in (0,len(frames)//2,len(frames)-1):
                image.save(args.output.with_name(args.output.stem+f'-{index}.png'))
            image.save(process.stdin,format='PNG')
    finally:
        process.stdin.close()
    if process.wait(): raise RuntimeError('视频编码失败。')
    print(json.dumps(dict(video=str(args.output.resolve()),samples=count,frames=len(frames),status=report['status']),ensure_ascii=False))


if __name__=='__main__': main()
