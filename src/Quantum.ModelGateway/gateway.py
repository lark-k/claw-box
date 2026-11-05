"""Loopback model relay for the Quantum box; no agent or tool execution."""

import argparse
import hmac
import http.client
import json
from pathlib import Path
import re
import socket
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit


class Gateway(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, config):
        upstream = urlsplit(config['upstream'])
        if (upstream.scheme not in ('https', 'http') or not upstream.hostname
                or upstream.username or upstream.password or upstream.query or upstream.fragment
                or upstream.path.rstrip('/') != '/v1'):
            raise ValueError('upstream must be an HTTP(S) /v1 URL without credentials or query')
        if upstream.scheme == 'http' and not config.get('allowHttp', False):
            raise ValueError('HTTP upstream requires explicit allowHttp for development')
        for key in ('token', 'upstreamToken'):
            if not isinstance(config.get(key), str) or len(config[key]) < 16 or any(c.isspace() for c in config[key]):
                raise ValueError(f'{key} must be a nonempty credential without whitespace')
        if address[0] != '127.0.0.1':
            raise ValueError('gateway must bind to 127.0.0.1; use authenticated SSH forwarding')
        interfaces = config.get('upstreamInterfaces', [])
        if (not isinstance(interfaces, list) or len(interfaces) > 8 or
                any(not isinstance(i, str) or not re.fullmatch(r'[A-Za-z0-9_.-]{1,15}', i) for i in interfaces)):
            raise ValueError('upstreamInterfaces must contain valid interface names')
        if interfaces and not hasattr(socket, 'SO_BINDTODEVICE'):
            raise ValueError('upstreamInterfaces require Linux SO_BINDTODEVICE')
        clients = config.get('clientTokensFile')
        if clients is not None and (not isinstance(clients, str) or not Path(clients).is_absolute()):
            raise ValueError('clientTokensFile must be an absolute path')
        self.config = config
        self.upstream = upstream
        super().__init__(address, Handler)


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *_args):
        pass  # Request bodies, URLs and credentials never enter access logs.

    def error(self, status, code):
        self.close_connection = True
        body = json.dumps({'error': {'code': code, 'message': code}}).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self.relay()

    def do_POST(self):
        self.relay()

    def relay(self):
        if self.headers.get('Origin') or not authorized(self.server.config, self.headers.get('Authorization', '')):
            self.error(401, 'unauthorized')
            return
        if (self.command, self.path) not in {('GET', '/v1/models'), ('POST', '/v1/chat/completions')}:
            self.error(404, 'unsupported_endpoint')
            return
        if self.headers.get('Transfer-Encoding') or len(self.headers.get_all('Content-Length', [])) > 1:
            self.error(400, 'invalid_framing')
            return
        try:
            size = int(self.headers.get('Content-Length', '0'))
        except ValueError:
            self.error(400, 'invalid_length')
            return
        if not 0 <= size <= 32 * 1024 * 1024:
            self.error(413, 'request_too_large')
            return
        self.connection.settimeout(300)
        try:
            body = self.rfile.read(size)
        except (OSError, TimeoutError):
            self.error(408, 'request_timeout')
            return
        if len(body) != size:
            self.error(400, 'incomplete_request')
            return
        endpoint = self.server.upstream
        connection_type = http.client.HTTPSConnection if endpoint.scheme == 'https' else http.client.HTTPConnection
        upstream = connection_type(endpoint.hostname, endpoint.port, timeout=300)
        if self.server.config.get('upstreamInterfaces'):
            upstream._create_connection = lambda address, timeout, source_address=None: connect_interfaces(
                address, self.server.config['upstreamInterfaces'], timeout)
        started = False
        try:
            # Fixed target and routes; no client headers, redirects or automatic retries.
            upstream.request(self.command, self.path, body=body, headers={
                'Authorization': 'Bearer ' + self.server.config['upstreamToken'],
                'Content-Type': 'application/json', 'Accept-Encoding': 'identity',
            })
            response = upstream.getresponse()
            if response.status != 200:
                # Do not expose upstream diagnostics that could contain secrets.
                self.error(response.status if 400 <= response.status < 600 else 502, 'model_service_error')
                return
            self.send_response(200)
            self.send_header('Content-Type', response.getheader('Content-Type', 'application/json'))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Connection', 'close')
            self.end_headers()
            started = True
            self.close_connection = True
            while chunk := response.read1(65536):
                self.wfile.write(chunk)
                self.wfile.flush()
        except (OSError, http.client.HTTPException, socket.timeout):
            if not started:
                self.error(502, 'model_service_unavailable')
            # After headers, close the stream without synthesizing a success/DONE.
        finally:
            upstream.close()


def authorized(config, header):
    candidate = header.encode()
    if hmac.compare_digest(candidate, ('Bearer ' + config['token']).encode()):
        return True
    path = config.get('clientTokensFile')
    if not path:
        return False
    try:
        source = Path(path)
        if source.is_symlink() or source.stat().st_size > 256 * 1024:
            return False
        clients = json.loads(source.read_text(encoding='utf-8'))
        if not isinstance(clients, dict) or len(clients) > 1000:
            return False
        return any(isinstance(value, str) and 32 <= len(value) <= 128 and
                   hmac.compare_digest(candidate, ('Bearer ' + value).encode()) for value in clients.values())
    except (OSError, ValueError, TypeError):
        return False


def connect_interfaces(address, interfaces, timeout):
    """Try each interface only while connecting; never replay an HTTP request."""
    last_error = None
    for interface in interfaces:
        for family, socktype, proto, _canonname, target in socket.getaddrinfo(
                address[0], address[1], type=socket.SOCK_STREAM):
            connection = socket.socket(family, socktype, proto)
            try:
                connection.settimeout(min(timeout, 5))
                connection.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, interface.encode() + b'\0')
                connection.connect(target)
                connection.settimeout(timeout)
                return connection
            except OSError as error:
                last_error = error
                connection.close()
    raise last_error or OSError('No upstream address available')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, help='Private JSON config outside the repository')
    parser.add_argument('--port', type=int, default=18792)
    args = parser.parse_args()
    with open(args.config, encoding='utf-8-sig') as source:
        config = json.load(source)
    with Gateway(('127.0.0.1', args.port), config) as server:
        print(f'Quantum model gateway listening on 127.0.0.1:{server.server_port}', flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == '__main__':
    main()
