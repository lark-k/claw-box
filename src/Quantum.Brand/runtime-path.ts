/** Keep ASAR and external plugin imports on one physical Windows installation identity. */
import { realpathSync } from 'node:fs'

/** Resolve the archive file rather than treating its virtual children as native directories. */
export function canonicalRuntimeDirectory(directory: string): string {
  const archive = /^(.*?\.asar)([\\/].*)?$/u.exec(directory)
  return archive === null ? realpathSync.native(directory) : realpathSync.native(archive[1]!) + (archive[2] ?? '')
}

/** Run inside Electron so its ASAR-aware filesystem can resolve Windows package virtualization. */
export const HOST_BOOTSTRAP = String.raw`
const { realpathSync } = require('node:fs')
const { join } = require('node:path')
const directory = process.argv[1]
const archive = /^(.*?\.asar)([\\/].*)?$/u.exec(directory)
const root = archive === null ? realpathSync.native(directory) : realpathSync.native(archive[1]) + (archive[2] ?? '')
const entry = join(root, 'node_modules', '@deepseek-ai', 'dsh-desktop-host', 'lib', 'index.js')
process.argv.splice(1, 0, entry)
process.argv[2] = root
require('node:module').runMain(entry)
`
