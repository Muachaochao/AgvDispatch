"""Offline cross-check; no networking, device approval, configuration change or fixture promotion."""
import argparse
import csv
import datetime as dt
import hashlib
import json
import math
import re
from pathlib import Path
from validate_capture import review as review_capture

ROLES = ('vehicleToPick', 'pickToWait', 'waitToDump', 'dumpToPick')
BEHAVIORS = ('unknownTaskResponse', 'taskHistoryRetention', 'controllerRestartRetention',
             'listTaskAtomicAcceptance', 'partialAcceptanceVisibilityDelay',
             'targetedCancellationCascade', 'smartCancelBehavior')


def load_input(path, limit, preserve_to=None):
    with Path(path).open('rb') as stream:
        raw = stream.read(limit + 1)
    if len(raw) > limit:
        raise ValueError(f'{Path(path).name} 超过{limit}字节预算')
    if preserve_to is not None:
        Path(preserve_to).write_bytes(raw)
    def pairs(items):
        out = {}
        for key, value in items:
            if key in out:
                raise ValueError('重复JSON键：' + key)
            out[key] = value
        return out
    def bad(value):
        raise ValueError('非有限JSON数值：' + value)
    def floating(value):
        parsed = float(value)
        if not math.isfinite(parsed):
            bad(value)
        return parsed
    return json.loads(raw.decode('utf-8-sig'), object_pairs_hook=pairs,
                      parse_constant=bad, parse_float=floating), raw


def finite(value):
    try:
        return type(value) in (int, float) and math.isfinite(value) and abs(value) <= 1e6
    except OverflowError:
        return False


def stamp(value):
    if not isinstance(value, str):
        raise ValueError('采集时间缺失')
    result = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    if result.utcoffset() is None:
        raise ValueError('采集时间必须含时区')
    return result


def angle_delta(a, b):
    return abs(math.atan2(math.sin(a-b), math.cos(a-b)))


