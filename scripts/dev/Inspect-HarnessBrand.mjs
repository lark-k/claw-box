/** Inspect only the owned Quantum preview through its loopback development endpoint. */
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
const targets = await (await fetch('http://127.0.0.1:9222/json/list')).json();
const target = targets.find(row => row.type === 'page' && row.url === 'dsh-app://app/' && row.title.includes('Quantum Harness'));
if (!target) throw new Error('Quantum preview page is not ready');
const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolvePromise, reject) => { socket.onopen = resolvePromise; socket.onerror = reject; });
let sequence = 0;
const pending = new Map();
socket.onmessage = event => { const reply = JSON.parse(event.data); if (pending.has(reply.id)) { pending.get(reply.id)(reply); pending.delete(reply.id); } };
const send = (method, params = {}) => new Promise(resolvePromise => { const id = ++sequence; pending.set(id, resolvePromise); socket.send(JSON.stringify({ id, method, params })); });
try {
  const observed = await send('Runtime.evaluate', { expression: 'Array.from(document.querySelectorAll("button")).some(b => b.textContent.trim().startsWith("新会话"))', returnByValue: true });
  if (!observed.result?.result?.value) throw new Error('Expected Chinese preview navigation is not ready');
  await send('Runtime.evaluate', { expression: 'if (!document.querySelector("[role=dialog]")) Array.from(document.querySelectorAll("button")).find(b => b.textContent.trim().startsWith("新会话")).click()' });
  await new Promise(resolvePromise => setTimeout(resolvePromise, 700));
  const observedBrand = await send('Runtime.evaluate', { expression: `JSON.stringify({title:document.title, brandCount:document.body.innerText.split('Quantum Harness').length-1, buildBadges:document.querySelectorAll('[data-copy-version]').length, oldHeadline:document.body.innerText.includes('探索未至之境'), icons:Array.from(document.images).filter(i=>i.src.endsWith('/favicon.svg')).map(i=>({loaded:i.complete&&i.naturalWidth>0,width:i.width,height:i.height}))})`, returnByValue: true });
  const result = JSON.parse(observedBrand.result.result.value);
  if (result.brandCount < 2 || result.buildBadges !== 0 || result.oldHeadline || result.icons.filter(i => i.loaded).length < 2) throw new Error('Brand inspection failed: '+JSON.stringify(result));
  const screenshot = await send('Page.captureScreenshot', { format: 'png' });
  const directory = resolve(import.meta.dirname, '../../artifacts/preview');
  mkdirSync(directory, { recursive: true });
  writeFileSync(resolve(directory, 'quantum-brand-home.png'), Buffer.from(screenshot.result.data, 'base64'));
  writeFileSync(resolve(directory, 'quantum-brand-inspection.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally { socket.close(); }
