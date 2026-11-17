"""Portable evidence export for one isolated simulation run."""
import csv
from contextlib import closing
import hashlib
import io
import json
import re
import sqlite3
import time
import zipfile
from pathlib import Path

from controller import read
from simulation_reports import find_run

MAX_ARCHIVE_BYTES = 64 * 1024 * 1024
MAX_SOURCE_BYTES = 48 * 1024 * 1024
MAX_FILES = 5000
MAX_AUDIT_FIELD_BYTES = 64 * 1024
TASK_ID = re.compile(r'^[A-Za-z0-9_-]{1,128}$')
IDENTIFIER = re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')


def _linked(path):
    return path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction())


def _json_bytes(value):
    return json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False).encode('utf-8')


def _task_ids(evidence):
    value = {}
    path = evidence / 'all-task-details.json'
    if path.is_file():
        try:
            value = read(path)
        except (OSError, ValueError, TypeError):
            value = {}
    identities = {}
    identity_path = evidence / 'task-identities.json'
    if identity_path.is_file():
        try:
            identities = read(identity_path)
        except (OSError, ValueError, TypeError):
            identities = {}
    return sorted(task_id for task_id in set(value) | set(identities)
                  if isinstance(task_id, str) and TASK_ID.fullmatch(task_id))


def _task_audit(root, evidence):
    ids = _task_ids(evidence)
    database = root / 'data/agv-dispatch.db'
    result = dict(taskIds=ids, tables={}, note='只读导出本轮任务编号关联的持久化审计行，每表最多10000行；超过64 KiB的文本字段以大小与SHA256替代，原值保留于本地数据库。')
    if not ids or not database.is_file():
        return result
    try:
        uri = database.resolve().as_uri() + '?mode=ro'
        with closing(sqlite3.connect(uri, uri=True, timeout=3)) as db:
            db.row_factory = sqlite3.Row
            tables = [row[0] for row in db.execute(
                "select name from sqlite_master where type in ('table','view') and name not like 'sqlite_%'")]
            placeholders = ','.join('?' for _ in ids)
            for table in tables:
                if not isinstance(table, str) or not IDENTIFIER.fullmatch(table):
                    continue
                columns = [row[1] for row in db.execute('pragma table_info("' + table + '")')]
                if 'TaskId' not in columns:
                    continue
                rows = db.execute('select * from "' + table + '" where TaskId in (' + placeholders + ') limit 10000',
                                  ids).fetchall()
                exported = []
                for row in rows:
                    item = dict(row)
                    for column, value in item.items():
                        if isinstance(value, str):
                            encoded = value.encode('utf-8')
                            if len(encoded) > MAX_AUDIT_FIELD_BYTES:
                                item[column] = dict(archiveOmitted=True, utf8Bytes=len(encoded),
                                                    sha256=hashlib.sha256(encoded).hexdigest())
                    exported.append(item)
                result['tables'][table] = exported
    except (OSError, sqlite3.Error) as error:
        result['note'] = '任务ID已记录；导出时只读数据库不可用：' + str(error)
    return result


def _add_bytes(archive, entries, name, data):
    if name in entries:
        raise ValueError('证据包包含重复路径。')
    if len(entries) >= MAX_FILES or sum(item['bytes'] for item in entries.values()) + len(data) > MAX_SOURCE_BYTES:
        raise ValueError('证据包超过文件数量或大小边界。')
    archive.writestr(name, data)
    entries[name] = dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())


def _add_file(archive, entries, source, name, omitted=None):
    if _linked(source) or not source.is_file():
        return
    size = source.stat().st_size
    if size + sum(item['bytes'] for item in entries.values()) > MAX_SOURCE_BYTES:
        if omitted is not None:
            omitted.append(dict(path=name, bytes=size, reason='超过证据包原始文件容量边界；原文件保留于本地实例。'))
        return
    data = source.read_bytes()
    _add_bytes(archive, entries, name, data)


def _add_tree(archive, entries, source_root, relative_root, predicate, omitted=None):
    for source in sorted(source_root.rglob('*')):
        if len(entries) >= MAX_FILES:
            break
        try:
            relative = source.relative_to(source_root)
        except ValueError:
            continue
        cursor = source
        unsafe = False
        while cursor != source_root.parent:
            if _linked(cursor):
                unsafe = True
                break
            cursor = cursor.parent
        if unsafe:
            continue
        if source.is_file() and predicate(source, relative):
            _add_file(archive, entries, source, (relative_root / relative).as_posix(), omitted)