def review(capture, smap, local, plan, *, synthetic=False):
    if not all(isinstance(v, dict) for v in (capture, smap, local, plan)):
        raise ValueError('四份输入根必须为JSON对象')
    errors, pending, station_rows, route_rows = [], [], [], []
    def check(ok, message):
        if not ok:
            errors.append(message)
        return bool(ok)
    def index(rows, field, label):
        if not isinstance(rows, list) or len(rows) > 20000:
            raise ValueError(label + '必须为不超过20000项的数组')
        result, insensitive = {}, set()
        for row in rows:
            key = row.get(field) if isinstance(row, dict) else None
            if not isinstance(key, str) or not key.strip() or len(key) > 256:
                errors.append(label + '编号缺失或过长'); continue
            if key.casefold() in insensitive:
                errors.append(label + '重复编号（含大小写冲突）：' + key); continue
            insensitive.add(key.casefold()); result[key] = row
        return result
    mode_origin = 'explicit_synthetic_project_regression' if synthetic else 'real_device_read_only_capture'
    check(capture.get('origin') == mode_origin, '采集来源声明与运行模式不符')
    capture_result = review_capture(capture, allow_synthetic=synthetic)
    errors.extend('采集结构：' + e for e in capture_result['errors'])
    if isinstance(local.get('draft'), dict):
        local = local['draft'].get('definition', {})
    if not isinstance(local, dict):
        raise ValueError('本地地图缺少definition')
    check(local.get('unit') == 'm', '上位机地图必须明确为米，禁止自动猜毫米换算')
    header = smap.get('header', {})
    if not isinstance(header, dict):
        raise ValueError('Smap缺少header')
    check(header.get('mapType') == '2D-Map' and isinstance(header.get('version'), str)
          and re.fullmatch(r'1\.0\.\d+', header['version']) is not None, '仅核对已留存1.0.x二维格式；ZIP/未知格式需先单独验收')
    samples = capture.get('samples', [])
    def response(api):
        for sample in samples if isinstance(samples, list) else []:
            if isinstance(sample, dict) and sample.get('api') == api and isinstance(sample.get('response'), dict):
                return sample['response']
        return {}
    map_reply = response(1300)
    check(header.get('mapName') == map_reply.get('current_map') and bool(header.get('mapName')), '原图mapName与车端当前地图不一致')
    transform = plan.get('localToVendor', {})
    if not isinstance(transform, dict):
        raise ValueError('localToVendor必须为对象')
    values = [transform.get(k) for k in ('yawRadians', 'translateX', 'translateY')]
    if not all(finite(v) for v in values):
        raise ValueError('必须显式提供米制刚体变换；禁止自动拟合/猜测坐标偏移')
    yaw, tx, ty = values
    check(transform.get('unit') == 'm', '坐标变换单位必须为m')
    tolerance = plan.get('xyToleranceM'); angular = plan.get('angleToleranceRadians')
    if not (finite(tolerance) and 0 < tolerance <= .1 and finite(angular) and 0 < angular <= .2):
        raise ValueError('预览比对容差需显式提供：XY(0,0.1]米，方向(0,0.2]弧度；不是厂家允差')
    confirmation = transform.get('evidence', {})
    if not (isinstance(confirmation, dict) and all(isinstance(confirmation.get(k), str) and confirmation[k].strip() for k in ('statement', 'source'))):
        pending.append('坐标系/原点/方向/单位的人工核对依据未提供')
    check(type(plan.get('schemaVersion')) is int and plan['schemaVersion'] == 1, '核对计划schemaVersion必须为1')
    native = index(smap.get('advancedPointList'), 'instanceName', '原图站点')
    observed = index(response(1301).get('stations'), 'id', '车端1301站点')
    nodes = index(local.get('nodes'), 'id', '业务节点')
    edges = index(local.get('edges'), 'id', '本地道路')
    source_curves = index(smap.get('advancedCurveList'), 'instanceName', '原图道路')
    bindings = {}
    for key, node in nodes.items():
        site = node.get('agvSiteId')
        if not isinstance(site, str) or not site.strip():
            errors.append('业务节点缺厂家映射：' + key); continue
        if site.casefold() in bindings:
            errors.append('多个业务节点映射同一厂家站点：' + site)
        bindings[site.casefold()] = key
    for key in nodes:
        check(key.casefold() not in bindings or bindings[key.casefold()] == key,
              '业务ID与其他节点的厂家ID冲突，禁止跨命名空间歧义：' + key)
    for site in sorted(set(native) | set(observed)):
        n, o = native.get(site), observed.get(site)
        row = {'siteId': site, 'logicalId': bindings.get(site.casefold(), ''), 'xyDeltaM': None,
               'localDeltaM': None, 'angleDeltaRadians': None, 'status': 'PASS', 'issues': []}
        def issue(message):
            row['issues'].append(message); row['status'] = 'FAIL'; errors.append(site + '：' + message)
        def unverified(message):
            row['issues'].append(message)
            if row['status'] != 'FAIL': row['status'] = 'PENDING'
            pending.append(site + message)
        if n is None or o is None:
            issue('原图/车端缺少同名站点'); station_rows.append(row); continue
        pos = n.get('pos')
        # Protobuf JSON permits omitted zero coordinates in source MapPos only.
        if not isinstance(pos, dict) or not all(finite(pos.get(k, 0)) for k in ('x', 'y')):
            issue('原图坐标无效'); station_rows.append(row); continue
        if not all(finite(o.get(k)) for k in ('x', 'y')):
            issue('1301缺有限XY，不能默认0'); station_rows.append(row); continue
        row.update(sourceX=pos.get('x', 0), sourceY=pos.get('y', 0), deviceX=o['x'], deviceY=o['y'])
        row['xyDeltaM'] = math.hypot(row['sourceX']-o['x'], row['sourceY']-o['y'])
        if row['xyDeltaM'] > tolerance:
            issue('原图与车端坐标超出核对容差')
        if type(n.get('ignoreDir', False)) is not bool or not finite(n.get('dir', 0)):
            issue('原图dir/ignoreDir字段类型无效')
        elif o.get('r') is not None and not finite(o['r']):
            issue('1301.r存在但不是有限方向数值')
        elif n.get('ignoreDir', False) is True:
            unverified('原图忽略方向；不声称朝向已核对')
        elif finite(n.get('dir', 0)) and finite(o.get('r')):
            row['angleDeltaRadians'] = angle_delta(n.get('dir', 0), o['r'])
            if row['angleDeltaRadians'] > angular:
                issue('原图dir与1301.r方向差超出核对容差')
        else:
            unverified('方向字段缺失，朝向未核对')
        logical = row['logicalId']
        if logical:
            node = nodes[logical]
            if node.get('agvSiteId') != site:
                issue('厂家站点编号大小写不精确一致')
            if not all(finite(node.get(k)) for k in ('x', 'y')):
                issue('本地坐标无效')
            else:
                vx = math.cos(yaw)*node['x']-math.sin(yaw)*node['y']+tx
                vy = math.sin(yaw)*node['x']+math.cos(yaw)*node['y']+ty
                row['localDeltaM'] = math.hypot(vx-o['x'], vy-o['y'])
                if row['localDeltaM'] > tolerance:
                    issue('上位机变换后坐标与车端不符')
        else:
            unverified('未在本地业务图使用，此处只比原图/车端')
        station_rows.append(row)
    for node in nodes.values():
        check(node.get('agvSiteId') in native and node.get('agvSiteId') in observed,
              '业务节点映射必须在原图和车端精确存在：' + node['id'])
    local_pairs = set()
    for edge in edges.values():
        a, b = edge.get('from'), edge.get('to')
        if not check(isinstance(a, str) and isinstance(b, str) and a in nodes and b in nodes, '本地路段端点无效：' + edge['id']):
            continue
        if not check(type(edge.get('bidirectional')) is bool, '本地路段必须明确bidirectional：' + edge['id']):
            continue
        local_pairs.add((a, b))
        if edge['bidirectional']:
            local_pairs.add((b, a))
    native_pairs = set()
    for curve in source_curves.values():
        a, b = curve.get('startPos', {}), curve.get('endPos', {})
        if not isinstance(a, dict) or not isinstance(b, dict):
            errors.append('原图路段端点格式错误：' + curve['instanceName']); continue
        start, end = a.get('instanceName'), b.get('instanceName')
        if not check(start in native and end in native, '原图道路引用缺失站点：' + curve['instanceName']):
            continue
        if curve.get('className') not in ('StraightPath', 'BezierPath'):
            pending.append('未核定道路几何：' + curve['instanceName']); continue
        for endpoint, site in ((a, start), (b, end)):
            if 'pos' in endpoint:
                ep, point = endpoint['pos'], native[site].get('pos', {})
                check(isinstance(ep, dict) and isinstance(point, dict) and all(finite(ep.get(k, 0)) and finite(point.get(k, 0)) for k in ('x', 'y'))
                      and math.hypot(ep.get('x', 0)-point.get('x', 0), ep.get('y', 0)-point.get('y', 0)) <= .001,
                      '原图道路内嵌端点与站点冲突：' + curve['instanceName'])
        native_pairs.add((start, end))
    routes = plan.get('routes')
    if not isinstance(routes, list) or len(routes) != 4:
        raise ValueError('必须提供四段运输的显式逻辑路径')
    check({r.get('role') for r in routes if isinstance(r, dict)} == set(ROLES), '四段运输角色重复或缺失')
    batch = plan.get('routeObservations', {})
    if not isinstance(batch, dict):
        raise ValueError('routeObservations必须为对象')
    check(batch.get('vehicleAlias') == capture.get('vehicleAlias') and bool(batch.get('vehicleAlias')), '路线观察车辆与采集批次不符')
    check(batch.get('mapName') == map_reply.get('current_map') and batch.get('mapMd5') == map_reply.get('current_map_md5'), '路线观察地图身份与采集不符')
    try:
        start, end = stamp(batch.get('startedAt')), stamp(batch.get('endedAt'))
        check(start <= stamp(capture.get('capturedAt')) <= end and (end-start).total_seconds() <= 120, '路线观察批次时间不一致或超过120秒离线观察预算')
    except (ValueError, TypeError, OverflowError):
        errors.append('路线观察批次需有效的起止及采集时间')
    observations = batch.get('samples', [])
    if not isinstance(observations, list) or len(observations) > 8196:
        raise ValueError('路线观察数组超过8196项预算')
    apis = [s.get('api') if isinstance(s, dict) else None for s in observations]
    check(len(apis) >= 5 and apis[:2] == [1300, 1022] and apis[-2:] == [1300, 1022]
          and all(a == 1303 for a in apis[2:-2]), '路线查询必须由前后1300/1022包围')
    lookup = {}
    previous = None
    for sample in observations:
        if not isinstance(sample, dict) or sample.get('api') not in (1300, 1022, 1303) or not isinstance(sample.get('response'), dict):
            errors.append('路线观察只允许1300/1022及1303完整请求响应'); continue
        try:
            collected = stamp(sample.get('capturedAt'))
            check(stamp(batch.get('startedAt')) <= collected <= stamp(batch.get('endedAt'))
                  and (previous is None or previous <= collected), '路线观察逐项时间越界或逆序')
            previous = collected
        except (ValueError, TypeError, OverflowError):
            errors.append('路线观察逐项时间缺失/非法')
        if sample['api'] in (1300, 1022):
            reply = sample['response']
            check(sample.get('completeResponse') is True and sample.get('request') is None, '路线批次前后无参采集缺完整声明')
            check('ret_code' not in reply or (type(reply['ret_code']) is int and reply['ret_code'] == 0), '路线批次前后采集返回错误')
            if sample['api'] == 1300:
                check((reply.get('current_map'), reply.get('current_map_md5')) == (map_reply.get('current_map'), map_reply.get('current_map_md5')), '路线查询期间地图名称/MD5变化')
            else:
                check(type(reply.get('loadmap_status')) is int and reply['loadmap_status'] == 1, '路线查询前后地图载入未完成')
            continue
        if not isinstance(sample.get('request'), dict):
            errors.append('1303观察缺对象请求'); continue
        request, reply = sample['request'], sample['response']
        if not check(sample.get('completeResponse') is True and type(reply.get('ret_code')) is int and reply['ret_code'] == 0, '路线观察缺完整成功应答'):
            continue
        key = (request.get('source_id'), request.get('target_id'))
        if not all(isinstance(k, str) for k in key):
            errors.append('路线观察端点必须为字符串'); continue
        if not check(key not in lookup, '同批1303端点重复，不能任选其一'):
            continue
        lookup[key] = reply.get('list')
    route_paths = {}
    for route in routes:
        if not isinstance(route, dict):
            raise ValueError('运输路径必须为对象')
        path = route.get('logicalPath'); role = route.get('role')
        if not isinstance(path, list) or not 2 <= len(path) <= 2048 or not all(isinstance(p, str) and p in nodes for p in path):
            errors.append(str(role) + '逻辑路径无效/少于2点/超过2048点'); continue
        route_paths[role] = path
        check(path[0] != path[-1], str(role) + '起终点相同；不绕圈伪造同站运输')
        for step, (a, b) in enumerate(zip(path, path[1:]), 1):
            site_a, site_b = nodes[a].get('agvSiteId'), nodes[b].get('agvSiteId')
            issues = []
            if a == b or site_a == site_b:
                issues.append('相邻重复点不能伪造运输')
            if (a, b) not in local_pairs:
                issues.append('本地缺指定方向直连路段')
            if (site_a, site_b) not in native_pairs:
                issues.append('原图缺支持几何的指定方向路段')
            if not isinstance(site_a, str) or not isinstance(site_b, str) or lookup.get((site_a, site_b)) != [site_a, site_b]:
                issues.append('缺1303恰好两点的相邻直连观察；多点路径不能当直连')
            row = dict(role=role, step=step, logicalFrom=a, logicalTo=b, vendorFrom=site_a,
                       vendorTo=site_b, status='FAIL' if issues else 'PASS', issues=issues)
            route_rows.append(row); errors.extend(str(role) + f'第{step}段：' + i for i in issues)
    if len(route_paths) == 4 and all(r in route_paths for r in ROLES):
        h, p, w, d = (route_paths[r] for r in ROLES)
        check(h[-1] == p[0] == d[-1] and p[-1] == w[0] and w[-1] == d[0], '四段运输端点衔接错误：应车位→取货→等待→倒料→取货')
        check(response(1100).get('current_station') == nodes[h[0]].get('agvSiteId'), '第一段来源与1100当前站点不符/缺失，不自动猜车辆位置')
    pending.extend('厂家行为待人工核定：' + key for key in BEHAVIORS)
    declared = capture.get('behaviorConfirmation', {})
    if not isinstance(declared, dict):
        declared = {}
    behavior_rows = []
    for key in BEHAVIORS:
        value = declared.get(key, {})
        if not isinstance(value, dict):
            value = {}
        statement, source = value.get('statement'), value.get('source')
        supplied = all(isinstance(v, str) and v.strip() for v in (statement, source))
        behavior_rows.append(dict(rule=key, status='provided_unverified' if supplied else 'missing',
                                  statement=statement, source=source, verified=False))
    disabled = [n['id'] for n in nodes.values() if n.get('enabled') is not True]
    disabled_edges = [e['id'] for e in edges.values() if e.get('enabled') is not True]
    return dict(offlineComparisonPassed=not errors, mode='synthetic' if synthetic else 'declared_capture',
                realDeviceValidated=False, coordinateAlignmentVerified=False, behaviorVerified=False,
                canEnableCommands=False, canUploadMap=False, originDeclaration=capture.get('origin'),
                captureReview=capture_result, stations=station_rows, routeSegments=route_rows,
                behaviorEvidence=behavior_rows,
                disabledLocalNodes=disabled, disabledLocalEdges=disabled_edges,
                errors=errors, pending=pending,
                scope='离线结构和数值关联；不证明原图/车端MD5算法、完整几何、真实来源或可调度')


