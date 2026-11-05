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


class SchemeStore:
    """Namespace each controller template so isolated verification cannot replace daily selection."""
    def __init__(self, workspace, template_root, validate_root):
        self.workspace = Path(workspace).absolute()
        self.template_root = Path(template_root).absolute()
        self.validate_root = validate_root
        self.lock = threading.RLock()
        directory = self.workspace / 'artifacts' / 'simulation-center'
        _no_links(directory)
        directory.mkdir(exist_ok=True, parents=True)
        scope = hashlib.sha256(str(self.template_root).casefold().encode()).hexdigest()[:16]
        self.path = directory / ('schemes-' + scope + '.json')
        _no_links(self.path)
        self.notice = None
        self.state = dict(version=1, selectedRoot=None, selectedSchemeId=None, recentInstances=[], schemes=[])
        if self.path.exists():
            try:
                if self.path.stat().st_size > 64 * 1024 * 1024:
                    raise ValueError('方案存储超出读取上限。')
                saved = json.loads(self.path.read_text(encoding='utf-8-sig'), parse_constant=_invalid_constant)
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
                self.state = saved
            except (OSError, ValueError, TypeError) as error:
                # Preserve damaged evidence; replacing it with a fresh catalogue would lose recipes.
                self.notice = '最近方案记录无法读取，已保留原文件；当前使用模板。' + str(error)

    def _commit(self, state):
        if self.notice and self.path.exists():
            raise ValueError(self.notice + ' 请先备份并修复方案记录。')
        _no_links(self.path)
        text = json.dumps(state, ensure_ascii=False, indent=2, allow_nan=False)
        if len(text.encode('utf-8')) > 64 * 1024 * 1024:
            raise ValueError('方案总大小超过64MB，请减少地图或方案数量。')
        temporary = self.path.with_name(self.path.name + '.' + uuid.uuid4().hex + '.tmp')
        try:
            temporary.write_text(text, encoding='utf-8')
            temporary.replace(self.path)
        finally:
            temporary.unlink(missing_ok=True)
        self.state = state

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
            return dict(schemes=[self._summary(row) for row in sorted(self.state['schemes'], key=lambda s: s['updatedAt'], reverse=True)],
                        selectedSchemeId=self.state.get('selectedSchemeId'),
                        recentInstances=copy.deepcopy(self.state['recentInstances']), persistenceMessage=self.notice)

    def get(self, scheme_id):
        if not isinstance(scheme_id, str) or not SCHEME_ID.fullmatch(scheme_id):
            raise ValueError('请选择有效的仿真方案。')
        with self.lock:
            value = next((row for row in self.state['schemes'] if row['id'] == scheme_id), None)
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
            self._commit(state)
            return self.catalog()
