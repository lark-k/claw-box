import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
const root = resolve(import.meta.dirname, '../..');
const require = createRequire(join(root, 'src/Quantum.Harness.Upstream/package.json'));
let yaml;
try { yaml = require('js-yaml'); } catch { /* Submodule dependencies are optional in legacy-only CI. */ }
for (const format of ['json', 'yaml']) {
  test(`gateway update preserves ${format} profile settings and unrelated providers`, { skip: !yaml }, () => {
    const dir = mkdtempSync(join(tmpdir(), 'quantum-profile-'));
    try {
      const path = join(dir, 'cordis.patch.yml');
      const rows = [{ id: 'llm-pi-ai', config: { providers: { 'quantum-box': { baseURL: 'http://127.0.0.1:1/v1', apiKeyEnv: 'PRIVATE_REF', models: [{ id: 'model', input: ['text'] }] }, other: { baseURL: 'https://example.com/v1' } } } }, { id: 'settings', config: { locale: 'zh' } }];
      writeFileSync(path, format === 'json' ? JSON.stringify(rows) : yaml.dump(rows));
      const result = spawnSync(process.execPath, [join(root, 'scripts/dev/Update-HarnessProfile.mjs'), path, '19876'], { encoding: 'utf8' });
      assert.equal(result.status, 0, result.stderr);
      rows[0].config.providers['quantum-box'].baseURL = 'http://127.0.0.1:19876/v1';
      assert.deepEqual(yaml.load(readFileSync(path, 'utf8')), rows);
    } finally { rmSync(dir, { recursive: true }); }
  });
}
test('invalid profile is left unchanged', { skip: !yaml }, () => {
  const dir = mkdtempSync(join(tmpdir(), 'quantum-profile-'));
  try {
    const path = join(dir, 'cordis.patch.yml');
    writeFileSync(path, '- id: unrelated\n  config: {}\n');
    const before = readFileSync(path, 'utf8');
    const result = spawnSync(process.execPath, [join(root, 'scripts/dev/Update-HarnessProfile.mjs'), path, '19876']);
    assert.notEqual(result.status, 0);
    assert.equal(readFileSync(path, 'utf8'), before);
  } finally { rmSync(dir, { recursive: true }); }
});
