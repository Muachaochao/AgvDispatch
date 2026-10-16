"""Generate explicitly synthetic offline teaching inputs from the tested scenario, never device captures."""
import argparse
import hashlib
import json
from pathlib import Path
from test_review_map_evidence import scenario


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', required=True)
    args = parser.parse_args(); dest = Path(args.out); dest.mkdir(parents=True, exist_ok=False)
    manifest = dict(origin='explicit_synthetic_project_regression', deviceValidated=False, files=[])
    for name, value in zip(('capture.json', 'map.smap', 'local-map.json', 'review-plan.json'), scenario()):
        raw = (json.dumps(value, ensure_ascii=False, indent=2)+'\n').encode('utf-8')
        (dest/name).write_bytes(raw)
        manifest['files'].append(dict(name=name, bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest()))
    (dest/'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    (dest/'README.md').write_text('# 明确合成的离线核对示例\n\n不是本车采集，不要上传车辆或复制为现场配置。'
        'SIM-MAP、站点编号、坐标、MD5、时间及版本均为测试值。地图平移(100,-50)米仅验证显式坐标变换。'
        'capture.json保存只读模拟返回，map.smap是合成二维原图，local-map.json是业务拓扑，review-plan.json提供四段路线观察。'
        '由测试场景生成器make_map_review_demo.py可复现；不修改既有真实样例模板。\n', encoding='utf-8')


if __name__ == '__main__': main()
