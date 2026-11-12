"""Bounded keep-alive JSON requests for the owned local simulation backend."""
import http.client
import json
import queue
import threading
from urllib.error import HTTPError


class LoopbackJsonClient:
    def __init__(self, port, timeout=35, capacity=2):
        self.port, self.timeout = port, timeout
        self.slots = threading.BoundedSemaphore(capacity)
        self.connections = queue.LifoQueue(capacity)

    def request(self, path, data=None):
        if not path.startswith('/') or path.startswith('//'):
            raise ValueError('Backend path must be local and absolute.')
        if not self.slots.acquire(timeout=self.timeout):
            raise TimeoutError('本地仿真接口并发已满，等待连接超时。')
        connection = None
        try:
            try: connection = self.connections.get_nowait()
            except queue.Empty: connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=self.timeout)
            # Reads may reconnect once if an idle keep-alive socket was closed by
            # the server. Writes are NEVER replayed after an uncertain send.
            for attempt in range(2):
                try:
                    body = None if data is None else json.dumps(data, ensure_ascii=False).encode('utf-8')
                    connection.request('GET' if data is None else 'POST', path, body,
                                       {'Content-Type': 'application/json'})
                    response = connection.getresponse()
                    raw = response.read()
                    if response.status >= 400:
                        raise HTTPError(f'http://127.0.0.1:{self.port}{path}', response.status,
                                        response.reason, response.headers, None)
                    value = json.loads(raw)
                    if response.will_close:
                        connection.close(); connection = None
                    return value
                except (http.client.RemoteDisconnected, BrokenPipeError, ConnectionResetError):
                    connection.close()
                    if data is not None or attempt:
                        raise
                    connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=self.timeout)
        except BaseException:
            if connection is not None: connection.close(); connection = None
            raise
        finally:
            if connection is not None: self.connections.put_nowait(connection)
            self.slots.release()
