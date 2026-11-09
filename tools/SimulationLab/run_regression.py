"""Run the same simulation HTTP actions exposed by the Web control page."""
import argparse
import json
import time
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', required=True, help='仿真控制台URL，仅允许127.0.0.1。')
    parser.add_argument('--scenarios', nargs='+', default=['normal-cycle', 'simultaneous-calls', 'disconnect-reconnect', 'plc-delay', 'service-restart'])
    parser.add_argument('--speed-scale', type=float, default=3.0,
                        help='虚拟车辆速度倍率；现场对齐测试使用0.75。')
    args = parser.parse_args(); url = urlsplit(args.url)
    if url.scheme != 'http' or url.hostname != '127.0.0.1' or url.username or url.password: raise ValueError('回环URL必需。')
    if not 0.1 <= args.speed_scale <= 10:
        raise ValueError('仿真速度倍率必须在0.1至10之间。')
    base = f'http://127.0.0.1:{url.port}'
    def api(action='', body=None):
        request = Request(base + '/api/simulation' + action, data=json.dumps(body).encode() if body is not None else None, headers={'Content-Type':'application/json'})
        with urlopen(request, timeout=45) as response: return json.load(response)
    def wait(predicate, timeout):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            status = api()
            if status.get('phase') == 'failed': raise RuntimeError(status.get('error'))
            if predicate(status): return status
            time.sleep(1)
        raise TimeoutError('等待仿真状态超时。')
    status = api()
    if not status.get('enabled'): raise RuntimeError('仿真未启用。')
    if not status.get('running'):
        # New-instance intentionally preserves any previous fault state.
        api('/new-instance', {}); api('/start', {})
        status = wait(lambda s:s.get('running') and s.get('fresh'), 180)
    api('/control', dict(vehicles={key:dict(speedScale=args.speed_scale) for key in status['device']['cars']}))
    for name in args.scenarios:
        print('RUN ' + name, flush=True)
        api('/run', dict(name=name, rounds=1, intervalSeconds=0, timeoutSeconds=720))
        status = wait(lambda s:s.get('scenario',{}).get('status') in ('passed','failed','aborted'), 1500)
        report = api('/report')
        print(json.dumps(report, ensure_ascii=False), flush=True)
        if report['status'] != 'passed': raise RuntimeError(name + ': ' + report['message'])
    print('PASS all selected actual Web / Service / Robokit / S7 scenarios', flush=True)


if __name__ == '__main__': main()
