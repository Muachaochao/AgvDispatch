"""Resolve one explicit private laboratory; old and new instances never migrate."""
import argparse
import json
from pathlib import Path
from urllib.parse import urlsplit

DEFAULT_LAB_NAME = 'web-single-vehicle-20261021-28stage'


def resolve_lab_root(argv=None, workspace=None):
    workspace = Path(workspace or Path(__file__).resolve().parents[2]).resolve()
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument('--root', default=str(workspace / 'artifacts' / DEFAULT_LAB_NAME))
    arguments, _ = parser.parse_known_args(argv)
    root = Path(arguments.root)
    if not root.is_absolute():
        root = workspace / root
    root = root.resolve()
    artifacts = (workspace / 'artifacts').resolve()
    if root == artifacts or not root.is_relative_to(artifacts) or not root.name.startswith('web-single-vehicle-'):
        raise RuntimeError('Choose one private artifacts/web-single-vehicle-* laboratory; external directories are rejected.')
    if not (root / 'lab.json').is_file():
        raise RuntimeError('Laboratory not prepared. Run prepare_lab.py with a new --name first.')
    return root


def read_ready(root):
    root = Path(root).resolve()
    ready = json.loads((root / 'ready.json').read_text(encoding='utf-8-sig'))
    target = Path(ready['root']).resolve()
    if not target.is_relative_to(root):
        raise RuntimeError('Ready runtime points outside the selected laboratory.')
    url = urlsplit(ready['url'])
    if url.scheme != 'http' or url.hostname != '127.0.0.1' or url.username or url.password or url.port != ready['port']:
        raise RuntimeError('Ready URL must identify the selected loopback Web port.')
    return ready
