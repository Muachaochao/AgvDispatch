"""Explicit PLC station bindings for private loopback simulations.

Addresses come from the selected current CSV. Disabled/inferred rows are never
silently promoted to field-confirmed points. A caller may explicitly opt into
using such a documented layout in an isolated synthetic instance only.
"""
import csv
import hashlib
import io
import os
from pathlib import Path
import re


MEMORY_BYTES = 1024
DUMPER_OFFSET = 610
MAX_STATIONS = 305
STATION_SIGNALS = (
    ('FullBoxRequested', 'PlcToHost', 'Read'),
    ('BoxPresent', 'PlcToHost', 'Read'),
    ('Faulted', 'PlcToHost', 'Read'),
    ('CommunicationOnline', 'PlcToHost', 'Read'),
    ('FullBoxRequestAcknowledged', 'HostToPlc', 'Write'),
    ('FullBoxRemoved', 'HostToPlc', 'Write'),
    ('EmptyBoxTaskAcknowledged', 'HostToPlc', 'Write'),
    ('EmptyBoxPlaced', 'HostToPlc', 'Write'),
)
DUMPER_SIGNALS = (
    ('AgvAtWaitingPosition', 'HostToPlc', 'Write'),
    ('AgvAtDumpingPosition', 'HostToPlc', 'Write'),
    ('DumperReady', 'PlcToHost', 'Read'),
    ('DumpingCompletedMayLeave', 'PlcToHost', 'Read'),
    ('Faulted', 'PlcToHost', 'Read'),
)
ADDRESS = re.compile(r'DB1\.DBX(\d+)\.([0-7])', re.IGNORECASE)
VERIFIED_LABEL = '当前点表已确认并启用；仅复制到隔离仿真'
SYNTHETIC_LABEL = '隔离仿真点位，现场未确认或未启用'


def _rows(path):
    with Path(path).open(encoding='utf-8-sig', newline='') as stream:
        reader = csv.DictReader(stream)
        headers = reader.fieldnames
        required = {'address', 'variableName', 'stationId', 'deviceType', 'dataType',
                    'direction', 'access', 'addressStatus', 'enabled', 'sourceNote'}
        if not headers or not required <= set(headers):
            raise ValueError('PLC点表缺少工位、地址、读写方向或核定状态字段。')
        rows = []
        for row in reader:
            if None in row:
                raise ValueError('PLC点表包含列数不匹配的行。')
            if any(row.values()):
                rows.append({key: (value or '').strip() for key, value in row.items()})
    return headers, rows


def station_catalog(csv_path):
    """Return supported station layouts, preserving their field validity flag."""
    _, rows = _rows(csv_path)
    grouped = {}
    for row in rows:
        if row['deviceType'] == 'ChipRemovalStation':
            grouped.setdefault(row['stationId'], []).append(row)
    result = []
    occupied = {}
    for station_id, signals in grouped.items():
        if not station_id or len(signals) != len(STATION_SIGNALS):
            raise ValueError(f'工位{station_id}的8个业务握手点不完整或有重复。')
        by_variable = {row['variableName']: row for row in signals}
        if len(by_variable) != len(STATION_SIGNALS):
            raise ValueError(f'工位{station_id}存在重复业务信号。')
        offset = None
        for bit, (variable, direction, access) in enumerate(STATION_SIGNALS):
            row = by_variable.get(variable)
            if row is None:
                raise ValueError(f'工位{station_id}缺少业务信号{variable}。')
            address = ADDRESS.fullmatch(row['address'])
            if (address is None or row['dataType'].lower() != 'bool' or
                    row['direction'] != direction or row['access'] != access):
                raise ValueError(f'工位{station_id}的信号{variable}地址或读写方向不受支持。')
            byte, actual_bit = map(int, address.groups())
            if offset is None:
                offset = byte
            if byte != offset or actual_bit != bit or not 0 <= byte < MEMORY_BYTES or byte == DUMPER_OFFSET:
                raise ValueError(f'工位{station_id}的地址须为独立DB1字节内的0至7位，不能占用倒料地址。')
        if offset in occupied:
            raise ValueError(f'工位{station_id}与{occupied[offset]}占用相同PLC字节。')
        occupied[offset] = station_id
        confirmed = all(row['enabled'].lower() == 'true' and row['addressStatus'].lower() == 'confirmed'
                        for row in signals)
        result.append(dict(stationId=station_id, offset=offset,
                           signalSource='verified' if confirmed else 'isolated-synthetic',
                           sourceLabel=VERIFIED_LABEL if confirmed else SYNTHETIC_LABEL,
                           fieldConfirmed=confirmed))
    return sorted(result, key=lambda station: (station['offset'], station['stationId']))


