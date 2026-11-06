"""Validated physical-device fault controls for the synthetic loopback lab.

Each sequence replaces the previous settings. An invalid/partially-written file
never partly changes device state. This module does not touch dispatch storage.
"""
import json
import math


VEHICLE_DEFAULTS = dict(speedScale=0.75, paused=False, offline=False,
                        cancelFinalReturnSegments=0,
                        dropNextResponse=False, blockedEdges=[], obstacle=False,
                        obstacleAfterSegments=None, charging=False)
PLC_DEFAULTS = dict(readyDelaySeconds=0.0, doneDelaySeconds=5.0,
                    suppressReady=False, suppressDone=False, fault=False)


def _number(value, minimum, maximum, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not minimum <= value <= maximum:
        raise ValueError(f'{name} must be between {minimum} and {maximum}')
    return float(value)


def validate_controls(value, vehicle_ids=None, edge_ids=None):
    if not isinstance(value, dict) or set(value) - {'sequence', 'vehicles', 'plc'}:
        raise ValueError('Expected sequence, vehicles and plc control fields')
    sequence = value.get('sequence')
    if not isinstance(sequence, str) or not sequence.strip() or len(sequence) > 128:
        raise ValueError('A nonempty sequence of at most 128 characters is required')
    vehicles = value.get('vehicles', {})
    if not isinstance(vehicles, dict) or len(vehicles) > 32:
        raise ValueError('vehicles must be an object with at most 32 entries')
    result = dict(sequence=sequence, vehicles={}, plc=dict(PLC_DEFAULTS))
    for vehicle_id, fields in vehicles.items():
        if not isinstance(vehicle_id, str) or not vehicle_id or (vehicle_ids is not None and vehicle_id not in vehicle_ids):
            raise ValueError(f'Unknown virtual vehicle: {vehicle_id}')
        if not isinstance(fields, dict) or set(fields) - VEHICLE_DEFAULTS.keys():
            raise ValueError('Unknown vehicle control field')
        control = dict(VEHICLE_DEFAULTS, blockedEdges=[])
        for key, item in fields.items():
            if key == 'speedScale':
                control[key] = _number(item, 0.1, 10, key)
            elif key == 'cancelFinalReturnSegments':
                if type(item) is not int or not 0 <= item <= 32:
                    raise ValueError('cancelFinalReturnSegments must be an integer between 0 and 32')
                control[key] = item
            elif key == 'blockedEdges':
                if not isinstance(item, list) or len(item) > 2048 or any(not isinstance(edge, str) or not edge or (edge_ids is not None and edge not in edge_ids) for edge in item):
                    raise ValueError('blockedEdges must reference existing map edge IDs')
                control[key] = list(dict.fromkeys(item))
            elif key == 'obstacleAfterSegments':
                if item is None:
                    control[key] = None
                elif type(item) is not int or not 0 <= item <= 10000:
                    raise ValueError('obstacleAfterSegments must be null or an integer between 0 and 10000')
                else:
                    control[key] = item
            elif not isinstance(item, bool):
                raise ValueError(f'{key} must be boolean')
            else:
                control[key] = item
        result['vehicles'][vehicle_id] = control
    fields = value.get('plc', {})
    if not isinstance(fields, dict) or set(fields) - PLC_DEFAULTS.keys():
        raise ValueError('Unknown PLC control field')
    for key, item in fields.items():
        if key in ('readyDelaySeconds', 'doneDelaySeconds'):
            result['plc'][key] = _number(item, 0, 300, key)
        elif not isinstance(item, bool):
            raise ValueError(f'{key} must be boolean')
        else:
            result['plc'][key] = item
    return result


def read_controls(path, vehicle_ids=None, edge_ids=None):
    if path.stat().st_size > 256 * 1024:
        raise ValueError('Device control file exceeds 256 KiB')
    return validate_controls(json.loads(path.read_text(encoding='utf-8-sig')), vehicle_ids, edge_ids)


# Public spelling used by the controller; accepts the same optional allowlists.
normalize_controls = validate_controls