def save_report(dest, result):
    (dest/'review.json').write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    for filename, rows in (('逐站核对.csv', result.get('stations', [])), ('四段运输核对.csv', result.get('routeSegments', [])), ('厂家行为待核定.csv', result.get('behaviorEvidence', []))):
        fields = list(dict.fromkeys(k for row in rows for k in row))
        with (dest/filename).open('w', encoding='utf-8-sig', newline='') as stream:
            writer = csv.DictWriter(stream, fieldnames=fields); writer.writeheader()
            for row in rows:
                safe = {k: json.dumps(v, ensure_ascii=False) if isinstance(v, list) else v for k, v in row.items()}
                # IDs are untrusted; prevent spreadsheet formula execution in exported evidence.
                safe = {k: "'"+v if isinstance(v, str) and v[:1] in ('=', '+', '-', '@', '\t', '\r') else v for k, v in safe.items()}
                writer.writerow(safe)
    text = ['# 原图、车端与业务映射离线核对', '',
            '模式：'+result.get('mode', 'invalid')+'；离线比对：'+str(result.get('offlineComparisonPassed', False)),
            '真实设备验收/坐标对齐确认/厂家行为核定/命令启用/上传许可均为false。', '',
            '逐站和逐段结果见同目录CSV；输入原字节和SHA256见inputs及review.json。', '', '## 错误']
    text += ['- '+json.dumps(x, ensure_ascii=False) for x in result.get('errors', [])] or ['无离线比对错误。']
    text += ['', '## 待核定'] + ['- '+json.dumps(x, ensure_ascii=False) for x in result.get('pending', [])]
    (dest/'核对报告.md').write_text('\n'.join(text)+'\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('capture', 'smap', 'local-map', 'plan', 'out'):
        parser.add_argument('--'+key, required=True)
    parser.add_argument('--synthetic', action='store_true', help='仅明确合成输入；不修改来源声明')
    args = parser.parse_args(); dest = Path(args.out)
    dest.mkdir(parents=True, exist_ok=False); (dest/'inputs').mkdir()
    manifests, inputs = [], []
    try:
        for key, limit in (('capture', 4<<20), ('smap', 64<<20), ('local_map', 64<<20), ('plan', 4<<20)):
            value, raw = load_input(getattr(args, key), limit, dest/'inputs'/f'{key}.json')
            inputs.append(value)
        result = review(*inputs, synthetic=args.synthetic)
    except (ValueError, OSError, TypeError, KeyError, AttributeError, RecursionError, OverflowError) as exc:
        result = dict(offlineComparisonPassed=False, realDeviceValidated=False,
                      coordinateAlignmentVerified=False, behaviorVerified=False,
                      canEnableCommands=False, canUploadMap=False, errors=[str(exc)], pending=[])
    for source in sorted((dest/'inputs').glob('*.json')):
        raw = source.read_bytes()
        manifests.append(dict(role=source.stem, sha256=hashlib.sha256(raw).hexdigest(), bytes=len(raw)))
    result['inputs'] = manifests; save_report(dest, result)
    print(json.dumps({k: result[k] for k in ('offlineComparisonPassed', 'realDeviceValidated')}))
    return 0 if result['offlineComparisonPassed'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
