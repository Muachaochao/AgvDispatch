import http.client
import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import Mock, patch

from loopback_http import LoopbackJsonClient


class ClientTests(unittest.TestCase):
    def test_many_queries_reuse_one_socket_and_parse_utf8(self):
        clients = set()
        class Handler(BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'
            def do_GET(self):
                clients.add(self.client_address)
                body = json.dumps(dict(message='任务运行'),ensure_ascii=False).encode('utf-8')
                self.send_response(200); self.send_header('Content-Length',str(len(body)))
                self.end_headers(); self.wfile.write(body)
            def log_message(self,*args): pass
        server = ThreadingHTTPServer(('127.0.0.1',0),Handler)
        worker = threading.Thread(target=server.serve_forever,daemon=True);worker.start()
        client = LoopbackJsonClient(server.server_port)
        try:
            for _ in range(40): self.assertEqual(client.request('/api/tasks')['message'],'任务运行')
            self.assertEqual(len(clients),1)
        finally:
            while not client.connections.empty(): client.connections.get_nowait().close()
            server.shutdown();server.server_close();worker.join()

    def test_uncertain_write_is_never_replayed(self):
        connection = Mock();connection.getresponse.side_effect = http.client.RemoteDisconnected()
        with patch('loopback_http.http.client.HTTPConnection',return_value=connection) as factory:
            client = LoopbackJsonClient(10001)
            with self.assertRaises(http.client.RemoteDisconnected): client.request('/api/create',dict(requestId='original'))
            self.assertEqual(connection.request.call_count,1);self.assertEqual(factory.call_count,1)
            self.assertTrue(client.connections.empty());connection.close.assert_called()

    def test_closed_idle_connection_retries_only_read(self):
        first,second = Mock(),Mock();first.getresponse.side_effect=http.client.RemoteDisconnected()
        response=second.getresponse.return_value;response.read.return_value=b'{"ok":true}'
        response.status=200;response.will_close=False
        with patch('loopback_http.http.client.HTTPConnection',side_effect=[first,second]):
            client=LoopbackJsonClient(10001);self.assertTrue(client.request('/api/tasks')['ok'])
        first.close.assert_called();second.request.assert_called_once()

    def test_concurrency_is_bounded(self):
        client=LoopbackJsonClient(10001,timeout=.01,capacity=1)
        client.slots.acquire()
        try:
            with self.assertRaises(TimeoutError): client.request('/api/tasks')
        finally: client.slots.release()


if __name__=='__main__':unittest.main()
