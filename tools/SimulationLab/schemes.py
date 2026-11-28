"""Durable simulation recipes and last selection; never starts or attaches devices."""
import copy
import hashlib
import json
import math
from pathlib import Path
import re
import threading
import time
import uuid


SCHEME_ID = re.compile(r'^[a-f0-9]{32}$')
ROOT_NAME = re.compile(r'^web-single-vehicle-[A-Za-z0-9_-]+$')


def _invalid_constant(value):
    raise ValueError('方案记录包含无效数值。')


def _no_links(path):
    for candidate in (path, *path.parents):
        if candidate.is_symlink() or (hasattr(candidate, 'is_junction') and candidate.is_junction()):
            raise ValueError('仿真方案目录不能经过符号链接或目录联接。')


STABLE_SCOPE = 'current'
CURRENT_POINTER = 'current-instance.json'
MAX_SCHEME_BYTES = 64 * 1024 * 1024


class SchemeStore:
    """Namespace selection state while exposing recipes preserved in older scopes."""
    def __init__(self, workspace, template_root, validate_root, scope=None):
        """scope=None keeps the historical per-template namespace (tests, ad-hoc labs).
        The served controller passes STABLE_SCOPE: the daily 5199 WebHost launches the controller with
        whichever instance is newest, so a template-derived namespace changed on every restart and the
        selected instance/scheme were forgotten (实例总在变化)."""
        self.workspace = Path(workspace).absolute()
        self.template_root = Path(template_root).absolute()
        self.validate_root = validate_root
        self.lock = threading.RLock()
        directory = self.workspace / 'artifacts' / 'simulation-center'
        _no_links(directory)
        directory.mkdir(exist_ok=True, parents=True)
        if scope is None:
            scope = hashlib.sha256(str(self.template_root).casefold().encode()).hexdigest()[:16]
        elif not re.fullmatch(r'[a-z0-9-]{1,32}', scope):
            raise ValueError('方案记录作用域无效。')
        self.path = directory / ('schemes-' + scope + '.json')
        self.pointer_path = directory / CURRENT_POINTER
        _no_links(self.pointer_path)
        _no_links(self.path)
        self.notice = None
        self.state = dict(version=1, selectedRoot=None, selectedSchemeId=None,
                          recentInstances=[], schemes=[], deletedSchemeIds=[])
        self._catalog_cache_key = None
        self._catalog_cache = None
        if self.path.exists():
            try:
                self.state = self._read_state(self.path)
            except (OSError, ValueError, TypeError) as error:
                # Preserve damaged evidence; replacing it with a fresh catalogue would lose recipes.
                self.notice = '最近方案记录无法读取，已保留原文件；当前使用模板。' + str(error)

    @staticmethod
    def _read_state(path):
        with path.open('rb') as source:
            data = source.read(2 * MAX_SCHEME_BYTES + 1)
        if len(data) > 2 * MAX_SCHEME_BYTES:
            raise ValueError('方案存储超出读取上限。')
        if len(data) > MAX_SCHEME_BYTES:
            data = data.replace(b'\r\n', b'\n')
        if len(data) > MAX_SCHEME_BYTES:
            raise ValueError('方案存储超出读取上限。')
        saved = json.loads(data.decode('utf-8-sig'), parse_constant=_invalid_constant)
        if not isinstance(saved, dict) or saved.get('version') != 1 or not isinstance(saved.get('schemes'), list) or not isinstance(saved.get('recentInstances'), list):
            raise ValueError('方案存储格式无效。')
        if len(saved['schemes']) > 40 or len(saved['recentInstances']) > 20:
            raise ValueError('方案存储条数无效。')
        for scheme in saved['schemes']:
            if not isinstance(scheme, dict) or not SCHEME_ID.fullmatch(str(scheme.get('id', ''))) or not isinstance(scheme.get('configuration'), dict) or not isinstance(scheme.get('name'), str) or type(scheme.get('updatedAt')) not in (int, float) or not math.isfinite(scheme['updatedAt']):
                raise ValueError('方案记录无效。')
            config = scheme['configuration']
            if not isinstance(config.get('mapDefinition'), dict) or not isinstance(config.get('vehicles'), list) or not isinstance(config.get('stations'), list):
                raise ValueError('方案缺少地图、车辆或工位配置。')
            if scheme.get('scenario') is not None and not isinstance(scheme['scenario'], dict):
                raise ValueError('方案场景记录无效。')
        if len({scheme['id'] for scheme in saved['schemes']}) != len(saved['schemes']):
            raise ValueError('方案编号重复。')
        for entry in saved['recentInstances']:
            if not isinstance(entry, dict) or not ROOT_NAME.fullmatch(str(entry.get('rootName', ''))) or type(entry.get('updatedAt')) not in (int, float) or not math.isfinite(entry['updatedAt']):
                raise ValueError('最近实例记录无效。')
        deleted = saved.get('deletedSchemeIds', [])
        if (not isinstance(deleted, list) or len(deleted) > 200
                or any(not isinstance(item, str) or not SCHEME_ID.fullmatch(item) for item in deleted)):
            raise ValueError('已删除方案记录无效。')
        saved.setdefault('deletedSchemeIds', [])
        return saved

    def _write_state(self, path, state):
        _no_links(path)
        data = json.dumps(state, ensure_ascii=False, separators=(',', ':'), allow_nan=False).encode('utf-8')
        if len(data) > MAX_SCHEME_BYTES:
            raise ValueError('方案总大小超过64MB，请减少地图或方案数量。')
        temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
        try:
            temporary.write_bytes(data)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)

    def _commit(self, state):
        if self.notice and self.path.exists():
            raise ValueError(self.notice + ' 请先备份并修复方案记录。')
        self._write_state(self.path, state)
        self.state = state
        self._catalog_cache_key = None

    def _store_paths(self):
        directory = self.path.parent
        paths = []
        for path in directory.glob('schemes-*.json'):
            try:
                _no_links(path)
                if path.is_file():
                    paths.append(path)
            except (OSError, ValueError):
                continue
        return sorted(paths)

    def _catalog_records(self):
        paths = self._store_paths()
        key = []
        readable_paths = []
        for path in paths:
            try:
                stat = path.stat()
            except OSError:
                continue
            key.append((str(path), stat.st_mtime_ns, stat.st_size))
            readable_paths.append(path)
        key = tuple(key)
        if key == self._catalog_cache_key and self._catalog_cache is not None:
            return self._catalog_cache
        candidates = []
        recent = []
        for path in readable_paths:
            try:
                state = self.state if path == self.path else self._read_state(path)
            except (OSError, ValueError, TypeError):
                continue
            candidates.extend((scheme['updatedAt'], path == self.path, scheme)
                              for scheme in state.get('schemes', []))
            recent.extend(state.get('recentInstances', []))
        preferred = {}
        for timestamp, current, scheme in candidates:
            previous = preferred.get(scheme['id'])
            if previous is None or (timestamp, current) > (previous[0], previous[1]):
                preferred[scheme['id']] = (timestamp, current, scheme)
        tombstones = set(self.state.get('deletedSchemeIds', []))
        schemes = [row[2] for key, row in preferred.items() if key not in tombstones]
        schemes.sort(key=lambda scheme: scheme['updatedAt'], reverse=True)
        instances = {}
        for entry in recent:
            name = entry.get('rootName')
            previous = instances.get(name)
            if previous is None or entry.get('updatedAt', 0) > previous.get('updatedAt', 0):
                instances[name] = entry
        recent_instances = sorted(instances.values(), key=lambda entry: entry.get('updatedAt', 0),
                                  reverse=True)[:20]
        self._catalog_cache_key = key
        self._catalog_cache = (schemes, recent_instances)
        return self._catalog_cache

    def _root(self, name):
        if not isinstance(name, str) or not ROOT_NAME.fullmatch(name):
            raise ValueError('最近实例名称无效。')
        root = self.workspace / 'artifacts' / name
        _no_links(root)
        self.validate_root(root)
        return root

    def restore_root(self):
        with self.lock:
            selected = self.state.get('selectedRoot')
            if selected:
                try:
                    return self._root(selected), self.notice
                except (OSError, ValueError, KeyError) as error:
                    return self.template_root, '上次实例已不可用，使用模板；保存的方案与历史仍保留。' + str(error)
            return self.template_root, self.notice

    def remember(self, root, selected_scheme_id=None):
        """Remember a validated directory, without treating its saved observations as fresh."""
        root = Path(root)
        canonical = self._root(root.name)
        if root.absolute() != canonical.absolute():
            raise ValueError('仅允许记住当前项目的隔离仿真实例。')
        root = canonical
        lab = json.loads((root / 'lab.json').read_text(encoding='utf-8-sig'))
        configuration = lab.get('simulationConfiguration') or {}
        entry = dict(rootName=root.name, mapName=configuration.get('mapName') or lab.get('mapName') or '',
                     vehicleCount=len(configuration.get('vehicles') or lab.get('initialSites') or []),
                     stationCount=len(configuration.get('stations') or lab.get('simulationTopology', {}).get('stations') or []),
                     plcTableName=(configuration.get('plcSignalTable') or lab.get('plcSignalTable') or {}).get('fileName'),
                     plcTableSha256=(configuration.get('plcSignalTable') or lab.get('plcSignalTable') or {}).get('sha256'),
                     plcSourceSha256=(configuration.get('plcSignalTable') or lab.get('plcSignalTable') or {}).get('sourceSha256'),
                     updatedAt=time.time())
        with self.lock:
            state = copy.deepcopy(self.state)
            state['selectedRoot'] = root.name
            if selected_scheme_id is not None:
                self.get(selected_scheme_id)
            state['selectedSchemeId'] = selected_scheme_id
            state['recentInstances'] = [entry] + [row for row in state['recentInstances'] if row.get('rootName') != root.name][:19]
            self._commit(state)
            self._write_pointer(root, lab, entry, state.get('selectedSchemeId'))

    def _write_pointer(self, root, lab, entry, scheme_id):
        """One small, stable file that names the current instance for the site assistant and the WebHost
        (artifacts/simulation-center/current-instance.json). Written atomically; never contains device data."""
        configuration = lab.get('simulationConfiguration') or {}
        identity = configuration.get('mapIdentity') or {}
        release = {}
        try:
            release = json.loads((root / 'runtime-release.json').read_text(encoding='utf-8-sig'))
        except (OSError, ValueError):
            pass
        scheme_name = None
        if scheme_id:
            scheme_name = next((row['name'] for row in self._catalog_records()[0] if row.get('id') == scheme_id), None)
        pointer = dict(schema='agv-simulation-current-instance', version=1, rootName=root.name,
                       selectedSchemeId=scheme_id, selectedSchemeName=scheme_name,
                       mapName=entry.get('mapName'), mapDefinitionHash=identity.get('definitionHash'),
                       mapVersionId=identity.get('mapVersionId'), plcTableSha256=entry.get('plcTableSha256'),
                       vehicleCount=entry.get('vehicleCount'), stationCount=entry.get('stationCount'),
                       simulationRate=lab.get('simulationRate'),
                       configurationFingerprint=lab.get('configurationFingerprint'),
                       releaseId=release.get('releaseId') or release.get('informationalVersion'),
                       createdAt=lab.get('createdAt'), updatedAt=entry.get('updatedAt'))
        temporary = self.pointer_path.with_name(self.pointer_path.name + '.' + str(time.time_ns()) + '.tmp')
        try:
            temporary.write_text(json.dumps(pointer, ensure_ascii=False, indent=2), encoding='utf-8')
            temporary.replace(self.pointer_path)
        finally:
            if temporary.exists():
                temporary.unlink()

    def current_pointer(self):
        try:
            return json.loads(self.pointer_path.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError):
            return None

    @staticmethod
    def _summary(scheme):
        config = scheme['configuration']
        identity = config.get('mapIdentity') or {}
        table = config.get('plcSignalTable') or {}
        return dict(id=scheme['id'], name=scheme['name'], updatedAt=scheme['updatedAt'],
                    mapName=config.get('mapDefinition', {}).get('name') or config.get('name') or '',
                    mapId=identity.get('mapId'), mapVersionId=identity.get('mapVersionId'),
                    mapDefinitionHash=identity.get('definitionHash'),
                    vehicleCount=len(config.get('vehicles', [])), stationCount=len(config.get('stations', [])),
                    scenarioName=(scheme.get('scenario') or {}).get('name'),
                    plcTableName=table.get('fileName'), plcTableSha256=table.get('sha256'),
                    plcSourceSha256=table.get('sourceSha256'))

    def catalog(self):
        with self.lock:
            schemes, recent_instances = self._catalog_records()
            return dict(schemes=[self._summary(row) for row in schemes],
                        selectedSchemeId=self.state.get('selectedSchemeId'),
                        recentInstances=copy.deepcopy(recent_instances), persistenceMessage=self.notice)

    def get(self, scheme_id):
        if not isinstance(scheme_id, str) or not SCHEME_ID.fullmatch(scheme_id):
            raise ValueError('请选择有效的仿真方案。')
        with self.lock:
            if scheme_id in self.state.get('deletedSchemeIds', []):
                values = []
            else:
                values, _ = self._catalog_records()
            value = next((row for row in values if row['id'] == scheme_id), None)
            if value is None:
                raise ValueError('保存的仿真方案不存在，请刷新方案列表。')
            return copy.deepcopy(value)

    def save(self, name, configuration, scenario=None, scheme_id=None):
        # Caller validates the topology and scenario through the runtime's current contracts.
        if not isinstance(name, str) or not 1 <= len(name.strip()) <= 80 or any(ord(c) < 32 for c in name):
            raise ValueError('方案名称须为1至80个字符。')
        if not isinstance(configuration, dict) or not isinstance(configuration.get('mapDefinition'), dict):
            raise ValueError('保存方案需要完整地图和车辆、工位配置。')
        if scenario is not None and not isinstance(scenario, dict):
            raise ValueError('方案测试场景必须为对象。')
        with self.lock:
            state = copy.deepcopy(self.state)
            if scheme_id is not None:
                self.get(scheme_id)
            elif len(state['schemes']) >= 40:
                raise ValueError('最多保存40个方案，请更新已有方案。')
            value = dict(id=scheme_id or uuid.uuid4().hex, name=name.strip(), configuration=copy.deepcopy(configuration),
                         scenario=copy.deepcopy(scenario), updatedAt=time.time())
            state['schemes'] = [row for row in state['schemes'] if row['id'] != value['id']] + [value]
            state['deletedSchemeIds'] = [item for item in state.get('deletedSchemeIds', [])
                                         if item != value['id']]
            state['selectedSchemeId'] = value['id']
            self._commit(state)
            return copy.deepcopy(value)

    def delete(self, scheme_id):
        with self.lock:
            state = copy.deepcopy(self.state)
            self.get(scheme_id)
            state['schemes'] = [row for row in state['schemes'] if row['id'] != scheme_id]
            if state.get('selectedSchemeId') == scheme_id:
                state['selectedSchemeId'] = None
            state['deletedSchemeIds'] = list(dict.fromkeys(
                state.get('deletedSchemeIds', []) + [scheme_id]))
            self._commit(state)
            return self.catalog()
