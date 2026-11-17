"""Bind isolated runs to the daily release without copying daily state or ports."""
import hashlib
import json
from pathlib import Path


def _read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def _hash(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest().upper()


def _options_hash(options):
    return hashlib.sha256(json.dumps(options, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest().upper()


def current_release(workspace):
    publish = (Path(workspace).parent / '发布包').resolve()
    manifest = publish / '最新版Web.json'
    if not manifest.exists():
        return None
    try:
        value = _read(manifest)
        directory = (publish / value['directory']).resolve()
        if (not directory.is_relative_to(publish) or directory == publish or
                value.get('url') != 'http://127.0.0.1:5199/' or not value.get('releaseId')):
            raise ValueError('5199运行包清单路径或入口无效。')
        directories = dict(service=directory / 'Service', webhost=directory / 'Web/host')
        files = {}
        for component, source in directories.items():
            if not source.resolve().is_relative_to(publish):
                raise ValueError('5199运行包组件路径越界。')
            name = 'AgvDispatch.Service' if component == 'service' else 'AgvDispatch.WebHost'
            if any(not (source / (name + suffix)).is_file() for suffix in ('.exe', '.dll')):
                raise ValueError('5199运行包缺少必需的exe/dll。')
            for path in sorted(source.rglob('*')):
                if path.is_file() and path.suffix.lower() in ('.dll', '.exe'):
                    if not path.resolve().is_relative_to(publish):
                        raise ValueError('5199运行包文件路径越界。')
                    files[(Path(component) / path.relative_to(source)).as_posix()] = _hash(path)
        options = _read(publish / '运行数据/后台5199/dispatch-optimization.json')['Options']
        if not isinstance(options, dict) or options.get('Mode') not in ('Global', 'Shadow', 'Disabled'):
            raise ValueError('5199算法选项无效。')
        metadata = dict(releaseId=value['releaseId'], algorithmMode=options['Mode'],
                        serviceSha256=files['service/AgvDispatch.Service.dll'],
                        webHostSha256=files['webhost/AgvDispatch.WebHost.dll'],
                        optionsSha256=_options_hash(options), files=files)
        return dict(directories=directories, options=options, metadata=metadata)
    except (OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise ValueError('无法读取5199现用运行包或算法配置；禁止回退旧模板。') from error


def verify_release(root, release):
    if release is None:
        return
    root = Path(root)
    for name, expected in release['metadata']['files'].items():
        path = root / name
        if not path.is_file() or _hash(path) != expected:
            raise ValueError('隔离运行包与5199现用版本不一致，请重新加载方案。')
    for name in ['service-settings.json', 'service/appsettings.json']:
        if _read(root / name).get('DispatchOptimization') != release['options']:
            raise ValueError('隔离算法配置与5199现用配置不一致，请重新加载方案。')
    saved = root / 'data/dispatch-optimization.json'
    if saved.exists() and _read(saved).get('Options') != release['options']:
        raise ValueError('隔离持久化算法配置与5199不一致，请重新加载方案。')
