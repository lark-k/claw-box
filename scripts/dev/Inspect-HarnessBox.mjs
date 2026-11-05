/** Inspect only the owned Quantum preview; never select another Electron application. */
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
const port = process.argv[2] ?? '9222';
if (!/^\d+$/u.test(port)) throw new Error('Expected a loopback development port');
const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
const target = targets.find(row => row.type === 'page' && row.url === 'dsh-app://app/' && row.title.includes('Quantum Harness'));
if (!target) throw new Error('Quantum preview page is not ready');
const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((done, reject) => { socket.onopen = done; socket.onerror = reject; });
let sequence = 0;
const pending = new Map();
socket.onmessage = event => { const reply = JSON.parse(event.data); if (pending.has(reply.id)) { pending.get(reply.id)(reply); pending.delete(reply.id); } };
const send = (method, params = {}) => new Promise(done => { const id = ++sequence; pending.set(id, done); socket.send(JSON.stringify({ id, method, params })); });
const evaluate = async expression => {
  const response = await send('Runtime.evaluate', { expression, returnByValue: true });
  if (response.result?.exceptionDetails) throw new Error(response.result.exceptionDetails.text);
  return response.result?.result?.value;
};
try {
  let ready = false;
  const deadline = Date.now() + 30000;
  while (!ready && Date.now() < deadline) {
    ready = await evaluate(`Array.from(document.querySelectorAll('button')).some(b => /^(新会话|New chat|New session)/.test(b.textContent.trim()))`);
    if (!ready) await new Promise(done => setTimeout(done, 500));
  }
  if (!ready) throw new Error('Expected Quantum navigation is not ready');
  await evaluate(`Array.from(document.querySelectorAll('button')).filter(b => /^(更多|More)$/.test(b.textContent.trim())).at(-1).click()`);
  await new Promise(done => setTimeout(done, 500));
  const menu = await evaluate(`Array.from(document.querySelectorAll('[role=menuitem]')).map(b=>b.textContent.trim())`);
  if (!menu.some(label => /^(连接盒子|Connect box)$/.test(label)) || menu.some(label => /登录|Sign in/.test(label))) throw new Error('Unexpected menu: '+JSON.stringify(menu));
  await evaluate(`Array.from(document.querySelectorAll('[role=menuitem]')).find(b=>/^(连接盒子|Connect box)$/.test(b.textContent.trim())).click()`);
  await new Promise(done => setTimeout(done, 700));
  const result = await evaluate(`(() => { const d=document.querySelector('[role=dialog]'); return {title:document.title, menu:${JSON.stringify(menu)}, dialog:d?.getAttribute('aria-label'), disconnected:/尚未连接|Not connected/.test(d?.textContent??''), guide:/以太网|Ethernet/.test(d?.textContent??''), password:!!d?.querySelector('input[type=password]'), externalLinks:d?.querySelectorAll('a[href]').length}; })()`);
  if (!/^(连接盒子|Connect box)$/.test(result.dialog) || !result.guide || !result.password || result.externalLinks !== 0) throw new Error('Box inspection failed: '+JSON.stringify(result));
  await evaluate(`Array.from(document.querySelectorAll('[role=dialog] button')).find(b=>/^(无线连接|Wireless connection)$/.test(b.textContent.trim())).click()`);
  await new Promise(done => setTimeout(done, 1000));
  const wireless = await evaluate(`(() => { const d=document.querySelector('[role=dialog]'); return { pairKey:/盒子配对密钥|Box pairing key/.test(d?.textContent??''), readonlyWifi:!!d?.querySelector('input[readonly]'), connect: Array.from(d?.querySelectorAll('button')??[]).some(b=>/^(连接盒子|Connect box)$/.test(b.textContent.trim())) }; })()`);
  if (wireless.pairKey || !wireless.readonlyWifi || !wireless.connect) throw new Error('Wireless inspection failed: '+JSON.stringify(wireless));
  const screenshot = await send('Page.captureScreenshot', { format: 'png' });
  const directory = resolve(import.meta.dirname, '../../artifacts/preview');
  mkdirSync(directory, { recursive: true });
  writeFileSync(resolve(directory, `quantum-box-connection-${port}.png`), Buffer.from(screenshot.result.data, 'base64'));
  writeFileSync(resolve(directory, `quantum-box-inspection-${port}.json`), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally { socket.close(); }
