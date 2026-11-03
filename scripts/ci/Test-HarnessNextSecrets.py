"""Check materialized release payloads for the developer's private model credentials."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--config', type=Path, required=True)
parser.add_argument('--application', type=Path, required=True)
args = parser.parse_args()
config = json.loads(args.config.read_text(encoding='utf-8-sig'))
secrets = [config[key].encode() for key in ('upstreamToken', 'token') if config.get(key)]
if not secrets:
    raise SystemExit('Private credential configuration is incomplete; scan was not performed.')
overlap = max(map(len, secrets)) - 1
count = 0
for path in args.application.rglob('*'):
    if not path.is_file():
        continue
    tail = b''
    with path.open('rb') as stream:
        while chunk := stream.read(1024 * 1024):
            body = tail + chunk
            if any(secret in body for secret in secrets):
                raise SystemExit('Release payload contains a private development credential; distribution blocked.')
            tail = body[-overlap:] if overlap else b''
    count += 1
print(f'Private credential exclusion passed: {count} application files scanned.')
