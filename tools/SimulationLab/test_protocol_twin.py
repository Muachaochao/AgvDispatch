"""Loopback protocol harness tests; no field endpoint or dispatch record is touched."""
import json
import socket
import tempfile
import unittest
import urllib.request
from pathlib import Path

from protocol_twin import start_protocol, stop_protocol, validate_protocol_config


class ProtocolTwinTests(unittest.TestCase):
    def config(self, transport='tcp', framing='line-json'):
        return dict(name='test-protocol', transport=transport, port=0, framing=framing, state=dict(count=0),
            rules=[
                dict(id='inc', match=dict(cmd='inc'), increment=dict(count=1),
                     response=dict(ok=True, count='{{counters.inc}}', value='{{state.count}}')),
                dict(id='drop', match=dict(cmd='drop'), drop=True, response={}),
                dict(id='http', match=dict(method='GET', path='/status'), response=dict(ready=True))
            ], defaultResponse=dict(error='unmatched'))

    def test_tcp_line_rules_template_state_and_real_port(self):
        with tempfile.TemporaryDirectory() as name:
            runtime = start_protocol(name, self.config())
            try:
                with socket.create_connection(('127.0.0.1', runtime['state'].port), timeout=3) as client:
                    reader = client.makefile()
                    client.sendall(b'{"cmd":"inc"}\n')
                    response = json.loads(reader.readline())
                    self.assertEqual(response, dict(ok=True, count=1, value=1))
                    client.sendall(b'{"cmd":"missing"}\n')
                    self.assertEqual(json.loads(reader.readline()), dict(error='unmatched'))
                status = json.loads((Path(name) / 'protocol-state.json').read_text(encoding='utf-8'))
                self.assertTrue(status['running']); self.assertEqual(status['requests'], 2); self.assertGreater(status['port'], 0)
            finally: stop_protocol(runtime)

    def test_udp_and_http_transports(self):
        with tempfile.TemporaryDirectory() as name:
            udp = start_protocol(name, self.config('udp'))
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                    client.settimeout(3); client.sendto(b'{"cmd":"inc"}', ('127.0.0.1', udp['state'].port))
                    self.assertEqual(json.loads(client.recvfrom(4096)[0])['value'], 1)
            finally: stop_protocol(udp)
        with tempfile.TemporaryDirectory() as name:
            http = start_protocol(name, self.config('http'))
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{http['state'].port}/status", timeout=3) as response:
                    self.assertEqual(json.load(response), dict(ready=True))
            finally: stop_protocol(http)

    def test_config_is_bounded_and_rejects_unknown_or_unsafe_fields(self):
        self.assertEqual(validate_protocol_config(self.config())['transport'], 'tcp')
        for value in [dict(self.config(), transport='serial'), dict(self.config(), port=5199),
                      dict(self.config(), rules=[]), dict(self.config(), unknown=True)]:
            with self.subTest(value=value), self.assertRaises(ValueError): validate_protocol_config(value)


if __name__ == '__main__': unittest.main()
