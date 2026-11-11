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



def render_markdown(metrics, vehicle_lines, task_lines, final_states):
    """Render only measured results; task closure never overrides scenario failure."""
    counts = metrics['counts']
    status = metrics.get('status')
    verdict = {'passed': '通过', 'failed': '未通过', 'aborted': '已中止'}.get(status, '未完成')
    return f"""# 隔离仿真运行报告

本轮场景**{verdict}**。业务任务完成 **{counts['completedTasks']}/{counts['taskRows']}**；业务完成数与场景验收结论分别记录。

场景说明：{metrics.get('message') or '无附加说明'}

| 项目 | 实测结果 |
| --- | --- |
| 原始场景状态 | {status} |
| 报告编号 | {metrics.get('reportId')} |
| 场景耗时 | {duration_text(metrics.get('durationSeconds') or 0)} |
| 最长活动步骤停留 | {duration_text(metrics['longestUnchangedStepSeconds']) if metrics.get('longestUnchangedStepSeconds') is not None else '未记录'} |
| 速度配置值 | {metrics.get('speedScales')}（原始配置值，不推定物理单位） |
| 道路限速（米/秒） | {metrics.get('routeSpeedLimits')} |
| 模型各档速度上限（米/秒） | {metrics.get('modelSpeedUpperBounds')} |
| 最大执行任务 | {metrics.get('maximumActiveTasks')} |
| 最大待派任务 | {metrics.get('maximumWaitingTasks')} |
| 全部车辆忙碌时最大待派 | {metrics.get('maximumBusyWaitingTasks')} |
| 九步全部完成 | {counts['allStepsCompleted']} |
| 出区证据通过/检查数 | {counts['exitEvidencePassed']}/{counts['exitEvidenceChecks']} |
| 故障 / 取消 / 未闭合任务 | {counts['faultedTasks']} / {counts['cancelledTasks']} / {counts['activeTasks']} |
| 结束时路权记录数 | {counts['remainingLeases']} |
| 地图节点 / 有向道路 / 资源 | {counts['mapNodes']} / {counts['mapEdges']} / {counts['mapResources']} |
| 场景检查统计 | 通过 {metrics.get('checks', {}).get('passed', 0)} 项，失败 {metrics.get('checks', {}).get('failed', 0)} 项 |

## 车辆参与情况

| 车辆 | 任务数 | 轨迹累计里程 |
| --- | ---: | ---: |
{vehicle_lines}

| 车辆 | 最终站点 | 载荷 | 通讯 | 状态 |
| --- | --- | --- | --- | --- |
{final_states}

## 任务证据

| 工位 | 车辆 | 建单至完成耗时 | 步骤闭环 | 交通回执 |
| --- | --- | ---: | ---: | ---: |
{task_lines}

## 结果边界

本报告从本次保存的地图、数据库和原始场景报告生成，不改写原始判定。地图包含的工位数量不等于本次已覆盖的呼叫工位数量；任务完成不代表积压、全部工位压力或物理净空验收通过。仿真不替代真车定位、倒车对接、机械动作及现场安全验收。可回放视频为仿真证据重放，不是现场录像。
"""


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
        "longestUnchangedStepSeconds": report.get("metrics", {}).get("longestUnchangedStepSeconds"),
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
        "routeSpeedLimits": sorted({float(edge.get('speedLimit', .5)) for edge in map_definition['edges']}),
        "modelSpeedUpperBounds": sorted({min(1.0, float(edge.get('speedLimit', .5))) * scale
                                          for edge in map_definition['edges'] for scale in speeds}),
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
    markdown = render_markdown(metrics, vehicle_lines, task_lines, "\n".join(final_states))
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
