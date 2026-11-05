"""Real-model smoke: streaming function call and tool result round trip; no files or shell tools."""
import argparse
import http.client
import importlib.util
import json
from pathlib import Path
import threading
from types import SimpleNamespace

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--config', required=True)
parser.add_argument('--existing-port', type=int, help='Test an already running loopback gateway')
args = parser.parse_args()
config = json.loads(Path(args.config).read_text(encoding='utf-8-sig'))


def completion(server, messages, tools):
    payload = dict(model=config['model'], messages=messages, tools=tools, stream=True,
                   max_tokens=2048, temperature=0, chat_template_kwargs={'enable_thinking': False})
    connection = http.client.HTTPConnection('127.0.0.1', server.server_port, timeout=300)
    try:
        connection.request('POST', '/v1/chat/completions', json.dumps(payload).encode(),
                           {'Authorization': 'Bearer ' + config['token'], 'Content-Type': 'application/json'})
        response = connection.getresponse()
        if response.status != 200:
            raise RuntimeError(f'Model smoke failed with HTTP {response.status}')
        content, calls, done = '', {}, False
        for line in response:
            if not line.startswith(b'data:'):
                continue
            data = line[5:].strip()
            if data == b'[DONE]':
                done = True
                break
            for choice in json.loads(data).get('choices', []):
                delta = choice.get('delta', {})
                content += delta.get('content') or ''
                for call in delta.get('tool_calls', []):
                    target = calls.setdefault(call['index'], {'id': '', 'type': 'function', 'function': {'name': '', 'arguments': ''}})
                    target['id'] += call.get('id', '')
                    for key in ('name', 'arguments'):
                        target['function'][key] += call.get('function', {}).get(key, '')
        if not done:
            raise RuntimeError('Stream ended without DONE')
        return content, list(calls.values())
    finally:
        connection.close()


tools = [{'type': 'function', 'function': {'name': 'quantum_probe', 'description': 'Return the sum of a and b.',
         'parameters': {'type': 'object', 'properties': {'a': {'type': 'integer'}, 'b': {'type': 'integer'}}, 'required': ['a', 'b']}}}]
messages = [{'role': 'user', 'content': 'You must call quantum_probe with a=19 and b=23, then report its returned result.'}]
def smoke(server):
        text, calls = completion(server, messages, tools)
        if len(calls) != 1 or calls[0]['function']['name'] != 'quantum_probe':
            raise RuntimeError('Model did not produce the expected tool call')
        values = json.loads(calls[0]['function']['arguments'])
        if values != {'a': 19, 'b': 23}:
            raise RuntimeError('Model produced unexpected tool arguments')
        messages.extend([{'role': 'assistant', 'content': text or None, 'tool_calls': calls},
                         {'role': 'tool', 'tool_call_id': calls[0]['id'], 'content': '{"result":42}'}])
        text, calls = completion(server, messages, tools)
        if '42' not in text or calls:
            raise RuntimeError('Model did not finish using the supplied tool result')
        print('PASS: real model SSE, tool-call JSON, tool result round trip (42). No credentials logged.')


if args.existing_port:
    smoke(SimpleNamespace(server_port=args.existing_port))
else:
    spec = importlib.util.spec_from_file_location('gateway', Path(__file__).parents[2] / 'src/Quantum.ModelGateway/gateway.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    with module.Gateway(('127.0.0.1', 0), config) as server:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            smoke(server)
        finally:
            server.shutdown()
