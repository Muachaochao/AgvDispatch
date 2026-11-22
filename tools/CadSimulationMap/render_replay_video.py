#!/usr/bin/env python3
"""Render an accelerated MP4 from a SimulationLab replay JSON."""

from __future__ import annotations

import argparse
import json
import math
import subprocess
from collections import defaultdict, deque
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw, ImageFont


NODE_COLORS = {
    "Station": "#38bdf8",
    "WaitingPoint": "#22d3ee",
    "ChargingPoint": "#a78bfa",
    "Intersection": "#475569",
    "Waypoint": "#334155",
}
STAGE_NAMES = {
    "WaitingForVehicle": "等待派车",
    "SubmittingToAgv": "提交车辆任务",
    "GoingToPickup": "前往取箱工位",
    "PickingUp": "取满箱",
    "GoingToDumper": "送往倒料终端",
    "WaitingForDumper": "等待倒料机",
    "Dumping": "倒料",
    "ReturningEmptyBox": "返送空箱",
    "WaitingForPlcConfirmation": "等待PLC收尾",
    "Completed": "已完成",
    "Faulted": "故障",
    "Cancelled": "已取消",
}


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    names = [
        "C:/Windows/Fonts/msyhbd.ttc" if bold else "C:/Windows/Fonts/msyh.ttc",
        "C:/Windows/Fonts/simhei.ttf",
        "C:/Windows/Fonts/arial.ttf",
    ]
    for name in names:
        if Path(name).exists():
            return ImageFont.truetype(name, size)
    return ImageFont.load_default()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def point_map(nodes: list[dict[str, Any]], bounds: tuple[float, float, float, float],
              viewport: tuple[float, float, float, float]):
    min_x, min_y, max_x, max_y = bounds
    left, top, width, height = viewport
    scale = min(width / max(0.001, max_x - min_x), height / max(0.001, max_y - min_y))
    used_w = (max_x - min_x) * scale
    used_h = (max_y - min_y) * scale
    ox = left + (width - used_w) / 2
    oy = top + (height - used_h) / 2

    def transform(x: float, y: float) -> tuple[float, float]:
        return ox + (x - min_x) * scale, oy + (max_y - y) * scale

    return transform


def draw_wrapped(draw: ImageDraw.ImageDraw, text: str, xy: tuple[int, int],
                 max_width: int, text_font: ImageFont.FreeTypeFont, fill: str,
                 line_gap: int = 6, max_lines: int = 4) -> int:
    lines: list[str] = []
    current = ""
    for char in text:
        candidate = current + char
        if draw.textbbox((0, 0), candidate, font=text_font)[2] <= max_width:
            current = candidate
        else:
            if current:
                lines.append(current)
            current = char
            if len(lines) >= max_lines:
                break
    if current and len(lines) < max_lines:
        lines.append(current)
    if len(lines) == max_lines:
        last = lines[-1]
        while draw.textbbox((0, 0), last + "…", font=text_font)[2] > max_width and len(last) > 1:
            last = last[:-1]
        lines[-1] = last + "…"
    y = xy[1]
    for line in lines:
        draw.text((xy[0], y), line, font=text_font, fill=fill)
        y += text_font.size + line_gap
    return y


def rotated_rectangle(x: float, y: float, angle: float, length_m: float,
                      width_m: float, transform) -> list[tuple[float, float]]:
    # Replay angles are world radians; screen Y is inverted.
    screen_angle = -angle
    c, s = math.cos(screen_angle), math.sin(screen_angle)
    corners = [(-length_m / 2, -width_m / 2), (length_m / 2, -width_m / 2),
               (length_m / 2, width_m / 2), (-length_m / 2, width_m / 2)]
    center = transform(x, y)
    # Convert metres to the same screen scale as the map transformation.
    probe = transform(x + 1, y)
    pixel_per_m = math.hypot(probe[0] - center[0], probe[1] - center[1])
    result = []
    for dx, dy in corners:
        rx = dx * c - dy * s
        ry = dx * s + dy * c
        result.append((center[0] + rx * pixel_per_m, center[1] + ry * pixel_per_m))
    return result


