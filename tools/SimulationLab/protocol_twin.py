"""Bounded loopback TCP/UDP/HTTP server driven by declarative JSON rules."""
import json
import re
import socketserver
import struct
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import simulation_clock

MAX_FRAME = 1024 * 1024


def _text(value):
    return value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, separators=(',', ':'))


def _subset(expected, actual):
    if isinstance(expected, dict):
        return isinstance(actual, dict) and all(key in actual and _subset(value, actual[key]) for key, value in expected.items())
    if isinstance(expected, list):
        return isinstance(actual, list) and len(expected) == len(actual) and all(_subset(a, b) for a, b in zip(expected, actual))
    return expected == actual


def _lookup(path, roots):
    parts = path.split('.')
    value = roots.get(parts[0])
    for part in parts[1:]:
        if isinstance(value, dict): value = value.get(part)
        elif isinstance(value, list) and part.isdigit() and int(part) < len(value): value = value[int(part)]
        else: return ''
    return value


def _render(value, state, request, counters, rule_id):
    if isinstance(value, dict): return {key: _render(item, state, request, counters, rule_id) for key, item in value.items()}
    if isinstance(value, list): return [_render(item, state, request, counters, rule_id) for item in value]
    if not isinstance(value, str): return value
    exact = re.fullmatch(r'\{\{\s*([^{}]+?)\s*\}\}', value)
    if exact:
        return _lookup(exact.group(1), dict(state=state, request=request, counters=counters, rules={rule_id: counters.get(rule_id, 0)}))
    def replace(match):
        return _text(_lookup(match.group(1).strip(), dict(state=state, request=request, counters=counters, rules={rule_id: counters.get(rule_id, 0)})))
    return re.sub(r'\{\{\s*([^{}]+?)\s*\}\}', replace, value)


def validate_protocol_config(value):
    if not isinstance(value, dict) or set(value) - {'name', 'transport', 'port', 'framing', 'rules', 'defaultResponse', 'state'}:
        raise ValueError('协议脚本包含未知字段。')
    result = dict(name=value.get('name') or 'Protocol twin', transport=value.get('transport', 'tcp'),
                  port=value.get('port', 0), framing=value.get('framing', 'line-json'),
                  rules=value.get('rules'), defaultResponse=value.get('defaultResponse'), state=value.get('state', {}))
    if result['transport'] not in ('tcp', 'udp', 'http'): raise ValueError('协议传输须为tcp、udp或http。')
    if result['framing'] not in ('line-json', 'length-prefix-4be', 'length-prefix-4le'):
        raise ValueError('协议分帧格式无效。')
    if type(result['port']) is not int or (result['port'] != 0 and not 1024 <= result['port'] <= 65535) or result['port'] in (5198, 5199):
        raise ValueError('协议端口须为0或1024至65535，且不能使用日常Web端口。')
    if not isinstance(result['rules'], list) or not 1 <= len(result['rules']) <= 200:
        raise ValueError('协议脚本须包含1至200条规则。')
    if not isinstance(result['state'], dict): raise ValueError('协议状态须为JSON对象。')
    ids = set()
    for index, rule in enumerate(result['rules']):
        if not isinstance(rule, dict) or set(rule) - {'id', 'match', 'matchText', 'regex', 'when', 'response', 'httpStatus',
                                                     'delayMs', 'drop', 'close', 'setState', 'increment'}:
            raise ValueError(f'第{index + 1}条规则字段无效。')
        rule_id = rule.get('id') or f'rule-{index + 1}'
        if not isinstance(rule_id, str) or not rule_id.strip() or rule_id in ids: raise ValueError('协议规则ID必须唯一。')
        ids.add(rule_id)
        if rule.get('match') is not None and not isinstance(rule['match'], (dict, list, str, int, float, bool)):
            raise ValueError('规则match类型无效。')
        if rule.get('when') is not None and not isinstance(rule['when'], dict): raise ValueError('规则when必须是状态对象。')
        if rule.get('matchText') is not None and (not isinstance(rule['matchText'], str) or len(rule['matchText']) > 1024):
            raise ValueError('规则matchText须为最多1024字符字符串。')
        if rule.get('regex') is not None and (not isinstance(rule['regex'], str) or len(rule['regex']) > 256):
            raise ValueError('规则regex须为最多256字符字符串。')
        if not isinstance(rule.get('delayMs', 0), (int, float)) or not 0 <= rule.get('delayMs', 0) <= 300000:
            raise ValueError('规则延迟须为0至300000毫秒。')
        if not isinstance(rule.get('drop', False), bool) or not isinstance(rule.get('close', False), bool):
            raise ValueError('drop/close必须为布尔值。')
        if rule.get('httpStatus') is not None and (type(rule['httpStatus']) is not int or not 100 <= rule['httpStatus'] <= 599):
            raise ValueError('HTTP状态码无效。')
        if rule.get('increment') is not None and not isinstance(rule['increment'], dict): raise ValueError('increment必须是对象。')
    return result


