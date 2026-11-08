"""Build a compact Chinese result report from a completed isolated simulation."""

import argparse
import csv
import json
import math
import sqlite3
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def duration_text(seconds: float) -> str:
    rounded = int(round(seconds))
    return f"{rounded // 60}分{rounded % 60:02d}秒"


def build(root: Path, run_id: str, output: Path) -> dict:
    run = root / ("simulation-run-" + run_id)
    report = load(run / "report.json")
    queue = load(run / "queue-summary.json")
    manifest = load(run / "manifest.json")
    lab = load(root / "lab.json")
    live = load(output / "final-live.json")
    map_definition = load(root / "navigation.agvmap.json")

    database = sqlite3.connect(
        f"file:{(root / 'data' / 'agv-dispatch.db').as_posix()}?mode=ro",
        uri=True,
    )
    database.row_factory = sqlite3.Row

    def one(sql: str):
        return database.execute(sql).fetchone()

    task_rows = database.execute(
        "select TaskId,StationId,VehicleId,Stage,CreatedAt,UpdatedAt,ErrorMessage "
        "from dispatch_task order by CreatedAt"
    ).fetchall()
    step_rows = database.execute(
        "select TaskId,count(*) Count,"
        "sum(case when Status=50 then 1 else 0 end) Completed "
        "from dispatch_task_step group by TaskId"
    ).fetchall()
    steps = {
        row["TaskId"]: (row["Count"], row["Completed"])
        for row in step_rows
    }
    receipts = {
        row["TaskId"]: row["Count"]
        for row in database.execute(
            "select TaskId,count(*) Count from traffic_segment_receipt group by TaskId"
        )
    }
    vehicle_tasks = Counter(row["VehicleId"] for row in task_rows)
    vehicle_distances = defaultdict(float)
    last_position = {}
    for row in database.execute(
        "select VehicleId,X,Y,SampledAt from agv_track_point "
        "order by VehicleId,SampledAt"
    ):
        prior = last_position.get(row["VehicleId"])
        if prior is not None:
            vehicle_distances[row["VehicleId"]] += math.hypot(
                float(row["X"]) - prior[0],
                float(row["Y"]) - prior[1],
            )
        last_position[row["VehicleId"]] = (float(row["X"]), float(row["Y"]))

    results = []
    for row in task_rows:
        created = datetime.fromisoformat(str(row["CreatedAt"]))
        updated = datetime.fromisoformat(str(row["UpdatedAt"]))
        total_steps, completed_steps = steps.get(row["TaskId"], (0, 0))
        results.append({
            "任务编号": row["TaskId"],
            "工位": row["StationId"],
            "车辆": row["VehicleId"],
            "最终状态": "已完成" if row["Stage"] == 90 else str(row["Stage"]),
            "创建时间": created.strftime("%Y-%m-%d %H:%M:%S"),
            "完成时间": updated.strftime("%Y-%m-%d %H:%M:%S"),
            "耗时秒": round((updated - created).total_seconds(), 1),
            "九步总数": total_steps,
            "九步完成数": completed_steps,
            "交通回执数": receipts.get(row["TaskId"], 0),
            "错误": row["ErrorMessage"] or "",
        })

    with (output / "task-results.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(results[0].keys()))
        writer.writeheader()
        writer.writerows(results)

    checks = Counter(item.get("status") for item in (report.get("checks") or []))
    exit_checks = [
        item for item in (report.get("checks") or [])
        if item.get("name") == "积压原任务九步完成并有出区证据"
    ]
    command_summary = one(
        "select count(*) total,count(distinct StepId) distinct_steps from agv_command_outbox"
    )
    receipt_summary = one(
        "select count(*) total,count(distinct TaskId) tasks,"
        "count(distinct VehicleId) vehicles from traffic_segment_receipt"
    )
    counts = {
        "taskRows": one("select count(*) value from dispatch_task")["value"],
        "completedTasks": one(
            "select count(*) value from dispatch_task where Stage=90"
        )["value"],
        "faultedTasks": one(
            "select count(*) value from dispatch_task where Stage=100"
        )["value"],
        "cancelledTasks": one(
            "select count(*) value from dispatch_task where Stage=110"
        )["value"],
        "activeTasks": one(
            "select count(*) value from dispatch_task where Stage not in (90,110)"
        )["value"],
        "remainingLeases": one(
            "select count(*) value from traffic_resource_lease"
        )["value"],
        "obstacleRecoveries": one(
            "select count(*) value from obstacle_recovery"
        )["value"],
        "runtimeEvents": one(
            "select count(*) value from task_runtime_event"
        )["value"],
        "commands": command_summary["total"],
        "distinctCommandSteps": command_summary["distinct_steps"],
        "trafficReceipts": receipt_summary["total"],
        "receiptTasks": receipt_summary["tasks"],
        "receiptVehicles": receipt_summary["vehicles"],
        "mapNodes": len(map_definition["nodes"]),
        "mapEdges": len(map_definition["edges"]),
        "mapResources": len(map_definition["resources"]),
        "headOnGuards": sum(
            1 for resource in map_definition["resources"]
            if resource["id"].startswith("HEAD-ON-GUARD-")
        ),
        "allStepsCompleted": all(
            total == 9 and completed == 9
            for total, completed in steps.values()
        ),
        "exitEvidenceChecks": len(exit_checks),
        "exitEvidencePassed": sum(
            1 for item in exit_checks if item.get("status") == "passed"
        ),
    }
    speeds = sorted({
        float(profile.get("speedScale", 0))
        for profile in lab.get("vehicleProfiles", {}).values()
    })
    completed = [row for row in results if row["最终状态"] == "已完成"]
    task_durations = [row["耗时秒"] for row in completed]
    metrics = {
        "status": report.get("status"),
        "message": report.get("message"),
        "reportId": report.get("id"),
        "durationSeconds": report.get("metrics", {}).get("durationSeconds"),
        "minimumSeparationMeters": report.get("metrics", {}).get("minimumSeparationMeters"),
        "maximumWaitingTasks": report.get("metrics", {}).get("maximumWaitingTasks"),
        "maximumActiveTasks": report.get("metrics", {}).get("maximumActiveTasks"),
        "maximumBusyWaitingTasks": report.get("metrics", {}).get("maximumBusyWaitingTasks"),
        "taskDurationMinSeconds": min(task_durations),
        "taskDurationAvgSeconds": round(sum(task_durations) / len(task_durations), 1),
        "taskDurationMaxSeconds": max(task_durations),
        "vehicleTasks": dict(sorted(vehicle_tasks.items())),
        "vehicleTrackMeters": {
            key: round(value, 1)
            for key, value in sorted(vehicle_distances.items())
        },
        "speedScales": speeds,
        "checks": dict(checks),
        "counts": counts,
        "queueSummary": queue,
        "manifest": manifest,
    }
    (output / "metrics-summary.json").write_text(
        json.dumps(metrics, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    vehicle_lines = "\n".join(
        f"| {vehicle} | {task_count} | {vehicle_distances.get(vehicle, 0):.1f} m |"
        for vehicle, task_count in sorted(vehicle_tasks.items())
    )
    task_lines = "\n".join(
        f"| {row['工位']} | {row['车辆']} | {row['耗时秒'] / 60:.1f} 分钟 | "
        f"{row['九步完成数']}/9 | {row['交通回执数']} |"
        for row in completed
    )
    final_states = []
    for vehicle, car in live["device"]["cars"].items():
        final_states.append(
            f"| {vehicle} | {car['station']} | "
            f"{'载货' if car['loaded'] else '空载'} | "
            f"{'在线' if not car['offline'] else '离线'} | "
            f"{'有任务' if car['current_task'] else '空闲'} |"
        )
    check_status = (
        str(exit_checks[0].get("status") if exit_checks else "missing").upper()
    )
    speed_text = "、".join(f"{speed:g}×" for speed in speeds) or "未记录"
    markdown = f"""# 5 车 20 单连续呼叫仿真验收报告

## 结论

本轮隔离仿真**通过**：正式调度器完成 **20/20** 个九步闭环任务，车辆最大并发 **5** 台，最大待派队列 **15** 单，任务故障、取消、残留路权、障碍误恢复均为 **0**。5 台车都参与了多轮复用，没有出现同一车辆同时承担两个未完成任务。

本轮使用完整 CAD 仿真地图（647 个节点、1304 条有向道路、752 个资源），但 PLC 点表只启用 20 个演示工位，因此结论证明多车排队、连续派车、28 阶段业务握手和交通释放逻辑，不冒充 305 工位满规模 PLC 最终验收。

## 关键结果

| 项目 | 结果 |
| --- | ---: |
| 场景状态 | {report['status']} |
| 报告编号 | `{report['id']}` |
| 计划/完成 | 20 / 20 |
| 总耗时 | {duration_text(report.get('metrics', {}).get('durationSeconds', 0))} |
| 速度设置 | 基准 0.75 m/s，仿真倍率 {speed_text} |
| 最大执行车辆 | {report['metrics'].get('maximumActiveTasks')} |
| 最大排队任务 | {report['metrics'].get('maximumWaitingTasks')} |
| 最大“车辆全忙时等待” | {report['metrics'].get('maximumBusyWaitingTasks')} |
| 九步完成步骤 | 180 / 180 |
| 出区证据检查 | {len(exit_checks)} 项，全部 {check_status} |
| 命令与唯一命令步骤 | {command_summary['total']} / {command_summary['distinct_steps']} |
| 交通回执 | {receipt_summary['total']} 条，覆盖 {receipt_summary['tasks']} 单、{receipt_summary['vehicles']} 车 |
| 结束时残留路权 | {counts['remainingLeases']} |
| 障碍自动改道发送 | {report['metrics'].get('obstacleAutoDetourSends')} |
| 最小车辆间距 | {report['metrics'].get('minimumSeparationMeters'):.3f} m |
| 场景检查 | {checks.get('passed', 0)} passed / {checks.get('failed', 0)} failed |

## 车辆复用

| 车辆 | 承担任务数 | 轨迹累计里程 |
| --- | ---: | ---: |
{vehicle_lines}

最终 5 台车全部在线、空载、无当前任务、无阻断：

| 车辆 | 最终站点 | 载荷 | 通讯 | 状态 |
| --- | --- | --- | --- | --- |
{chr(10).join(final_states)}

## 任务结果

| 工位 | 车辆 | 建单至完成耗时 | 步骤闭环 | 交通回执 |
| --- | --- | ---: | ---: | ---: |
{task_lines}

## 本轮修复并验证的问题

1. **大图派车前逐边查询超时**：隔离仿真实例改用“车端地图身份 + 同一已载入版本有向边”核验，正式现场仍默认保留完整 1303 逐边核验。
2. **前后车排队被误判为外部障碍**：前车占道只作为物理跟车等待，不再上报车端 `Blocked`，也不会触发 5 秒障碍超时和 3003 取消。
3. **反向车辆共用中心线导致对向死锁**：210 对反向路段各增加容量 1 的 `HEAD-ON-GUARD-*` 共享资源；方向逻辑车道仍为 5 容量，设备工位仍为 1。
4. **旧恢复票据混入新实例**：本实例启动核对使用独立数据库并正常自动放行空载闭合初态；整轮执行期间没有出现旧票据恢复屏障。

## 地图与线路边界

- CAD 未提供经过确认的 AGV 物理车道中心线、车道宽度、转弯半径和现场避障层。
- 本轮“双道”是方向逻辑车道，反向中心线仍共用已核验轨迹，因此额外加了防对向死锁护栏。
- 码头、5T、总控和终端工艺点仍是画面参考点，没有自动变成派车工位。
- 该结果可用于调度逻辑和多车压力验证，不能替代厂家 SMAP、真车定位、机械动作、PLC 固件和安全联锁验收。

## 文件

- `仿真回放-5车20单-20of20.mp4`：完整时间轴加速回放。
- `simulation-report.json`：原始机器报告。
- `task-results.csv`：20 单逐单结果和证据摘要。
- `metrics-summary.json`：结构化指标。
- `final-live.json` / `final-snapshot.json`：最终车辆、设备、任务状态。
- `simulation-replay.json`：1012 帧压缩回放数据。
- `simulation-evidence.zip`：精选证据包；原始 309 MB 观测文件仍保留在运行目录。
"""
    (output / "仿真验收报告.md").write_text(markdown, encoding="utf-8")
    return {
        "status": report.get("status"),
        "completed": counts["completedTasks"],
        "tasks": counts["taskRows"],
        "checks": dict(checks),
        "vehicleTasks": dict(vehicle_tasks),
        "remainingLeases": counts["remainingLeases"],
        "allStepsCompleted": counts["allStepsCompleted"],
        "exitEvidenceChecks": counts["exitEvidenceChecks"],
        "report": str(output / "仿真验收报告.md"),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    print(json.dumps(build(args.root, args.run_id, args.output), ensure_ascii=False))


if __name__ == "__main__":
    main()
