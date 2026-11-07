import http.client
import importlib.util
import json
from pathlib import Path
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

spec = importlib.util.spec_from_file_location('gateway_multi', Path(__file__).parents[2] / 'src/Quantum.ModelGateway/gateway.py')
gateway = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gateway)
CLIENT = 'client-test-token-not-production'
SERVER = 'server-test-token-not-production'


class Upstream(BaseHTTPRequestHandler):
    def log_message(self, *_args): pass

    def do_GET(self):
        self.server.auth = self.headers.get('Authorization')
        body = json.dumps({'data': [{'id': self.server.model}]}).encode()
        self.send_response(self.server.status)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        self.server.received = (self.headers.get('Authorization'), json.loads(self.rfile.read(int(self.headers['Content-Length']))))
        self.send_response(200)
        self.send_header('Content-Length', '2')
        self.end_headers()
        self.wfile.write(b'{}')


class MultiTests(unittest.TestCase):
    def setUp(self):
        self.servers = [ThreadingHTTPServer(('127.0.0.1', 0), Upstream) for _ in range(2)]
        for server in self.servers:
            server.model = 'same-model'
            server.status = 200
            threading.Thread(target=server.serve_forever, daemon=True).start()
        config = {'token': CLIENT, 'allowHttp': True, 'upstreams': [
            {'id': str(i), 'upstream': f'http://127.0.0.1:{s.server_port}/v1', 'upstreamToken': SERVER} for i, s in enumerate(self.servers)]}
        self.relay = gateway.Gateway(('127.0.0.1', 0), config)
        threading.Thread(target=self.relay.serve_forever, daemon=True).start()

    def tearDown(self):
        for server in [self.relay, *self.servers]:
            server.shutdown()
            server.server_close()

    def request(self, method, path, body=None):
        connection = http.client.HTTPConnection('127.0.0.1', self.relay.server_port, timeout=5)
        try:
            connection.request(method, path, body=body, headers={'Authorization': 'Bearer ' + CLIENT})
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_routing_disambiguates_identical_names_and_preserves_multimodal_content(self):
        status, catalog = self.request('GET', '/v1/models')
        self.assertEqual(status, 200)
        self.assertEqual([item['id'] for item in catalog['data']], ['0::same-model', '1::same-model'])
        body = {'model': '1::same-model', 'messages': [{'content': [{'type': 'image_url', 'image_url': {'url': 'data:image/png;base64,AA=='}}]}], 'tools': [{'type': 'function'}]}
        status, _ = self.request('POST', '/v1/chat/completions', json.dumps(body).encode())
        self.assertEqual(status, 200)
        auth, received = self.servers[1].received
        self.assertEqual(auth, 'Bearer ' + SERVER)
        self.assertEqual(received, {**body, 'model': 'same-model'})
        self.assertFalse(hasattr(self.servers[0], 'received'))

    def test_offline_server_and_replaced_model_are_reflected(self):
        self.servers[1].status = 502
        _, result = self.request('GET', '/v1/models')
        self.assertEqual([item['id'] for item in result['data']], ['0::same-model'])
        self.assertEqual(self.request('POST', '/v1/chat/completions', b'{"model":"1::same-model"}')[0], 404)
        self.servers[1].status = 200
        self.servers[0].model = 'replacement'
        self.relay.catalog_at = 0
        _, result = self.request('GET', '/v1/models')
        self.assertEqual([item['id'] for item in result['data']], ['0::replacement', '1::same-model'])
        self.assertEqual(self.request('POST', '/v1/chat/completions', b'{"model":"0::same-model"}')[0], 404)


if __name__ == '__main__': unittest.main()
