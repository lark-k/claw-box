import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, chmodSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { ExecutorClient, loadConfiguration } from '../../src/ClawBox.OpenClawPlugin/client.js';
import plugin from '../../src/ClawBox.OpenClawPlugin/index.js';

const config = { port: 18791, token: 'a'.repeat(64), timeoutSeconds: 10 };
function mockTransport(replies) {
  const calls = [];
  return { calls, fetch: async (url, options) => {
    calls.push({ url, ...options });
    const reply = replies.shift();
    if (reply instanceof Error) throw reply;
    if (!reply) throw new Error('Unexpected call.');
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? 200 });
  } };
}

test('waits for approval and forwards real result; no admin credential or duplicate POST', async () => {
  const mock = mockTransport([
    { body: { status: 'pending' } },
    { body: { status: 'approved', task: { id: 'task', status: 'running' } } },
    { body: { status: 'approved', task: { id: 'task', status: 'succeeded', result: { content: '真实文本' } } } },
  ]);
  const answer = await new ExecutorClient(config, mock.fetch, 1).execute('file.read', { path: 'C:\\workspace\\a.txt' });
  assert.equal(answer.isError, false);
  assert.equal(answer.details.result.content, '真实文本');
  assert.equal(mock.calls.filter(c => c.method === 'POST').length, 1);
  assert.ok(mock.calls.every(c => c.url.startsWith('http://127.0.0.1:18791/api/v1/') && c.redirect === 'error'));
  assert.equal(mock.calls[0].headers.Authorization, `Bearer ${config.token}`);
  assert.equal(JSON.parse(mock.calls[0].body).tool, 'file.read');
});

for (const status of ['rejected', 'expired', 'canceled', 'interrupted']) {
  test(`${status} never submits another task`, async () => {
    const mock = mockTransport([{ body: { status } }]);
    const answer = await new ExecutorClient(config, mock.fetch, 1).execute('file.write', {});
    assert.equal(answer.isError, true);
    assert.equal(answer.details.status, status);
    assert.match(answer.details.message, /Do not resubmit/);
    assert.equal(mock.calls.length, 1);
  });
}

test('lost connection reports unknown request ID and only attempts cancellation', async () => {
  const mock = mockTransport([{ body: { status: 'pending' } }, new Error('private failure'), { body: {} }]);
  const answer = await new ExecutorClient(config, mock.fetch, 1).execute('word.create', {});
  assert.equal(answer.details.status, 'outcome_unknown');
  assert.equal(mock.calls[2].url.endsWith(`/${answer.details.requestId}/cancel`), true);
  assert.equal(mock.calls.filter(c => c.url.endsWith('/requests')).length, 1);
  assert.ok(!JSON.stringify(answer).includes('private failure'));
});

test('abort stops polling and attempts best effort cancellation', async () => {
  const controller = new AbortController();
  const mock = mockTransport([{ body: { status: 'pending' } }, { body: {} }]);
  const answer = await new ExecutorClient(config, mock.fetch, 1).execute('file.read', {}, controller.signal, () => controller.abort());
  assert.equal(answer.details.status, 'outcome_unknown');
  assert.equal(mock.calls.length, 2);
});

test('HTTP failures do not expose private response bodies', async () => {
  const mock = mockTransport([{ status: 401, body: { secret: 'DO_NOT_RELAY' } }, { status: 404, body: {} }]);
  const answer = await new ExecutorClient(config, mock.fetch, 1).execute('file.read', {});
  assert.ok(!JSON.stringify(answer).includes('DO_NOT_RELAY'));
  assert.equal(answer.isError, true);
  assert.equal(answer.details.status, 'not_submitted');
  assert.equal(mock.calls.length, 1);
});

test('initial validation rejection exposes only a known public code and cannot execute', async () => {
  const mock = mockTransport([{ status: 422, body: { code: 'invalid_arguments', detail: 'PRIVATE_CONTENT' } }]);
  const answer = await new ExecutorClient(config, mock.fetch, 1).execute('word.format', {});
  assert.equal(answer.details.errorCode, 'invalid_arguments');
  assert.equal(answer.details.status, 'not_submitted');
  assert.equal(mock.calls.length, 1);
  assert.ok(!JSON.stringify(answer).includes('PRIVATE_CONTENT'));
});

test('failure after admission still reports unknown outcome and attempts cancellation', async () => {
  const mock = mockTransport([{ body: { status: 'pending' } }, { status: 404, body: { code: 'request_not_found' } }, { body: {} }]);
  const answer = await new ExecutorClient(config, mock.fetch, 1).execute('directory.create', {});
  assert.equal(answer.details.status, 'outcome_unknown');
  assert.equal(mock.calls.length, 3);
});

test('configuration is loopback only; sixteen optional schemas match the executor argument contract', () => {
  const root = mkdtempSync(join(tmpdir(), 'clawbox-plugin-'));
  try {
    const workerTokenFile = join(root, 'worker-token');
    writeFileSync(workerTokenFile, config.token, { mode: 0o600 });
    chmodSync(workerTokenFile, 0o600);
    assert.equal(loadConfiguration({ workerTokenFile }).port, 18791);
    assert.throws(() => loadConfiguration({ workerTokenFile, port: 80 }));
    assert.throws(() => loadConfiguration({ workerTokenFile: 'relative' }));
    const registered = [];
    plugin.register({ pluginConfig: { workerTokenFile }, registerTool: (tool, options) => registered.push({ tool, options }) });
    assert.equal(registered.length, 16);
    assert.ok(registered.every(t => t.options.optional && !t.tool.parameters.additionalProperties));
    assert.deepEqual(registered.map(t => t.tool.name), ['windows_project_command', 'windows_file_read', 'windows_project_list', 'windows_project_check', 'windows_file_write', 'windows_word_read', 'windows_word_create', 'windows_word_replace', 'windows_directory_create', 'windows_directory_list', 'windows_file_info', 'windows_file_search', 'windows_file_copy', 'windows_file_move', 'windows_file_replace', 'windows_word_format']);
    const schema = name => registered.find(t => t.tool.name === name).tool.parameters;
    assert.deepEqual(schema('windows_word_replace').required, ['path', 'search', 'replacement']);
    assert.deepEqual(schema('windows_word_format').required, ['path', 'expectedSha256', 'scope']);
    assert.deepEqual(schema('windows_project_check').required, ['path', 'kind', 'expectedSnapshotSha256']);
    assert.deepEqual(schema('windows_project_command').required, ['path', 'operation', 'expectedSnapshotSha256']);
    writeFileSync(workerTokenFile, 'not-a-key');
    assert.throws(() => loadConfiguration({ workerTokenFile }));
  } finally { rmSync(root, { recursive: true }); }
});
