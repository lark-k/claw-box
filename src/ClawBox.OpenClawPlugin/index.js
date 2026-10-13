import { ExecutorClient, loadConfiguration } from './client.js';

const definitions = [
  ['windows_file_read', 'file.read', 'Read a UTF-8 file on the paired Windows PC.', ['path'], []],
  ['windows_file_write', 'file.write', 'Write UTF-8 on Windows. Existing files require their current expectedSha256.', ['path', 'content'], ['expectedSha256']],
  ['windows_word_read', 'word.read', 'Read ordinary body/table text from a Windows DOCX.', ['path'], []],
  ['windows_word_create', 'word.create', 'Create a new Windows DOCX from text; cannot overwrite.', ['path', 'content'], []],
  ['windows_word_replace', 'word.replace', 'Replace literal body text into a NEW Windows DOCX; source is preserved.', ['path', 'outputPath', 'search', 'replacement'], []],
];

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
          properties: Object.fromEntries([...required, ...optional].map(field => [field, { type: 'string' }])),
          required,
        },
        execute: async (_toolCallId, params, signal, onUpdate) => client.execute(tool, params, signal, onUpdate),
      }, { optional: true });
    }
  },
};
