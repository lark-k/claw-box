import { randomUUID } from 'node:crypto';
import { readFileSync, statSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const terminalTasks = new Set(['succeeded', 'failed', 'canceled', 'interrupted']);
class ExecutorHttpError extends Error {
  constructor(status, code) { super(`executor_http_${status}`); this.status = status; this.code = code; }
}
const safeCodes = new Set(['invalid_arguments', 'unknown_tool', 'path_denied', 'input_too_large', 'file_conflict', 'queue_full', 'request_conflict', 'request_not_found']);
const guidance = {
  directory_missing: 'The parent directory does not exist. Discover and request the directory creation tool before writing a new file.',
  file_conflict: 'File changed or destination exists. Read current file/hash first; do not overwrite an unrelated destination.',
  search_not_found: 'No literal match; file unchanged. Read the current content before proposing another edit.',
  paragraph_not_found: 'No exact matching paragraph; document unchanged. Read the current paragraph text first.',
  unsupported_text_encoding: 'The file is not supported UTF-8 text. Use Word tools for DOCX; do not overwrite binary files with text.',
  project_runtime_missing: 'The matching local SDK/runtime is not configured. Ask the human to configure it; never bypass isolation with shell execution.',
  project_lock_missing: 'npm ci needs package-lock.json. Obtain a fresh snapshot and request npm.install first.',
  dependency_source_unsupported: 'This installer accepts npm registry dependencies only, without workspaces, overrides, Git/file/URL dependencies or links. Do not bypass it with shell execution.',
};

export function loadConfiguration(config) {
  const port = config.port ?? 18791;
  const timeoutSeconds = config.timeoutSeconds ?? 360;
  if (!Number.isInteger(port) || port < 1024 || port > 65535 ||
      !Number.isInteger(timeoutSeconds) || timeoutSeconds < 10 || timeoutSeconds > 600 ||
      typeof config.workerTokenFile !== 'string' || !isAbsolute(config.workerTokenFile)) {
    throw new Error('Invalid ClawBox connection configuration.');
  }
  const info = statSync(config.workerTokenFile);
  if (!info.isFile() || info.size > 1024 || (process.platform !== 'win32' && (info.mode & 0o077) !== 0)) {
    throw new Error('Worker credential must be a private, small regular file (chmod 600).');
  }
  const token = readFileSync(config.workerTokenFile, 'utf8').trim();
  if (!/^[A-Fa-f0-9]{64}$/.test(token)) throw new Error('Invalid worker credential.');
  return { port, timeoutSeconds, token };
}

export class ExecutorClient {
  constructor(config, transport = fetch, pollMilliseconds = 500) {
    this.config = config;
    this.transport = transport;
    this.pollMilliseconds = pollMilliseconds;
  }

  async request(method, route, body, signal) {
    // Fixed loopback authority: a model cannot choose a destination or a credential.
    const response = await this.transport(`http://127.0.0.1:${this.config.port}/api/v1${route}`, {
      method, redirect: 'error',
      headers: { Authorization: `Bearer ${this.config.token}`, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.any([signal ?? new AbortController().signal, AbortSignal.timeout(10_000)]),
    });
    if (!response.ok) {
      // Never relay a server response body: it may contain content or deployment details.
      let code;
      if (response.status >= 400 && response.status < 500) {
        try { const body = await response.json(); if (safeCodes.has(body.code)) code = body.code; } catch { /* No public error code. */ }
      }
      throw new ExecutorHttpError(response.status, code);
    }
    const bytes = await response.arrayBuffer();
    if (bytes.byteLength > 32 * 1024 * 1024) throw new Error('executor_response_too_large');
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  }

  async execute(tool, args, signal, onUpdate) {
    const requestId = randomUUID();
    const lifetime = AbortSignal.any([signal ?? new AbortController().signal,
      AbortSignal.timeout(this.config.timeoutSeconds * 1000)]);
    const route = `/requests/${requestId}`;
    let request;
    try {
      request = await this.request('POST', '/requests', { requestId, tool, arguments: args }, lifetime);
      onUpdate?.({ content: [{ type: 'text', text: `Waiting for Windows approval: ${requestId}` }], details: { requestId } });
      while (true) {
        if (request.status !== 'pending' && request.status !== 'approved') {
          return result({ requestId, status: request.status,
            message: 'Windows did not approve this request. Stop this operation. Do not resubmit it unless the human provides new instructions.' }, true);
        }
        if (request.task && terminalTasks.has(request.task.status)) {
          return result({ requestId, taskId: request.task.id, status: request.task.status,
            result: request.task.result, errorCode: request.task.errorCode,
            message: request.task.status === 'failed' ? guidance[request.task.errorCode] ?? 'Tool execution failed. Check its error code; do not claim success.' : undefined }, request.task.status !== 'succeeded');
        }
        await delay(this.pollMilliseconds, undefined, { signal: lifetime });
        request = await this.request('GET', route, undefined, lifetime);
      }
    } catch (error) {
      // A completed initial 4xx response (except timeout) means nothing was admitted.
      // A later polling error or transport loss still has an unknown outcome.
      if (request === undefined && error instanceof ExecutorHttpError && error.status >= 400 && error.status < 500 && error.status !== 408) {
        return result({ requestId, status: 'not_submitted', httpStatus: error.status,
          errorCode: error.code ?? `executor_http_${error.status}`,
          message: 'Executor rejected this request before approval or execution. Check the tool schema, workspace and arguments. No file change occurred; do not claim success.' }, true);
      }
      // Cancellation is best effort. A completed write remains committed. Never POST the tool again.
      try { await this.request('POST', `${route}/cancel`, {}, AbortSignal.timeout(2000)); } catch { /* Tunnel may be down. */ }
      return result({ requestId, status: 'outcome_unknown',
        message: 'Connection lost, canceled, or timed out. Check this request ID on Windows. Do not automatically repeat a write.' }, true);
    }
  }
}

function result(details, isError) {
  return { content: [{ type: 'text', text: JSON.stringify(details) }], details, isError };
}
