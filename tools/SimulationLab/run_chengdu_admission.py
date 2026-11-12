"""Run a reproducible Chengdu PLC backlog through an isolated control portal.

No real endpoint, task insertion, vehicle choice or lease editing is used.
"""
import argparse
import json
import time
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


def build_calls(stations, cycles=1):
    if type(cycles) is not int or cycles < 1 or not stations or len(stations)*cycles > 500:
        raise ValueError('Call plan must contain 1..500 calls and at least one cycle.')
    return [dict(stationId=station, atSeconds=0, vehicleId=None)
            for _ in range(cycles) for station in stations]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', required=True)
    parser.add_argument('--source-instance', type=Path, required=True)
    parser.add_argument('--map', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--admission', type=int, default=0)
    parser.add_argument('--stations', nargs='+', default=[f'CNC-{i:03}' for i in range(282,294)])
    parser.add_argument('--cycles', type=int, default=1,
                        help='Repeat the station plan; each station waits for its own PLC closure before reissuing.')
    parser.add_argument('--timeout', type=int, default=7200)
    args=parser.parse_args(); url=urlsplit(args.url)
    calls = build_calls(args.stations, args.cycles)
    if url.scheme!='http' or url.hostname!='127.0.0.1' or url.port in (None,5198,5199) or url.username or url.password:
        raise ValueError('Only an isolated loopback portal is allowed.')
    args.output.mkdir(parents=True,exist_ok=False)
    def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
    def api(action='',body=None):
        request=Request(f'http://127.0.0.1:{url.port}/api/simulation'+action,
            data=None if body is None else json.dumps(body,ensure_ascii=False).encode(),headers={'Content-Type':'application/json'})
        with urlopen(request,timeout=120) as response:return json.load(response)
    def save(name,value):(args.output/name).write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
    status=api()
    if status.get('running') or status.get('scenario',{}).get('status')=='running':
        raise ValueError('Portal has an active environment; preserve it and use a separate portal.')
    config=read(args.source_instance/'lab.json')['simulationConfiguration']
    config.pop('mapName',None);config.pop('mapIdentity',None)
    config['mapDefinition']=read(args.map)
    # Validate identities before stopping/starting any environment. Both logical
    # node IDs and the vendor site IDs must remain one-to-one.
    sites = [n.get('agvSiteId', n['id']) for n in config['mapDefinition']['nodes'] if n.get('enabled', True)]
    invalid = sorted({s for s in args.stations if sites.count(s) != 1})
    if invalid: raise ValueError('Missing or ambiguous station sites: ' + ', '.join(invalid))
    config['downstreamAdmissionCapacity']=args.admission
    raw=(args.source_instance/'plc-signals.source.csv').read_text(encoding='utf-8-sig')
    config['plcSignalTable']=dict(fileName='chengdu-305-simulation.csv',csvText=raw)
    for vehicle in config['vehicles']:vehicle['speedScale']=1.0
    save('requested-configuration.json',config)
    status=api('/configure',config);save('configured.json',status)
    api('/start',{})
    for _ in range(240):
        status=api()
        if status.get('phase')=='failed':raise RuntimeError(status.get('error'))
        if status.get('running') and status.get('fresh'):break
        time.sleep(1)
    else:raise TimeoutError('Isolated startup timed out; evidence retained.')
    scenario=dict(name='dispatch-backlog',rounds=1,intervalSeconds=0,timeoutSeconds=args.timeout,
        calls=calls)
    save('call-plan.json',dict(stations=args.stations, cycles=args.cycles, totalCalls=len(calls), scenario=scenario))
    saved=api('/scheme-save',dict(name=f'成都五车·{len(args.stations)}工位×{args.cycles}轮·{len(calls)}单·准入{args.admission}',scenario=scenario))
    save('saved-scheme.json',saved)
    status=api('/run',scenario);save('started.json',status)
    print(json.dumps(dict(root=status.get('rootName'),scenario=status.get('scenario')),ensure_ascii=True),flush=True)
    deadline=time.monotonic()+args.timeout+300
    previous=None
    while time.monotonic()<deadline:
        live=api('/live');save('latest-live.json',live)
        progress=dict(scenario=live.get('scenario'),summary=live.get('taskSummary'),error=live.get('error'))
        text=json.dumps(progress,ensure_ascii=True)
        if text!=previous:print(text,flush=True);previous=text
        if live.get('scenario',{}).get('status') in ('passed','failed','aborted'):
            report=api('/report');save('report.json',report)
            print('FINAL '+str(report.get('status')),flush=True)
            return 0 if report.get('status')=='passed' else 1
        time.sleep(10)
    raise TimeoutError('Observation budget exhausted; environment and tasks retained.')


if __name__=='__main__':raise SystemExit(main())
