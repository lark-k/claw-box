'use strict';
const fs = require('node:fs'), path = require('node:path'), url = require('node:url');
const child = require('node:child_process'), Module = require('node:module');
const crypto = require('node:crypto');
const native = fs.realpathSync.native, root = path.dirname(__dirname).toLowerCase();
process.env.TEMP = path.join(path.dirname(__dirname), 'temp');
process.env.TMP = process.env.TEMP;
function realpath(input, options) {
  try { return native(input, options); } catch (error) {
    if (!['EPERM', 'EACCES'].includes(error.code)) throw error;
    const file = path.resolve(input instanceof URL ? url.fileURLToPath(input) : Buffer.isBuffer(input) ? input.toString() : input);
    if (file.toLowerCase() !== root && !file.toLowerCase().startsWith(root + path.sep)) throw error;
    for (let current = file; ; current = path.dirname(current)) {
      if (fs.lstatSync(current).isSymbolicLink()) throw error;
      if (current.toLowerCase() === root) break;
    }
    const encoding = typeof options === 'string' ? options : options?.encoding;
    return encoding === 'buffer' ? Buffer.from(file) : file;
  }
}
fs.realpathSync = realpath; fs.realpathSync.native = realpath;
fs.realpath = function (input, options, callback) {
  if (typeof options === 'function') { callback = options; options = undefined; }
  try { const value = realpath(input, options); process.nextTick(callback, null, value); }
  catch (error) { process.nextTick(callback, error); }
};
fs.realpath.native = fs.realpath;
fs.promises.realpath = async (input, options) => realpath(input, options);

// AppContainer cannot use libuv's global named-pipe capture. Disposable regular
// files provide the same synchronous capture without changing OS permissions.
const spawnSync = child.spawnSync;
child.spawnSync = function (file, args, options) {
  if (!Array.isArray(args)) { options = args; args = []; }
  options ??= {};
  if (options.stdio === 'inherit' || options.stdio === 'ignore') return spawnSync(file, args, options);
  const prefix = path.join(process.env.TEMP, 'spawn-' + crypto.randomUUID());
  const files = [prefix + '.in', prefix + '.out', prefix + '.err'];
  const descriptors = [];
  try {
    fs.writeFileSync(files[0], options.input ?? '');
    descriptors.push(fs.openSync(files[0], 'r'), fs.openSync(files[1], 'w+'), fs.openSync(files[2], 'w+'));
    const requested = Array.isArray(options.stdio) ? options.stdio : ['pipe', 'pipe', 'pipe'];
    const stdio = requested.map((value, index) => value === 'pipe' ? descriptors[index] : value);
    const result = spawnSync(file, args, { ...options, input: undefined, stdio });
    const stdout = requested[1] === 'pipe' ? fs.readFileSync(files[1], options.encoding) : null;
    const stderr = requested[2] === 'pipe' ? fs.readFileSync(files[2], options.encoding) : null;
    return { ...result, stdout, stderr, output: [null, stdout, stderr] };
  } finally {
    for (const descriptor of descriptors) fs.closeSync(descriptor);
    for (const filename of files) { try { fs.unlinkSync(filename); } catch {} }
  }
};
child.execFileSync = function (file, args, options) {
  if (!Array.isArray(args)) { options = args; args = []; }
  const result = child.spawnSync(file, args, options);
  if (result.error) throw result.error;
  if (result.status !== 0) {
    const error = new Error('Command failed: ' + file);
    Object.assign(error, result); throw error;
  }
  return result.stdout;
};
child.execSync = (command, options) => child.execFileSync(command, [], { ...options, shell: true });
const exec = child.exec;
child.exec = function (command, options, callback) {
  if (command.trim().toLowerCase() !== 'net use') return exec(command, options, callback);
  if (typeof options === 'function') callback = options;
  const error = Object.assign(new Error('Network drive discovery is disabled in the isolated local snapshot.'), { code: 'EACCES' });
  if (callback) process.nextTick(callback, error, '', '');
  return new (require('node:events').EventEmitter)();
};
Module.syncBuiltinESMExports();
