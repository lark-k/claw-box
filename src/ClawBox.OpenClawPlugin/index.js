import { ExecutorClient, loadConfiguration } from './client.js';

const definitions = [
  ['windows_file_read', 'file.read', 'Read a UTF-8 file on the paired Windows PC.', ['path'], []],
  ['windows_project_list', 'file.list', 'Discover files in a selected Windows code project and obtain snapshotSha256. Start here for code development; no command knowledge needed.', ['path'], []],
  ['windows_project_check', 'project.check', 'Build or test a Windows project snapshot in native Windows isolation. The executor detects Node/Vue/React, Maven/Java, Gradle or .NET. kind must be build or test. Pass the latest snapshotSha256 as expectedSnapshotSha256; refresh it after code changes. Check checksPassed and logs, not just request success.', ['path', 'kind', 'expectedSnapshotSha256'], []],
  ['windows_file_write', 'file.write', 'Write UTF-8 on Windows. Existing files require their current expectedSha256.', ['path', 'content'], ['expectedSha256']],
  ['windows_word_read', 'word.read', 'Read ordinary body/table text from a Windows DOCX.', ['path'], []],
  ['windows_word_create', 'word.create', 'Create a new Windows DOCX from text; cannot overwrite.', ['path', 'content'], []],
  ['windows_word_replace', 'word.replace', 'Replace literal DOCX text. By default modify the original: omit outputPath and supply current expectedSha256 from reading. Use a NEW outputPath only if user asks for a copy.', ['path', 'search', 'replacement'], ['outputPath', 'expectedSha256']],
  ['windows_directory_create', 'directory.create', 'Create a Windows directory including missing parent folders. Use before writing new files into a new directory.', ['path'], []],
  ['windows_directory_list', 'directory.list', 'Browse direct child files and folders, including DOCX and other office files. Unlike project_list, this is general directory browsing. Links are reported but not followed.', ['path'], []],
  ['windows_file_info', 'file.info', 'Get size, timestamp and current SHA256 of a file, including binary/Word files. Use the hash for copy, move or modifying a file.', ['path'], []],
  ['windows_file_search', 'file.search', 'Find literal case-sensitive text in UTF-8 source/text files under a directory; returns bounded line excerpts and hashes. Not a regex or web search.', ['path', 'query'], []],
  ['windows_file_copy', 'file.copy', 'Copy a file to a NEW destination without overwriting. Requires current expectedSha256 of the source. Create destination folder first.', ['path', 'outputPath', 'expectedSha256'], []],
  ['windows_file_move', 'file.move', 'Move or rename one file on the same volume to a NEW destination, no overwrite. Requires current source expectedSha256; does not move directories.', ['path', 'outputPath', 'expectedSha256'], []],
  ['windows_file_replace', 'file.replace', 'Replace all literal matches in the original UTF-8 file using current expectedSha256; no regex. Search must be nonempty. No match leaves file unchanged.', ['path', 'search', 'replacement', 'expectedSha256'], []],
  ['windows_word_format', 'word.format', 'Format DOCX in place by default; only specify NEW outputPath for an explicit copy. Requires current expectedSha256. scope document or paragraph; paragraph selects ALL paragraphs with exact text. Options are string values: bold true/false, fontName, fontSizePt 6..72 in half-point steps, alignment left/center/right/justify, style Normal/Title/Heading1/Heading2/Heading3. Supply at least one option. Body and table paragraphs only; no page layout or text-fragment selection.', ['path', 'expectedSha256', 'scope'], ['outputPath', 'text', 'bold', 'fontName', 'fontSizePt', 'alignment', 'style']],
];

function propertySchema(field) {
  const enums = {scope: ['document', 'paragraph'], bold: ['true', 'false'], alignment: ['left', 'center', 'right', 'justify'], style: ['Normal', 'Title', 'Heading1', 'Heading2', 'Heading3'], kind: ['build', 'test']};
  if (enums[field]) return {type: 'string', enum: enums[field]};
  if (field === 'expectedSha256') return {type: 'string', pattern: '^[A-Fa-f0-9]{64}$'};
  if (field === 'expectedSnapshotSha256') return {type: 'string', pattern: '^[A-F0-9]{64}$'};
  return {type: 'string'};
}

export default {
  id: 'clawbox-windows',
  name: 'ClawBox Windows tools',
  register(api) {
    const client = new ExecutorClient(loadConfiguration(api.pluginConfig ?? {}));
    for (const [name, tool, description, required, optional] of definitions) {
      api.registerTool({
        name, label: name, description: `${description} Use absolute Windows paths in the configured workspace. Every call waits for exact Windows user approval. Never repeat a write after an unknown outcome.`,
        parameters: {
          type: 'object', additionalProperties: false,
          properties: Object.fromEntries([...required, ...optional].map(field => [field, propertySchema(field)])),
          required,
        },
        execute: async (_toolCallId, params, signal, onUpdate) => client.execute(tool, params, signal, onUpdate),
      }, { optional: true });
    }
  },
};
