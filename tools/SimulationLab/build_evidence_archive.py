"""Build a compact, portable archive for a completed SimulationLab campaign."""

import argparse
import hashlib
import json
import sqlite3
import zipfile
from datetime import datetime
from pathlib import Path


MAX_FILE_BYTES = 48 * 1024 * 1024
TASK_TABLES = (
    "dispatch_task",
    "dispatch_task_step",
    "agv_command_outbox",
    "task_runtime_event",
    "execution_evidence",
    "traffic_resource_lease",
    "traffic_resource_release_audit",
    "traffic_segment_receipt",
    "traffic_segment_receipt_event",
    "task_handshake_checkpoint",
    "task_signal_last_observation",
    "obstacle_recovery",
)


def json_bytes(value) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        indent=2,
        allow_nan=False,
    ).encode("utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    args = parser.parse_args()

    run = args.root / ("simulation-run-" + args.run_id)
    output = args.output_dir
    entries = {}
    buffer = args.archive.with_suffix(".tmp")
    observations = run / "observations.jsonl"
    observations_hash = hashlib.sha256()
    with observations.open("rb") as stream:
        for chunk in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            observations_hash.update(chunk)

    with zipfile.ZipFile(
        buffer,
        "w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=7,
    ) as archive:
        def add_bytes(name: str, data: bytes) -> None:
            archive.writestr(name, data)
            entries[name] = {
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            }

        def add_file(source: Path, name: str) -> None:
            if not source.is_file() or source.stat().st_size > MAX_FILE_BYTES:
                return
            add_bytes(name, source.read_bytes())

        summary = {
            "schema": "agv-simulation-curated-evidence/v1",
            "reportId": args.run_id,
            "sourceRoot": str(args.root),
            "exportedAt": datetime.now().astimezone().isoformat(),
            "originalObservations": {
                "path": str(observations),
                "bytes": observations.stat().st_size,
                "sha256": observations_hash.hexdigest(),
                "note": "原始逐采样观测因体积过大未复制；本包保留压缩回放和各阶段证据。",
            },
            "scope": "隔离仿真调度、PLC、车辆和路权证据；不替代现场验收。",
        }
        add_bytes("README.json", json_bytes(summary))

        for name in (
            "final-live.json",
            "final-snapshot.json",
            "task-results.csv",
            "metrics-summary.json",
            "simulation-report.json",
            "simulation-replay.json",
            "仿真验收报告.md",
        ):
            add_file(output / name, "result/" + name)

        for name in (
            "report.json",
            "queue-summary.json",
            "manifest.json",
            "scenario.json",
            "execution-configuration.json",
            "simulation-configuration.json",
            "map.json",
            "plc-signal-table.json",
            "plc-signals.source.csv",
            "task-summary.json",
            "all-task-details.json",
            "accepted-3066.json",
            "outbox-readonly.json",
            "final-device-observation.json",
            "device-events.jsonl",
        ):
            source = run / name if (run / name).exists() else args.root / name
            add_file(source, "run/" + name)

        for name in (
            "lab.json",
            "source-manifest.json",
            "device-state.json",
            "multi-state.json",
            "plc-signals.source.csv",
            "navigation.agvmap.json",
            "simulation-report.json",
        ):
            add_file(args.root / name, "instance/" + name)

        for name in (
            "service/logs/service/service.log",
            "robots/AGV-01/device-events.jsonl",
            "robots/AGV-02/device-events.jsonl",
            "robots/AGV-03/device-events.jsonl",
            "robots/AGV-04/device-events.jsonl",
            "robots/AGV-05/device-events.jsonl",
        ):
            add_file(args.root / name, "logs/" + name)

        database = sqlite3.connect(
            f"file:{(args.root / 'data' / 'agv-dispatch.db').as_posix()}?mode=ro",
            uri=True,
        )
        database.row_factory = sqlite3.Row
        audit = {
            "tables": {},
            "note": "按本轮任务编号关联的只读审计表；数据库中的大体量车辆轨迹未复制。",
        }
        task_ids = [
            row["TaskId"]
            for row in database.execute(
                "select TaskId from dispatch_task order by CreatedAt"
            )
        ]
        placeholders = ",".join("?" for _ in task_ids)
        for table in TASK_TABLES:
            columns = [
                row["name"]
                for row in database.execute(
                    f'pragma table_info("{table}")'
                )
            ]
            if "TaskId" not in columns:
                continue
            rows = database.execute(
                f'select * from "{table}" where TaskId in ({placeholders})',
                task_ids,
            ).fetchall()
            audit["tables"][table] = [dict(row) for row in rows]
        add_bytes("audit/task-tables.json", json_bytes(audit))

        manifest = {
            "schema": summary["schema"],
            "reportId": args.run_id,
            "files": [
                {"path": name, **metadata}
                for name, metadata in sorted(entries.items())
            ],
        }
        add_bytes("archive-manifest.json", json_bytes(manifest))

    buffer.replace(args.archive)
    print(json.dumps({
        "archive": str(args.archive),
        "bytes": args.archive.stat().st_size,
        "files": len(entries),
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
