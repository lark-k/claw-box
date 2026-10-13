import { ExecutorClient, loadConfiguration } from '../../src/ClawBox.OpenClawPlugin/client.js';

const [port, workerTokenFile, workspace, tag] = process.argv.slice(2);
const client = new ExecutorClient(loadConfiguration({ port: Number(port), workerTokenFile, timeoutSeconds: 30 }));
const path = name => workspace + '\\' + tag + name;
const expected = 'ClawBox bridge original';
const calls = [
  ['file.write', { path: path('.txt'), content: expected }],
  ['file.read', { path: path('.txt') }],
  ['word.create', { path: path('.docx'), content: expected }],
  ['word.replace', { path: path('.docx'), outputPath: path('-updated.docx'), search: 'original', replacement: 'verified' }],
  ['word.read', { path: path('-updated.docx') }],
];
for (const [tool, args] of calls) {
  const answer = await client.execute(tool, args);
  if (answer.isError) throw new Error('Bridge tool did not succeed: ' + answer.details.status);
  if (tool === 'file.read' && answer.details.result.content !== expected) throw new Error('Wrong text.');
  if (tool === 'word.read' && answer.details.result.content !== 'ClawBox bridge verified') throw new Error('Wrong DOCX.');
}
console.log('Plugin-to-executor five-tool verification passed.');
