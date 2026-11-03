/** Preserve upstream YAML settings while updating the owned test gateway address. */
import { readFileSync, writeFileSync, renameSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(new URL('../../src/Quantum.Harness.Upstream/package.json', import.meta.url));
const yaml = require('js-yaml');
const [path, portText] = process.argv.slice(2);
const port = Number(portText);
if (!path || !Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Expected profile path and gateway port');
const rows = yaml.load(readFileSync(path, 'utf8'));
if (!Array.isArray(rows)) throw new Error('Profile must contain a patch list');
const provider = rows.find(row => row.id === 'llm-pi-ai')?.config?.providers?.['quantum-box'];
if (!provider) throw new Error('Profile has no Quantum Box provider; settings were left unchanged');
provider.baseURL = `http://127.0.0.1:${port}/v1`;
const temporary = `${path}.quantum-tmp-${process.pid}`;
writeFileSync(temporary, yaml.dump(rows, { lineWidth: 120, noRefs: true }), { mode: 0o600 });
renameSync(temporary, path);