def render_map(draw: ImageDraw.ImageDraw, replay: dict[str, Any],
               viewport: tuple[float, float, float, float],
               trail: dict[str, deque[tuple[float, float]]]):
    map_definition = replay["map"]
    nodes = {node["id"]: node for node in map_definition["nodes"]}
    bounds = (
        min(node["x"] for node in nodes.values()),
        min(node["y"] for node in nodes.values()),
        max(node["x"] for node in nodes.values()),
        max(node["y"] for node in nodes.values()),
    )
    padding = max(1.0, max(bounds[2] - bounds[0], bounds[3] - bounds[1]) * .07)
    bounds = (bounds[0] - padding, bounds[1] - padding,
              bounds[2] + padding, bounds[3] + padding)
    transform = point_map(list(nodes.values()), bounds, viewport)
    left, top, width, height = viewport
    draw.rounded_rectangle((left, top, left + width, top + height), radius=14,
                           fill="#071321", outline="#24415d", width=2)
    for edge in map_definition["edges"]:
        if not edge.get("enabled", True) or edge["from"] not in nodes or edge["to"] not in nodes:
            continue
        a = nodes[edge["from"]]
        b = nodes[edge["to"]]
        draw.line((*transform(a["x"], a["y"]), *transform(b["x"], b["y"])),
                  fill="#18344d", width=1)
    for node in nodes.values():
        x, y = transform(node["x"], node["y"])
        radius = 1.5
        color = NODE_COLORS.get(node.get("type"), "#475569")
        if str(node["id"]).startswith("BUS-CNC-"):
            radius = 2.2
            color = "#5cc8ff"
        elif str(node["id"]).startswith(("WAIT-", "CHARGE-", "DUMPER", "INSPECT")):
            radius = 4
            color = NODE_COLORS.get(node.get("type"), "#f8fafc")
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)
        site = node.get("agvSiteId")
        station_numbers = {"LM1": 1, "LM3": 2, "LM5": 3, "LM7": 4,
                           "LM9": 5, "LM11": 6, "LM13": 7}
        if site in station_numbers:
            draw.text((x + 4, y - 14), f"CNC-{station_numbers[site]:03d}",
                      font=font(10), fill="#c9efff")
        elif site in {"LM35", "LM36", "LM39", "LM40"}:
            draw.text((x + 4, y - 14), site, font=font(10), fill="#a9dff2")
    return transform, bounds


