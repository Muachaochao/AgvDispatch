"""Use normal application HTTP operations to import, save and publish the file."""
from pathlib import Path
import json
import urllib.request
from lab_paths import read_ready, resolve_lab_root

root=resolve_lab_root()
lab=json.loads((root/'lab.json').read_text(encoding='utf-8'))
base=read_ready(root)['url'].rstrip('/') if (root/'ready.json').exists() else f"http://127.0.0.1:{lab['webPort']}"

def api(path,data=None,raw=None):
    payload=raw if raw is not None else json.dumps(data,ensure_ascii=False).encode() if data is not None else None
    request=urllib.request.Request(base+path,data=payload,headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(request,timeout=40) as response:
        result=json.load(response)
    if isinstance(result,dict) and result.get('accepted') is False:
        raise RuntimeError(result)
    return result

preview=api('/api/maps/import?format=json',raw=(root/'navigation.agvmap.json').read_bytes())
assert preview.get('definition'),preview
definition=preview['definition']
name=definition['name']
created=api('/api/maps/create',dict(name=name,description='实际文件及主后台闭环联调；仅回环虚拟设备，不是实车测绘',backgroundFilePath='',coordinateSystem=definition['coordinateSystem'],unit='m',coordinateScale=1))
workspace=created['workspace']
draft=workspace['draft'];project=workspace['project']
saved=api('/api/maps/draft',dict(mapId=project['mapId'],versionId=draft['versionId'],name=name,description=project['description'],backgroundFilePath='',coordinateSystem=definition['coordinateSystem'],unit='m',coordinateScale=1,definition=definition,expectedEditToken=draft['editToken']))
draft=saved['workspace']['draft']
published=api('/api/maps/publish',dict(mapId=project['mapId'],versionId=draft['versionId'],expectedEditToken=draft['editToken']))
workspace=published['workspace']
metadata=dict(mapId=project['mapId'],versionId=workspace['published']['versionId'])
(root/'map-operation-results.json').write_text(json.dumps(dict(importPreview=preview,created=created,saved=saved,published=published,**metadata),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(metadata))
