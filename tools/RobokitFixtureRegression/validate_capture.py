"""Offline review only. Never connect devices or infer authenticity from schema validity."""
import argparse, datetime, hashlib, json, math, re
from pathlib import Path

ALLOWED={1000,1100,1300,1022,1060,1301,1302,1303,1110}
REQUIRED={1000,1100,1300,1022,1301,1302,1303,1110}

def load(path):
    raw=Path(path).read_bytes()
    if len(raw)>4*1024*1024: raise ValueError('样例超过4MiB离线预算')
    def pairs(values):
        out={}
        for key,value in values:
            if key in out: raise ValueError('重复JSON键：'+key)
            out[key]=value
        return out
    def constant(value): raise ValueError('非法JSON数值：'+value)
    def floating(value):
        result=float(value)
        if not math.isfinite(result):raise ValueError('非有限JSON数值')
        return result
    return json.loads(raw.decode('utf-8-sig'),object_pairs_hook=pairs,parse_constant=constant,parse_float=floating),raw

def review(data, *, allow_synthetic=False):
    errors=[]; warnings=['来源为提交者声明，离线结构校验不能证明真实设备来源或厂家行为。']
    def check(ok,text):
        if not ok: errors.append(text)
        return bool(ok)
    def number(v):
        try:return type(v) in (int,float) and math.isfinite(v)
        except OverflowError:return False
    def text(v): return isinstance(v,str) and bool(v.strip())
    def md5(v): return isinstance(v,str) and re.fullmatch('[0-9a-fA-F]{32}',v) is not None
    if not isinstance(data,dict): return {'schemaReady':False,'realDeviceValidated':False,'errors':['根必须是对象'],'warnings':warnings}
    check(type(data.get('schemaVersion')) is int and data.get('schemaVersion')==1,'schemaVersion必须为1')
    check(data.get('origin')=='real_device_read_only_capture' or (allow_synthetic and data.get('origin')=='explicit_synthetic_project_regression'),'origin未声明真实只读采集；模板/模拟/文档例子不可作为本车采集导入')
    check(data.get('deviceValidated') is False,'deviceValidated必须保持false；此工具不能批准实车验收')
    for key in ['vehicleAlias','actualRobokitVersion','actualProtocolVersion','anonymizationNotes']:
        check(text(data.get(key)),key+'必须填写确认信息，不能留空')
    try:
        stamp=datetime.datetime.fromisoformat(data.get('capturedAt','').replace('Z','+00:00'))
        check(stamp.utcoffset() is not None,'capturedAt必须含时区')
    except (ValueError,TypeError,AttributeError): errors.append('capturedAt必须为带时区ISO时间')
    check(data.get('actualStatusPortConfirmed') is True,'状态端口尚未人工确认')
    check(data.get('sameCollectionBatch') is True,'尚未确认来自同车同一采集批次')
    samples=data.get('samples')
    if not isinstance(samples,list): samples=[];errors.append('samples必须为数组')
    if len(samples)>100: errors.append('samples超过100项预算');samples=samples[:100]
    grouped={}; order=[]
    for i,s in enumerate(samples):
        if not isinstance(s,dict):errors.append(f'样例{i}必须为对象');continue
        api=s.get('api');label=f'样例{i}/API{api}'
        if not check(type(api) is int and api in ALLOWED,label+'不在只读白名单'):continue
        order.append(api);grouped.setdefault(api,[]).append(s)
        check('request' in s,label+'必须记录request，无参写null')
        check(s.get('completeResponse') is True,label+'须声明保留完整脱敏响应，不能截字段')
        response=s.get('response')
        if not check(isinstance(response,dict),label+'缺少对象响应'):continue
        if 'ret_code' in response:check(type(response['ret_code']) is int and response['ret_code']==0,label+'该基准需要成功整数ret_code，错误响应应另建负例')
        if api in {1000,1300,1022,1060,1301}:check(s.get('request') is None,label+'无参请求应记录null，不能用空JSON代替零长度')
        elif api in {1100,1302,1303,1110}:check(isinstance(s.get('request'),dict),label+'必须记录对象请求')
        if api==1100:
            for key in ['x','y','angle','confidence','battery_level','vx','vy','w','voltage','current','battery_temp']:
                if key in response and response[key] is not None:check(number(response[key]),label+'/'+key+'必须是有限数值，不能用字符串或布尔代替')
            for key in ['blocked','emergency','charging']:
                if key in response and response[key] is not None:check(type(response[key]) is bool,label+'/'+key+'必须是布尔')
            missing=[k for k in ['x','y','angle','confidence','battery_level','current_station'] if response.get(k) is None]
            if missing:warnings.append('1100缺少现用字段：'+','.join(missing)+'；保持未知，不补造')
    check(REQUIRED.issubset(grouped),'缺少必需API：'+','.join(map(str,sorted(REQUIRED-set(grouped)))))
    check(all(len(rows)==1 for api,rows in grouped.items() if api not in {1300,1022}),'除前后1300/1022外不允许重复API；额外场景另建基准')
    def first(api):
        rows=grouped.get(api,[])
        value=rows[0].get('response') if rows else None
        return value if isinstance(value,dict) else {}
    info=first(1000)
    check(text(info.get('version')) and info.get('version')==data.get('actualRobokitVersion'),'1000版本与确认元数据不一致/缺失')
    check(text(info.get('netprotocol_version')) and info.get('netprotocol_version')==data.get('actualProtocolVersion'),'1000协议版本与确认元数据不一致/缺失')
    maps=grouped.get(1300,[]);loads=grouped.get(1022,[])
    check(len(maps)==2 and len(loads)==2,'需要采集前后各一份1300和1022，不能凭同批声明代替')
    if len(maps)==2 and len(loads)==2:
        positions={a:[i for i,n in enumerate(order) if n==a] for a in (1300,1022,1301,1302,1303)}
        check(all(positions[a] and positions[1300][0]<min(positions[a])<max(positions[1300]) and positions[1022][0]<min(positions[a])<max(positions[1022]) for a in (1301,1302,1303)),'地图核验API必须位于前后1300/1022采集之间')
        a=first(1300);b=maps[-1].get('response') or {}
        check(isinstance(b,dict) and (a.get('current_map'),a.get('current_map_md5'))==(b.get('current_map'),b.get('current_map_md5')),'前后地图名称/MD5变化')
        check(all(isinstance(s.get('response'),dict) and type(s['response'].get('loadmap_status')) is int and s['response']['loadmap_status']==1 for s in loads),'前后地图载入未完成')
    map_reply=first(1300);name=map_reply.get('current_map');digest=map_reply.get('current_map_md5')
    check(text(name) and md5(digest),'1300地图名称或32位MD5无效')
    station_list=first(1301).get('stations');ids=[]
    if check(isinstance(station_list,list) and 0<len(station_list)<=10000,'1301 stations缺失/为空/超过预算'):
        for s in station_list:
            if check(isinstance(s,dict) and text(s.get('id')),'1301站点对象/id无效'):
                ids.append(s['id'])
                for key in ['x','y','r']:
                    if key in s and s[key] is not None:check(number(s[key]),'1301 '+key+'必须为有限数值')
        check(len(ids)==len(set(ids)),'1301重复站点编号')
    hashes=first(1302).get('map_info');file=name if isinstance(name,str) and name.endswith('.smap') else str(name)+'.smap'
    req=grouped.get(1302,[{}])[0].get('request') or {}
    check(isinstance(req,dict) and req.get('map_names')==[file],'1302请求地图文件名与1300不一致')
    if check(isinstance(hashes,list),'1302 map_info必须为数组'):
        matching=[h for h in hashes if isinstance(h,dict) and h.get('name')==file]
        check(len(matching)==1 and md5(matching[0].get('md5')) and matching[0].get('md5')==digest,'1302匹配地图必须唯一且MD5与1300完全一致')
    path=first(1303).get('list');request=grouped.get(1303,[{}])[0].get('request') or {}
    if check(isinstance(path,list) and 0<len(path)<=2048 and all(text(p) for p in path),'1303路径缺失/无效/超过预算'):
        check(isinstance(request,dict) and path[0]==request.get('source_id') and path[-1]==request.get('target_id'),'1303响应端点与请求不一致')
        check(all(p in ids for p in path),'1303途经站点不在1301中')
    for row in grouped.get(1110,[]):
        req=row.get('request');reply=row.get('response');package=reply.get('task_status_package',{}) if isinstance(reply,dict) else {}
        tasks=package.get('task_status_list') if isinstance(package,dict) else None
        if check(isinstance(tasks,list),'1110缺少task_status_list数组'):
            task_ids=[]
            for task in tasks:
                if check(isinstance(task,dict) and text(task.get('task_id')),'1110任务id无效'):
                    task_ids.append(task['task_id']);check(type(task.get('status')) is int and task['status'] in {0,1,2,3,4,5,6,7,404},'1110未知/非法状态码')
            check(len(task_ids)==len(set(task_ids)),'1110重复任务id')
            if isinstance(req,dict) and 'task_ids' in req:
                requested=req['task_ids'];valid=isinstance(requested,list) and all(text(t) for t in requested)
                check(valid,'1110请求task_ids必须为非空字符串数组（允许空数组）')
                if valid:check(set(task_ids).issubset(requested),'1110响应含未请求编号')
    behavior=data.get('behaviorConfirmation')
    pending=[]
    for key in ['unknownTaskResponse','taskHistoryRetention','controllerRestartRetention','listTaskAtomicAcceptance','partialAcceptanceVisibilityDelay','targetedCancellationCascade','smartCancelBehavior']:
        value=behavior.get(key) if isinstance(behavior,dict) else None
        if not (isinstance(value,dict) and text(value.get('statement')) and text(value.get('source'))):pending.append(key)
    return {'schemaReady':not errors,'realDeviceValidated':False,'behaviorVerified':False,'pendingBehaviorEvidence':pending,'errors':errors,'warnings':warnings,'sampleCount':len(samples)}

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('source');p.add_argument('--out',required=True);args=p.parse_args()
    dest=Path(args.out);dest.mkdir(parents=True,exist_ok=False)
    try:
        data,raw=load(args.source);result=review(data)
        result.update(sourceSha256=hashlib.sha256(raw).hexdigest(),originDeclaration=data.get('origin') if isinstance(data,dict) else None)
        # Preserve submitted bytes under a neutral name, never a label claiming physical authenticity.
        (dest/'submitted.json').write_bytes(raw)
    except (ValueError,OSError,RecursionError) as ex:result={'schemaReady':False,'realDeviceValidated':False,'errors':[str(ex)]}
    (dest/'review.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'schemaReady':result['schemaReady'],'realDeviceValidated':False,'errors':len(result['errors'])}))
    return 0 if result['schemaReady'] else 2

if __name__=='__main__':raise SystemExit(main())
