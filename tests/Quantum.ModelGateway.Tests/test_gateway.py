import importlib.util
import http.client
import json
from pathlib import Path
import threading
import unittest
import os
import subprocess
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

spec = importlib.util.spec_from_file_location('gateway', Path(__file__).parents[2] / 'src/Quantum.ModelGateway/gateway.py')
gateway = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gateway)
TOKEN = 'client-test-token-not-production'
SERVER_TOKEN = 'server-test-token-not-production'


class Model(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def do_GET(self):
        self.server.received_auth = self.headers.get('Authorization')
        body = b'{"data":[{"id":"test-model"}]}'
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        self.server.received = (self.headers.get('Authorization'), self.rfile.read(int(self.headers['Content-Length'])))
        self.send_response(self.server.status)
        self.send_header('Content-Type', 'text/event-stream')
        self.end_headers()
        self.wfile.write(b'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]}}]}\n\n')
        self.wfile.flush()
        self.server.release.wait(2)
        self.wfile.write(b'data: [DONE]\n\n')


class Tests(unittest.TestCase):
    def setUp(self):
        self.model = ThreadingHTTPServer(('127.0.0.1', 0), Model)
        self.model.status = 200
        self.model.release = threading.Event()
        self.config = dict(upstream=f'http://127.0.0.1:{self.model.server_port}/v1', upstreamToken=SERVER_TOKEN, token=TOKEN, allowHttp=True)
        self.relay = gateway.Gateway(('127.0.0.1', 0), self.config)
        for server in (self.model, self.relay):
            threading.Thread(target=server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.model.release.set()
        for server in (self.relay, self.model):
            server.shutdown()
            server.server_close()

    def request(self, path='/v1/chat/completions', token=TOKEN, extra=None, body=b'{}'):
        connection = http.client.HTTPConnection('127.0.0.1', self.relay.server_port, timeout=3)
        self.addCleanup(connection.close)
        headers = {'Authorization': 'Bearer ' + token, **(extra or {})}
        connection.request('POST', path, body=body, headers=headers)
        return connection.getresponse()

    def test_stream_is_flushed_before_completion_and_preserves_tools_images(self):
        body = json.dumps({'messages': [{'role': 'user', 'content': [{'type': 'image_url', 'image_url': {'url': 'data:image/png;base64,AA=='}}]}], 'tools': [{'type': 'function', 'function': {'name': 'read'}}]}).encode()
        response = self.request(body=body)
        self.assertEqual(response.status, 200)
        self.assertIn(b'tool_calls', response.readline())
        self.assertFalse(self.model.release.is_set())
        self.assertEqual(self.model.received, ('Bearer ' + SERVER_TOKEN, body))
        self.model.release.set()
        self.assertIn(b'[DONE]', response.read())

    def test_wrong_credential_does_not_reach_model(self):
        self.assertEqual(self.request(token='wrong').status, 401)
        self.assertFalse(hasattr(self.model, 'received'))

    def test_browser_origins_are_rejected(self):
        self.assertEqual(self.request(extra={'Origin': 'https://example.com'}).status, 401)

    def test_arbitrary_routes_and_queries_are_rejected(self):
        for path in ('/v1/chat/completions?target=other', '/v1/files', 'http://example.com/v1/models'):
            self.assertEqual(self.request(path).status, 404)

    def test_upstream_error_is_redacted(self):
        self.model.release.set()
        self.model.status = 401
        response = self.request()
        self.assertEqual(response.status, 401)
        self.assertNotIn(b'tool_calls', response.read())

    def test_chunked_request_is_rejected(self):
        self.assertEqual(self.request(extra={'Transfer-Encoding': 'chunked'}).status, 400)

    def test_loopback_and_tls_configuration_are_enforced(self):
        with self.assertRaises(ValueError):
            gateway.Gateway(('0.0.0.0', 0), self.config)
        with self.assertRaises(ValueError):
            gateway.Gateway(('127.0.0.1', 0), {**self.config, 'allowHttp': False})

    def test_model_discovery_uses_server_credential(self):
        connection = http.client.HTTPConnection('127.0.0.1', self.relay.server_port, timeout=3)
        self.addCleanup(connection.close)
        connection.request('GET', '/v1/models', headers={'Authorization': 'Bearer ' + TOKEN})
        response = connection.getresponse()
        self.assertEqual(response.status, 200)
        self.assertEqual(json.loads(response.read())['data'][0]['id'], 'test-model')
        self.assertEqual(self.model.received_auth, 'Bearer ' + SERVER_TOKEN)

    def test_large_body_is_rejected_before_reading(self):
        self.assertEqual(self.request(extra={'Content-Length': str(33 * 1024 * 1024)}).status, 413)


@unittest.skipUnless(os.name == 'nt', 'Windows launcher configuration')
class LauncherTests(unittest.TestCase):
    def test_generated_profiles_keep_arrays_default_route_and_no_literal_credential(self):
        repo = Path(__file__).parents[2]
        if not (repo / 'src/Quantum.Harness.Upstream/package.json').exists():
            self.skipTest('Pinned upstream submodule is not initialized')
        with tempfile.TemporaryDirectory() as state:
            env = {**os.environ, 'LOCALAPPDATA': state, 'QUANTUM_GATEWAY_TOKEN': TOKEN}
            command = ['pwsh', '-NoProfile', '-File', str(repo / 'scripts/dev/Start-HarnessNext.ps1'),
                       '-Model', 'test-model', '-ConfigureOnly']
            subprocess.run(command, env=env, check=True, capture_output=True)
            path = Path(state) / 'QuantumHarnessNext/harness/profiles/web/cordis.patch.yml'
            data = path.read_text(encoding='utf-8-sig')
            rows = json.loads(data)
            self.assertIsInstance(rows, list)
            provider = rows[0]['config']['providers']['quantum-box']
            self.assertEqual(provider['models'][0]['input'], ['text'])
            self.assertEqual(rows[1]['config'], {'provider': 'quantum-box', 'model': 'test-model'})
            self.assertNotIn(TOKEN, data)
            path.write_text('[]\n', encoding='utf-8')
            subprocess.run(command, env=env, check=True, capture_output=True)
            self.assertEqual(path.read_text(), '[]\n')
            subprocess.run(command + ['-Surface', 'Desktop', '-ImageInput', '-DisableThinking'], env=env, check=True, capture_output=True)
            desktop = json.loads((path.parent.parent / 'desktop/cordis.patch.yml').read_text(encoding='utf-8-sig'))
            provider = desktop[0]['config']['providers']['quantum-box']
            self.assertEqual(provider['models'][0]['input'], ['text', 'image'])
            self.assertFalse(provider['compat']['chatTemplateKwargs']['enable_thinking'])


if __name__ == '__main__':
    unittest.main()
