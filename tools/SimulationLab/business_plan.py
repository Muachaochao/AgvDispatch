"""Validated, reproducible station call plans; does not allocate vehicles."""
import math
import random

def generate_plan(value, stations):
    if not isinstance(value, dict) or set(value) != {'seed','count','intervalSeconds','stationIds'}:
        raise ValueError('业务计划字段无效。')
    seed, count, interval, ids = (value[k] for k in ('seed','count','intervalSeconds','stationIds'))
    if type(seed) is not int or not 0 <= seed <= 2147483647: raise ValueError('随机种子须为0至2147483647。')
    if type(count) is not int or not 1 <= count <= 500: raise ValueError('呼叫总量须为1至500。')
    if type(interval) not in (int,float) or not math.isfinite(interval) or not 0 <= interval <= 60: raise ValueError('呼叫间隔须为0至60秒。')
    available = {s['stationId'] for s in stations}
    if not isinstance(ids,list) or not ids or any(not isinstance(s,str) or s not in available for s in ids) or len(set(ids)) != len(ids):
        raise ValueError('请选择方案内不重复的有效工位。')
    rng = random.Random(seed)
    ordered = sorted(ids)
    return dict(seed=seed,count=count,intervalSeconds=interval,stationIds=ordered,
        calls=[dict(stationId=rng.choice(ordered),atSeconds=i*interval,vehicleId=None) for i in range(count)])