def archive_evidence(runtime, identity):
    evidence = find_run(runtime, identity)
    root = evidence.parent
    report = read(evidence / 'report.json')
    if not isinstance(report, dict) or report.get('id') != identity:
        raise ValueError('报告与证据目录不一致，拒绝导出。')
    buffer = io.BytesIO()
    entries = {}
    omitted = []
    with zipfile.ZipFile(buffer, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        readme = (
            'AGV 隔离仿真证据包\n'
            f'报告编号：{identity}\n'
            f'实例：{root.name}\n'
            f'导出时间：{time.strftime("%Y-%m-%d %H:%M:%S")}\n\n'
            '目录说明：\n'
            '- evidence/：本轮场景参数、地图、PLC来源、任务身份、逐次观察、最终状态和报告。\n'
            '- logs/：虚拟车、后台服务、WebHost和设备事件日志。\n'
            '- task-audit.json：本轮任务编号关联的只读数据库审计行。\n'
            '- archive-manifest.json：除自身外所有文件的大小与SHA256。\n\n'
            '容量受限时，未收录文件见清单 omittedFiles；审计大文本仅保留大小与SHA256。完整原值仍在本地实例，本包不是完整数据库备份。\n'
            '结果是回环Robokit/S7合成模型测试证据，不替代实车定位、机械动作、PLC固件和安全联锁验收。\n'
        ).encode('utf-8')
        _add_bytes(archive, entries, 'README.txt', readme)
        for source in sorted(evidence.rglob('*')):
            if source.is_file() and not _linked(source):
                relative = source.relative_to(evidence)
                _add_file(archive, entries, source, (Path(evidence.name) / relative).as_posix(), omitted)
        plan_path = root / 'business-call-plan.json'
        scenario_path = evidence / 'scenario.json'
        if report.get('name') == 'business-calls' and plan_path.is_file() and not _linked(plan_path) and scenario_path.is_file():
            plan = read(plan_path)
            if plan.get('calls') == read(scenario_path).get('calls'):
                _add_file(archive, entries, plan_path, evidence.name + '/business-call-plan.json', omitted)
        audit = _task_audit(root, evidence)
        _add_bytes(archive, entries, 'task-audit.json', _json_bytes(audit))
        detail_path = evidence / 'all-task-details.json'
        if detail_path.is_file():
            details = read(detail_path)
            rows = []
            for task_id, detail in sorted(details.items()) if isinstance(details, dict) else []:
                task = detail.get('task', {}) if isinstance(detail, dict) else {}
                rows.append(dict(TaskId=task_id, StationId=task.get('stationId'),
                                 VehicleId=task.get('vehicleId'), Stage=task.get('stage'),
                                 Progress=detail.get('waitingExplanation') if isinstance(detail, dict) else None))
            output = io.StringIO(newline='')
            writer = csv.DictWriter(output, fieldnames=('TaskId', 'StationId', 'VehicleId', 'Stage', 'Progress'))
            writer.writeheader()
            writer.writerows(rows)
            _add_bytes(archive, entries, 'task-summary.csv', output.getvalue().encode('utf-8-sig'))

        root_files = {
            'device-events.jsonl', 'lab.json', 'processes.json', 'source-manifest.json',
            'simulation-report.json', 'plc-signals.source.csv', 'protocol-state.json',
            'simulation-service-stdout.log', 'simulation-service-stderr.log',
            'simulation-webhost-stdout.log', 'simulation-webhost-stderr.log',
            'simulation-twin-stdout.log', 'simulation-twin-stderr.log',
            'simulation-map-import-stdout.log', 'simulation-map-import-stderr.log'
        }
        for name in sorted(root_files):
            _add_file(archive, entries, root / name, 'logs/' + name, omitted)
        _add_tree(archive, entries, root / 'robots', Path('logs/robots'),
                  lambda source, relative: source.suffix == '.jsonl', omitted)
        _add_tree(archive, entries, root / 'service' / 'logs', Path('logs/service'),
                  lambda source, relative: source.suffix == '.log', omitted)
        _add_tree(archive, entries, root / 'webhost', Path('logs/webhost'),
                  lambda source, relative: source.suffix == '.log', omitted)

        manifest = dict(
            schema='agv-simulation-evidence/v1', reportId=identity, rootName=root.name,
            exportedAt=time.time(), files=sorted(entries.values(), key=lambda item: item['path']),
            omittedFiles=omitted,
            scope='隔离仿真任务、PLC、车辆、路权、场景事件与设备日志；不代表现场验收。')
        manifest_bytes = _json_bytes(manifest)
        if len(buffer.getvalue()) + len(manifest_bytes) > MAX_ARCHIVE_BYTES:
            raise ValueError('证据包压缩后超过64 MiB。')
        archive.writestr('archive-manifest.json', manifest_bytes)
    data = buffer.getvalue()
    if len(data) > MAX_ARCHIVE_BYTES:
        raise ValueError('证据包超过64 MiB。')
    return 'simulation-evidence-' + identity + '.zip', data
