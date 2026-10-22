"""Bound repetitive queries without discarding commands, PLC writes or faults."""
import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
import time

QUERY_APIS = {1000, 1022, 1060, 1100, 1110, 1300, 1301, 1302, 1303}


class DeviceJournal:
    def __init__(self, root, max_bytes=5 * 1024 * 1024, backups=3):
        self.root = Path(root)
        self.logger = logging.Logger('private-device-queries')
        self.handler = RotatingFileHandler(self.root / 'device-queries.jsonl', maxBytes=max_bytes,
                                           backupCount=backups, encoding='utf-8', delay=True)
        self.handler.setFormatter(logging.Formatter('%(message)s'))
        self.logger.addHandler(self.handler)

    def write(self, kind, **data):
        event = dict(time=time.time(), kind=kind, **data)
        line = json.dumps(event, ensure_ascii=False)
        response = data.get('response') or {}
        routine_query = (kind == 'robokit' and data.get('api') in QUERY_APIS
                         and response.get('ret_code', 0) == 0
                         and not any(response.get(key) for key in ('errors', 'fatals', 'warnings')))
        if routine_query:
            self.logger.info(line)
        else:
            # The regression tools count accepted 3066 commands and PLC edges
            # across restarts. Never rotate their source of evidence away.
            with (self.root / 'device-events.jsonl').open('a', encoding='utf-8') as stream:
                stream.write(line + '\n')

    def close(self):
        self.handler.close()


def rotate_before_launch(path, max_bytes=10 * 1024 * 1024, backups=3):
    """Only called before opening a stopped child's redirected stdout/stderr."""
    path = Path(path)
    if not path.exists() or path.stat().st_size < max_bytes:
        return
    path.with_name(path.name + f'.{backups}').unlink(missing_ok=True)
    for index in range(backups - 1, 0, -1):
        old = path.with_name(path.name + f'.{index}')
        if old.exists():
            old.replace(path.with_name(path.name + f'.{index + 1}'))
    path.replace(path.with_name(path.name + '.1'))