def normalize_stations(stations, csv_path):
    """Resolve station IDs against the CSV; pickup node validation belongs to map code."""
    if not isinstance(stations, list) or not 1 <= len(stations) <= MAX_STATIONS:
        raise ValueError(f'工位须配置1至{MAX_STATIONS}个，并在当前点表中具有完整业务握手地址。')
    available = {station['stationId']: station for station in station_catalog(csv_path)}
    result, seen = [], set()
    for station in stations:
        if (not isinstance(station, dict) or
                set(station) - {'stationId', 'pickupSite', 'signalSource', 'offset', 'sourceLabel', 'fieldConfirmed', 'priority'}):
            raise ValueError('工位配置字段无效。')
        station_id = station.get('stationId')
        if not isinstance(station_id, str) or station_id not in available or station_id in seen:
            raise ValueError('工位ID必须唯一且存在于当前业务点表中。')
        pickup = station.get('pickupSite')
        if not isinstance(pickup, str) or not pickup.strip():
            raise ValueError(f'工位{station_id}未配置取货点。')
        binding = available[station_id]
        source = station.get('signalSource', 'verified')
        if source not in ('verified', 'isolated-synthetic'):
            raise ValueError('工位信号来源必须选择已确认点表或隔离仿真点位。')
        if source == 'verified' and not binding['fieldConfirmed']:
            raise ValueError(f'工位{station_id}的点位尚未现场确认或启用；仅可显式选择隔离仿真点位。')
        if 'offset' in station and station['offset'] != binding['offset']:
            raise ValueError(f'工位{station_id}的PLC偏移与当前点表不一致。')
        priority = station.get('priority', 0)
        if not isinstance(priority, int) or isinstance(priority, bool) or not 0 <= priority <= 100:
            raise ValueError('工位呼叫优先级须为0至100的整数，数值越大越优先。')
        seen.add(station_id)
        result.append(dict(stationId=station_id, pickupSite=pickup, offset=binding['offset'],
                           signalSource=source,
                           priority=priority,
                           sourceLabel=VERIFIED_LABEL if source == 'verified' else SYNTHETIC_LABEL,
                           fieldConfirmed=binding['fieldConfirmed'] and source == 'verified'))
    return result


def write_private_signal_table(source_csv, target_csv, stations, dumper_station_id='DUMPER-001'):
    """Write only the selected loopback station/dumper points, with source provenance.

    The caller must supply an isolated target path; overwriting the source itself
    is rejected here as an additional guard. Unconfirmed AddressStatus values are
    retained even when a virtual point is explicitly enabled.
    """
    source_csv, target_csv = Path(source_csv).resolve(), Path(target_csv).resolve()
    if source_csv == target_csv:
        raise ValueError('不得覆盖来源点表；必须写入隔离仿真实例。')
    normalized = normalize_stations(stations, source_csv)
    selected = {station['stationId']: station for station in normalized}
    headers, rows = _rows(source_csv)
    output = []
    dumper_rows = [row for row in rows if row['stationId'] == dumper_station_id]
    dumper_by_name = {row['variableName']: row for row in dumper_rows}
    if len(dumper_rows) != 5 or len(dumper_by_name) != 5:
        raise ValueError('当前倒料点表不是受支持的DB1.DBX610五信号契约。')
    for bit, (variable, direction, access) in enumerate(DUMPER_SIGNALS):
        row = dumper_by_name.get(variable)
        if (row is None or row['address'].upper() != f'DB1.DBX{DUMPER_OFFSET}.{bit}' or
                row['dataType'].lower() != 'bool' or row['direction'] != direction or
                row['access'] != access or row['enabled'].lower() != 'true' or
                row['addressStatus'].lower() != 'confirmed'):
            raise ValueError('当前倒料点表需具备已确认并启用的DB1.DBX610五信号契约。')
    for row in rows:
        binding = selected.get(row['stationId'])
        if binding is not None:
            row = dict(row)
            row['enabled'] = 'true'
            row['sourceNote'] = binding['sourceLabel'] + '；来源点表说明：' + row['sourceNote']
            output.append(row)
        elif row['stationId'] == dumper_station_id:
            output.append(dict(row))
    buffer = io.StringIO(newline='')
    writer = csv.DictWriter(buffer, fieldnames=headers)
    writer.writeheader()
    writer.writerows(output)
    data = buffer.getvalue().encode('utf-8-sig')
    target_csv.parent.mkdir(parents=True, exist_ok=True)
    temporary = target_csv.with_name(target_csv.name + '.tmp')
    try:
        temporary.write_bytes(data)
        os.replace(temporary, target_csv)
    finally:
        temporary.unlink(missing_ok=True)
    return dict(sourceSha256=hashlib.sha256(source_csv.read_bytes()).hexdigest(),
                privateSha256=hashlib.sha256(data).hexdigest(), stations=normalized,
                signalCount=len(output), isolationOnly=True)


def validate_twin_stations(stations):
    """Validate stored private topology before a twin touches its shared PLC memory."""
    if not isinstance(stations, list) or not 1 <= len(stations) <= MAX_STATIONS:
        raise ValueError('虚拟PLC工位数量无效。')
    ids, offsets, pickups = set(), set(), set()
    for station in stations:
        if not isinstance(station, dict):
            raise ValueError('虚拟PLC工位配置必须是对象。')
        station_id, offset, pickup = (station.get(key) for key in ('stationId', 'offset', 'pickupSite'))
        if (not isinstance(station_id, str) or not station_id or station_id in ids or
                not isinstance(pickup, str) or not pickup or pickup in pickups or
                not isinstance(offset, int) or isinstance(offset, bool) or
                not 0 <= offset < MEMORY_BYTES or offset == DUMPER_OFFSET or offset in offsets):
            raise ValueError('虚拟PLC工位ID、取货点或独立字节地址无效/重复。')
        ids.add(station_id)
        pickups.add(pickup)
        offsets.add(offset)
    return stations
