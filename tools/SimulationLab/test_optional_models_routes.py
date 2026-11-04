"""End-to-end controller routes for optional protocol and equipment models."""
import json
import socket
import tempfile
import threading
import time
import unittest
import urllib.request
from pathlib import Path
from unittest.mock import patch

import controller


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


class OptionalModelRouteTests(unittest.TestCase):
    def test_controller_routes_start_exercise_and_stop_optional_models(self):
        with tempfile.TemporaryDirectory() as name:
            workspace = Path(name)
            root = workspace / 'artifacts/web-single-vehicle-optional-routes'
            (root / 'service').mkdir(parents=True)
            (root / 'webhost').mkdir()
            (root / 'data').mkdir()
            ports = [free_port() for _ in range(4)]
            while len(set(ports)) != len(ports):
                ports = [free_port() for _ in range(4)]
            web_port, plc_port, robot_port, navigation_port = ports
            lab = dict(simulationOnly=True, pipe='Agv28StageLab-optional-routes', webPort=web_port)
            settings = dict(
                HandshakeExecution=dict(PlcContractVersion='plc-28-stage-v2'),
                Database=dict(Path=str(root / 'data/agv-dispatch.db')),
                Security=dict(PipeName=lab['pipe']),
                Plc=dict(IpAddress='127.0.0.1', Port=plc_port),
                Agv=dict(Robots=[dict(VehicleId='car', IpAddress='127.0.0.1',
                                      Port=robot_port, NavigationPort=navigation_port)]),
                TaskCreation=dict(AutomaticCreationEnabled=False))
            web = dict(Port=web_port, PipeName=lab['pipe'],
                       ProjectStorePath=str(root / 'projects'),
                       EngineeringStorePath=str(root / 'engineering'))
            for path, value in [
                (root / 'lab.json', lab),
                (root / 'service/appsettings.json', settings),
                (root / 'webhost/appsettings.json', {'Web': web}),
                (root / 'navigation.agvmap.json', dict(nodes=[], edges=[], resources=[])),
            ]:
                path.write_text(json.dumps(value), encoding='utf-8')

            token = 'a' * 64
            port = free_port()
            server = threading.Thread(target=controller.serve, args=(root, port, token), daemon=True)
            with patch.object(controller, 'WORKSPACE', workspace):
                server.start()
                self.addCleanup(lambda: server.join(timeout=3))

                def request(path, payload=None):
                    data = json.dumps(payload).encode() if payload is not None else None
                    req = urllib.request.Request(
                        f'http://127.0.0.1:{port}{path}', data=data,
                        headers={'Content-Type': 'application/json', 'X-Simulation-Token': token})
                    with urllib.request.urlopen(req, timeout=5) as response:
                        return json.load(response)

                deadline = time.monotonic() + 5
                while True:
                    try:
                        request('/status')
                        break
                    except (OSError, urllib.error.URLError):
                        if time.monotonic() >= deadline:
                            raise
                        time.sleep(.05)

                protocol = request('/protocol-start', dict(
                    name='route-protocol', transport='tcp', port=0, framing='line-json',
                    state=dict(ready=True),
                    rules=[dict(id='ping', match=dict(command='ping'),
                                response=dict(ok=True, count='{{counters.ping}}'))],
                    defaultResponse=dict(error='unmatched')))['protocol']
                self.assertTrue(protocol['running'])
                with socket.create_connection(('127.0.0.1', protocol['port']), timeout=3) as client:
                    reader = client.makefile()
                    client.sendall(b'{"command":"ping"}\n')
                    self.assertEqual(json.loads(reader.readline()), dict(ok=True, count=1))
                self.assertFalse(request('/protocol-stop', {})['protocol']['running'])

                status = request('/equipment-start', dict(
                    sequence='start', travelSeconds=.2, doorSeconds=.2))['equipment']
                self.assertTrue(status['running'])
                request('/equipment-control', dict(
                    sequence='move', elevator=dict(targetFloor='2'), signals={}))
                deadline = time.monotonic() + 3
                while time.monotonic() < deadline:
                    status = request('/status')['equipment']
                    if status['currentFloor'] == '2':
                        break
                    time.sleep(.05)
                self.assertEqual(status['currentFloor'], '2')

                request('/equipment-control', dict(
                    sequence='open', elevator=dict(doorCommand='open'), signals={}))
                deadline = time.monotonic() + 2
                while time.monotonic() < deadline:
                    status = request('/status')['equipment']
                    if status['door'] == 'Open':
                        break
                    time.sleep(.05)
                self.assertEqual(status['door'], 'Open')

                request('/equipment-control', dict(
                    sequence='signals', elevator={},
                    signals=dict(set=dict(call=False),
                                 schedule=[dict(id='call', afterMs=60, set=dict(call=True))])))
                deadline = time.monotonic() + 2
                while time.monotonic() < deadline:
                    status = request('/status')['equipment']
                    if status['signals'].get('call'):
                        break
                    time.sleep(.03)
                self.assertTrue(status['signals']['call'])
                self.assertFalse(request('/equipment-stop', {})['equipment']['running'])

                request('/shutdown', {})
                server.join(timeout=3)
                self.assertFalse(server.is_alive())


if __name__ == '__main__':
    unittest.main()
