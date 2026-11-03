/** Preserve NSIS upgrade rollback while allowing the complete runtime's deep file paths. */
import { createRequire } from 'node:module'
import { dirname, join } from 'node:path'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
const require = createRequire(import.meta.url)
const patched = Symbol.for('quantum-harness/nsis-long-paths')

export function installQuantumNsis() {
  require('app-builder-lib')
  const { NsisTarget } = require('app-builder-lib/out/targets/nsis/NsisTarget.js')
  const prototype = NsisTarget.prototype
  if (prototype[patched]) return
  prototype[patched] = true
  const compute = prototype.computeFinalScript
  prototype.computeFinalScript = async function (source, ...args) {
    const directory = join(this.outDir, '.quantum-nsis')
    await mkdir(directory, { recursive: true })
    const templates = join(dirname(require.resolve('app-builder-lib/package.json')), 'templates/nsis')
    const original = await readFile(join(templates, 'uninstaller.nsh'), 'utf8')
    if (!original.includes('Call un.atomicRMDir') || !original.includes('Call un.restoreFiles')) throw new Error('Review changed NSIS rollback template')
    const fixed = original.replaceAll('"$INSTDIR', '"\\\\?\\$INSTDIR')
      .replaceAll('"$PLUGINSDIR\\old-install', '"\\\\?\\$PLUGINSDIR\\old-install')
      .replace('RMDir /r $INSTDIR', 'RMDir /r "\\\\?\\$INSTDIR"')
    const path = join(directory, 'uninstaller.nsh')
    await writeFile(path, fixed)
    if (source.split('!include "uninstaller.nsh"').length !== 2) throw new Error('Review changed NSIS entry template')
    return compute.call(this, source.replace('!include "uninstaller.nsh"', `!include "${path}"`), ...args)
  }
}