def render_frame(replay: dict[str, Any], index: int, total: int,
                 report: dict[str, Any], trail: dict[str, deque[tuple[float, float]]]) -> Image.Image:
    frame = replay["timeline"][index]
    width, height = 1600, 900
    image = Image.new("RGB", (width, height), "#030a12")
    draw = ImageDraw.Draw(image)
    title_font, normal, small, tiny = font(34, True), font(18), font(14), font(12)
    vehicle_ids = sorted({str(vehicle.get("vehicleId")) for vehicle in frame.get("vehicles", [])
                          if vehicle.get("vehicleId")})
    station_count = len(frame.get("tasks") or [])
    map_name = replay["map"].get("name") or "隔离仿真地图"
    draw.text((40, 24), f"{map_name} · {len(vehicle_ids)}车{station_count}工位仿真闭环",
              fill="#eaf7ff", font=title_font)
    draw.text((41, 70), "正式调度 + 回环 Robokit / S7 · 基准 0.75 m/s · 加速回放 · 整车包络",
              fill="#7f9bb8", font=small)
    map_view = (32, 108, 1120, 670)
    transform, bounds = render_map(draw, replay, map_view, trail)

    for vehicle in frame.get("vehicles", []):
        vehicle_id = vehicle.get("vehicleId")
        if not vehicle_id:
            continue
        x, y = float(vehicle["x"]), float(vehicle["y"])
        trail[vehicle_id].append((x, y))
        if len(trail[vehicle_id]) > 2:
            path = [transform(px, py) for px, py in trail[vehicle_id]]
            draw.line(path, fill="#f59e0b", width=3)
        length, width_m = (1.0, .545) if vehicle_id == "AGV-03" else (.81, .545)
        polygon = rotated_rectangle(x, y, float(vehicle.get("angle") or 0), length, width_m, transform)
        active = bool(vehicle.get("taskId"))
        draw.polygon(polygon, fill="#f59e0b" if active else "#22c55e",
                     outline="#fff7d6" if active else "#d1fae5")
        vx, vy = transform(x, y)
        draw.text((vx + 7, vy - 9), vehicle_id, font=tiny, fill="#08111c")

    side_x = 1175
    draw.rounded_rectangle((side_x, 108, 1568, 778), radius=14, fill="#071321",
                           outline="#24415d", width=2)
    draw.text((side_x + 20, 132), "运行状态", font=font(22, True), fill="#e8f6ff")
    elapsed = (float(frame["time"]) - float(replay["timeline"][0]["time"]))
    actual_duration = (float(replay["timeline"][-1]["time"]) - float(replay["timeline"][0]["time"]))
    speedup = actual_duration / max(.001, actual_duration * (index + 1) / total)
    draw.text((side_x + 20, 170),
              f"仿真时刻  {int(elapsed)//60:02d}:{int(elapsed)%60:02d} / "
              f"{int(actual_duration)//60:02d}:{int(actual_duration)%60:02d}",
              font=normal, fill="#d7e9f5")
    progress = min(1.0, elapsed / max(.001, actual_duration))
    draw.text((side_x + 20, 202), f"回放进度 {int(progress * 100):3d}%",
              font=small, fill="#7f9bb8")
    draw.rounded_rectangle((side_x + 20, 228, 1548, 240), radius=6, fill="#10253a")
    progress_right = side_x + 20 + 508 * progress
    draw.rounded_rectangle((side_x + 20, 228, progress_right, 240), radius=6, fill="#22d3ee")

    tasks = frame.get("tasks") or []
    active_tasks = [task for task in tasks if task.get("stage") not in
                    ("Completed", "Cancelled", "Faulted")]
    draw.text((side_x + 20, 270), "车辆状态", font=normal, fill="#8ce4f2")
    task_by_vehicle = {task.get("vehicleId"): task for task in tasks if task.get("vehicleId")}
    vehicle_y = 302
    for vehicle in frame.get("vehicles", []):
        vehicle_id = vehicle.get("vehicleId")
        task = task_by_vehicle.get(vehicle_id)
        status = STAGE_NAMES.get(task.get("stage"), task.get("stage")) if task else "空闲"
        location = vehicle.get("site") or ("任务结束" if status == "已完成" else "行驶中")
        draw.text((side_x + 20, vehicle_y),
                  f"{vehicle_id}  {location}  {status}",
                  font=small, fill="#f0f8ff")
        vehicle_y += 28
    draw.text((side_x + 20, 402),
              f"任务队列  {len(active_tasks)} 进行 / {len(tasks) - len(active_tasks)} 完成",
              font=small, fill="#a9bdce")
    task_y = 434
    for task in tasks[:7]:
        stage = STAGE_NAMES.get(task.get("stage"), task.get("stage") or "待派车")
        draw.text((side_x + 20, task_y),
                  f"{task.get('stationId') or '工位'}  {task.get('vehicleId') or '待派'}  {stage}",
                  font=tiny, fill="#d4e6f2")
        task_y += 24
    representative = (next((task for task in active_tasks if task.get("vehicleId")), None)
                      or (active_tasks[0] if active_tasks else {}))
    draw.text((side_x + 20, 608), "当前等待原因", font=small, fill="#7f9bb8")
    draw_wrapped(draw, representative.get("waitReason") or "无等待", (side_x + 20, 636),
                 350, tiny, "#f3c46f", line_gap=4, max_lines=2)
    draw.text((side_x + 20, 706),
              f"交通租约 {len(frame.get('leases') or []):02d} · 车辆 {len(frame.get('vehicles') or []):02d}",
              font=small, fill="#a9bdce")
    metrics = report.get("metrics") or {}
    judgement = ("通过" if report.get("status") == "passed" else "未通过")
    draw.text((side_x + 20, 740),
              f"最终判定  {judgement} · {metrics.get('completedRounds', 0)}/"
              f"{metrics.get('taskCount', 0)} 九步闭环",
              font=font(16, True), fill="#56e0ad" if judgement == "通过" else "#ff8f8f")

    draw.rectangle((0, 824, width, height), fill="#071321")
    draw.text((32, 840),
              "道路和车辆来自回放快照；PIL 视频用于快速复核，不替代原始 JSON、日志和现场安全验收。",
              font=small, fill="#7894ad")
    draw.text((32, 866),
              f"报告 {report.get('id', replay.get('id'))} · 采样 {len(replay['timeline'])} 帧 · "
              f"源视频加速约 {max(1, round(actual_duration/45))}×",
              font=tiny, fill="#55728b")
    return image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--replay", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--frames", type=int, default=360)
    parser.add_argument("--fps", type=int, default=8)
    args = parser.parse_args()
    replay, report = load(args.replay), load(args.report)
    timeline = replay["timeline"]
    count = min(max(2, args.frames), len(timeline))
    selections = [round(index * (len(timeline) - 1) / (count - 1)) for index in range(count)]
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "image2pipe", "-framerate", str(args.fps), "-i", "-",
        "-c:v", "libx264", "-preset", "medium", "-crf", "21",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(args.output),
    ]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    process = subprocess.Popen(command, stdin=subprocess.PIPE)
    assert process.stdin is not None
    trail: dict[str, deque[tuple[float, float]]] = defaultdict(lambda: deque(maxlen=90))
    try:
        for position, selection in enumerate(selections):
            image = render_frame(replay, selection, count, report, trail)
            image.save(process.stdin, format="PNG")
            if position % 30 == 0:
                print(f"Rendered {position + 1}/{count}", flush=True)
    finally:
        process.stdin.close()
    code = process.wait()
    if code:
        raise SystemExit(code)
    print(json.dumps({"output": str(args.output.resolve()), "frames": count,
                      "fps": args.fps, "bytes": args.output.stat().st_size}, ensure_ascii=False))


if __name__ == "__main__":
    main()