class _State:
    def __init__(self, root, config):
        self.root = Path(root).resolve()
        self.config = config
        self.state = dict(config['state'])
        self.counters = {}
        self.lock = threading.Lock()
        self.stop_event = threading.Event()
        self.started_at = simulation_clock.CLOCK.time()
        self.request_count = 0
        self.last_event = None

    def journal(self, value):
        line = dict(time=simulation_clock.CLOCK.time(), **value)
        with (self.root / 'protocol-events.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(line, ensure_ascii=False) + '\n')
        self.last_event = line
        self.write_status()

    def write_status(self, running=True, error=None):
        value = dict(running=running, name=self.config['name'], transport=self.config['transport'],
                     framing=self.config['framing'], rules=len(self.config['rules']), port=getattr(self, 'port', self.config['port']),
                     state=self.state, counters=self.counters, requests=self.request_count, startedAt=self.started_at,
                     lastEvent=self.last_event, error=error)
        temporary = self.root / 'protocol-state.tmp'
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
        temporary.replace(self.root / 'protocol-state.json')

    def handle(self, request):
        with self.lock:
            self.request_count += 1
            for rule in self.config['rules']:
                when = rule.get('when')
                if when is not None and not _subset(when, self.state): continue
                expected = rule.get('match')
                if expected is not None and not (_subset(expected, request) if isinstance(expected, (dict, list)) else _text(expected) == _text(request)): continue
                match_text = rule.get('matchText')
                if match_text is not None and str(match_text) not in _text(request): continue
                if rule.get('regex') is not None:
                    try: matched = re.search(rule['regex'], _text(request)) is not None
                    except re.error: matched = False
                    if not matched: continue
                rule_id = rule.get('id') or 'rule'
                self.counters[rule_id] = self.counters.get(rule_id, 0) + 1
                for key, increment in (rule.get('increment') or {}).items():
                    if type(increment) not in (int, float): raise ValueError('increment values must be numbers')
                    self.state[key] = self.state.get(key, 0) + increment
                if isinstance(rule.get('setState'), dict): self.state.update(rule['setState'])
                response = _render(rule.get('response', {}), self.state, request, self.counters, rule_id)
                self.journal(dict(kind='rule-matched', ruleId=rule_id, request=request, response=response,
                                  delayMs=rule.get('delayMs', 0), drop=rule.get('drop', False), close=rule.get('close', False)))
                return rule, response
            response = _render(self.config.get('defaultResponse') or {}, self.state, request, self.counters, 'default')
            self.journal(dict(kind='default-matched', request=request, response=response))
            return None, response


def _decode(data, framing):
    try:
        if framing == 'line-json': return json.loads(data.rstrip(b'\r\n').decode('utf-8'))
        return json.loads(data.decode('utf-8'))
    except (UnicodeDecodeError, json.JSONDecodeError): return data.rstrip(b'\r\n').decode('utf-8', errors='replace')


def _encode(response, framing):
    data = _text(response).encode('utf-8')
    if framing == 'length-prefix-4be': return struct.pack('>I', len(data)) + data
    if framing == 'length-prefix-4le': return struct.pack('<I', len(data)) + data
    return data + b'\n'


def _read_tcp(handler, framing):
    if framing == 'line-json': return handler.rfile.readline(MAX_FRAME + 1)
    prefix = handler.rfile.read(4)
    if len(prefix) != 4: raise EOFError
    size = struct.unpack('>I' if framing == 'length-prefix-4be' else '<I', prefix)[0]
    if not 0 < size <= MAX_FRAME: raise ValueError('Protocol frame exceeds 1MiB')
    return handler.rfile.read(size)


def start_protocol(root, raw_config):
    config = validate_protocol_config(raw_config)
    state = _State(root, config)
    server = None
    thread = None
    if config['transport'] == 'tcp':
        class TcpHandler(socketserver.StreamRequestHandler):
            def handle(self):
                while not state.stop_event.is_set():
                    try: data = _read_tcp(self, config['framing'])
                    except EOFError: break
                    if not data: break
                    request = _decode(data, config['framing'])
                    rule, response = state.handle(request)
                    delay = float(rule.get('delayMs', 0) / 1000 if rule else 0)
                    if delay: state.stop_event.wait(simulation_clock.CLOCK.business_delay(delay))
                    if rule and rule.get('drop'): continue
                    self.wfile.write(_encode(response, config['framing'])); self.wfile.flush()
                    if rule and rule.get('close'): break
        class Server(socketserver.ThreadingTCPServer): allow_reuse_address = False; daemon_threads = True
        server = Server(('127.0.0.1', config['port']), TcpHandler)
    elif config['transport'] == 'udp':
        class UdpHandler(socketserver.BaseRequestHandler):
            def handle(self):
                data, sock = self.request
                request = _decode(data, config['framing'])
                rule, response = state.handle(request)
                delay = float(rule.get('delayMs', 0) / 1000 if rule else 0)
                if delay: state.stop_event.wait(simulation_clock.CLOCK.business_delay(delay))
                if not (rule and rule.get('drop')): sock.sendto(_encode(response, config['framing']), self.client_address)
        class Server(socketserver.ThreadingUDPServer): allow_reuse_address = False; daemon_threads = True
        server = Server(('127.0.0.1', config['port']), UdpHandler)
    else:
        class HttpHandler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def do_GET(self): self._serve()
            def do_POST(self): self._serve()
            def _serve(self):
                length = int(self.headers.get('Content-Length', 0))
                if length > MAX_FRAME: self.send_error(413); return
                body = self.rfile.read(length) if length else b''
                try: payload = json.loads(body) if body else {}
                except json.JSONDecodeError: payload = body.decode('utf-8', errors='replace')
                request = dict(method=self.command, path=self.path, body=payload,
                               headers={key: value for key, value in self.headers.items()})
                rule, response = state.handle(request)
                delay = float(rule.get('delayMs', 0) / 1000 if rule else 0)
                if delay: state.stop_event.wait(simulation_clock.CLOCK.business_delay(delay))
                if rule and rule.get('drop'): self.close_connection = True; return
                encoded = _text(response).encode('utf-8')
                self.send_response(int(rule.get('httpStatus', 200) if rule else 200))
                self.send_header('Content-Type', 'application/json; charset=utf-8')
                self.send_header('Content-Length', str(len(encoded)))
                self.end_headers(); self.wfile.write(encoded)
                if rule and rule.get('close'): self.close_connection = True
        server = ThreadingHTTPServer(('127.0.0.1', config['port']), HttpHandler)
    state.port = server.server_address[1]
    state.write_status()
    def serve():
        try: server.serve_forever(poll_interval=.2)
        except Exception as error: state.write_status(running=False, error=str(error))
    thread = threading.Thread(target=serve, daemon=True); thread.start()
    return dict(server=server, thread=thread, state=state, config=config)


def stop_protocol(runtime):
    if not runtime: return
    runtime['state'].stop_event.set(); runtime['server'].shutdown(); runtime['server'].server_close()
    if runtime.get('thread') and runtime['thread'].is_alive(): runtime['thread'].join(timeout=2)
    runtime['state'].write_status(running=False)
