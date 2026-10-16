"""Preserve original document examples separately from explicitly simulated fixtures."""
from pathlib import Path
import json, hashlib
ROOT=Path(__file__).resolve().parents[2]
DEST=ROOT/'docs/examples/robokit-regression-v1'
DEST.mkdir(parents=True,exist_ok=True)
audit=json.loads((ROOT/'docs/commissioning/evidence/20261014-robokit-document-examples/document-audit.json').read_text(encoding='utf-8'))
sources=[]
for number in (1000,1100,1301,1302,1303):
    pages=[p for p in audit['pages'] if number in p['requestNumbers']]
    candidates=[(p,b) for p in pages for b in p['jsonExamples'] if b['role']=='res' and isinstance(b['payload'],dict) and b['payload'].get('ret_code',0)==0 and (number!=1301 or 'stations' in b['payload'])]
    if candidates:
        p,b=candidates[0]
        filename=f'document-{number}-response.json'
        (DEST/filename).write_text(json.dumps(b['payload'],ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        sources.append(dict(api=number,origin='document_example_not_device_capture',file=filename,page=p['path'],sourceSha256=p['sha256'],line=b['line']))
    else:
        sources.append(dict(api=number,origin='missing_full_document_response',file=None,pages=[p['path'] for p in pages]))

responses={
 '1000':{'ret_code':0,'version':'SIMULATED-NOT-DEVICE','netprotocol_version':'SIMULATED-NOT-DEVICE'},
 '1100':{'ret_code':0,'x':1.25,'y':-2.5,'angle':1.57,'confidence':0.98,'current_station':'LM1','last_station':'LM0','battery_level':0.76,'blocked':False,'emergency':False,'navigation_status':2,'vx':0.2,'vy':0.0,'w':0.1,'charging':False,'voltage':48.2,'current':-1.5,'battery_temp':26.0},
 '1300':{'ret_code':0,'current_map':'SIM-MAP','current_map_md5':'0123456789abcdef0123456789abcdef'},
 '1022':{'ret_code':0,'loadmap_status':1},
 '1060':{'ret_code':0,'locked':False,'nick_name':'SIM-ONLY'},
 '1301':{'ret_code':0,'stations':[{'id':'LM1','type':'LocationMark','x':0,'y':0,'r':0},{'id':'LM2','type':'LocationMark','x':1,'y':0,'r':0},{'id':'AP1','type':'ActionPoint','x':2,'y':0,'r':1.57}]},
 '1302':{'ret_code':0,'map_info':[{'name':'SIM-MAP.smap','md5':'0123456789abcdef0123456789abcdef'}]},
 '1303':{'ret_code':0,'list':['LM1','LM2','AP1']},
 '1110':{'ret_code':0,'task_status_package':{'source_name':'LM1','target_name':'AP1','percentage':0.25,'task_status_list':[]}},
 '3066':{'ret_code':0},'3003':{'ret_code':0}}
bundle={'schemaVersion':1,'origin':'explicit_synthetic_project_regression','deviceValidated':False,'description':'完整响应结构仅覆盖项目使用字段，不代表1100厂家完整字段全集；没有本车采集','responses':responses,
 'taskScenarios':{'unknown':[404,404],'running':[2,2],'partial':[2,404],'historyCleared':[404,404],'nonCascade':[6,2],'cascade':[6,6],'completed':[4,4],'suspended':[3,2]},
 'taskIds':['FIXTURE-ROOT-01','FIXTURE-ROOT']}
(DEST/'simulated-bundle.json').write_text(json.dumps(bundle,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
files=[dict(path=p.name,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sorted(DEST.glob('*.json')) if p.name.startswith('document-') or p.name=='simulated-bundle.json']
(DEST/'fixture-manifest.json').write_text(json.dumps({'schemaVersion':1,'realDeviceReady':False,'sources':sources,'files':files,'missing':['本车1100完整脱敏返回','本车1000固件与协议版本','本车1300/1301/1302/1303同批次地图及路线','1110历史清除／重启保留实测','3066部分接收与可见延迟说明','3003级联与智能取消说明']},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'fixtureFiles':len(files),'documentResponses':len([s for s in sources if s['file']]),'realDeviceReady':False}))
