import { mkdtempSync, mkdirSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, it, onTestFinished } from 'vitest'
import { canonicalRuntimeDirectory } from '../src/runtime-path.ts'

it('keeps archive virtual paths under the physical installation used by external plugin resolution', () => {
  const root = mkdtempSync(join(tmpdir(), 'quantum-install-path-'))
  onTestFinished(() => rmSync(root, { recursive: true, force: true }))
  const actual = join(root, 'physical')
  const alias = join(root, 'alias')
  mkdirSync(actual)
  symlinkSync(actual, alias, process.platform === 'win32' ? 'junction' : 'dir')
  writeFileSync(join(actual, 'app.asar'), 'fixture archive file')
  expect(canonicalRuntimeDirectory(join(alias, 'app.asar', 'dsh'))).toBe(join(realpathSync(actual), 'app.asar', 'dsh'))
  expect(canonicalRuntimeDirectory(alias)).toBe(realpathSync(actual))
})
