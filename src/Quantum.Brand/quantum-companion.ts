/** Electron-owned, fixed-operation bridge to the Windows box companion. */
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { createInterface } from 'node:readline'
import { join } from 'node:path'

const operations = new Set(['status', 'models', 'reconnect', 'disconnect', 'discover', 'current-wifi', 'pair-wired', 'pair-wireless'])
type Pending = { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }

export class QuantumCompanion {
  private child: ChildProcessWithoutNullStreams | undefined
  private next = 0
  private pending = new Map<string, Pending>()

  async call(value: unknown): Promise<unknown> {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Error('Invalid box request')
    const request = value as Record<string, unknown>
    if (typeof request.op !== 'string' || !operations.has(request.op)) throw new Error('Unsupported box request')
    const raw = JSON.stringify(request)
    if (raw.length > 16384) throw new Error('Box request is too large')
    const child = this.start()
    const id = String(++this.next)
    return await new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        this.dispose()
        reject(new Error('Box connection timed out'))
      }, 190_000)
      this.pending.set(id, { resolve, reject, timer })
      child.stdin.write(JSON.stringify({ ...request, id }) + '\n', error => {
        if (error === null || error === undefined) return
        const entry = this.pending.get(id)
        if (entry !== undefined) { clearTimeout(entry.timer); this.pending.delete(id); entry.reject(new Error('Box connection is unavailable')) }
      })
    })
  }

  private start(): ChildProcessWithoutNullStreams {
    if (this.child !== undefined && !this.child.killed && this.child.exitCode === null) return this.child
    const executable = process.env.QUANTUM_BOX_COMPANION ?? join(process.resourcesPath, 'quantum-box', 'QuantumBoxCompanion.exe')
    const child = spawn(executable, [], { windowsHide: true, stdio: 'pipe' })
    this.child = child
    const lines = createInterface({ input: child.stdout, crlfDelay: Infinity })
    lines.on('line', (line) => {
      if (line.length > 32768) { this.dispose(); return }
      try {
        const message: unknown = JSON.parse(line)
        if (typeof message !== 'object' || message === null || !('id' in message) || typeof message.id !== 'string') return
        const entry = this.pending.get(message.id)
        if (entry === undefined) return
        clearTimeout(entry.timer)
        this.pending.delete(message.id)
        if ('ok' in message && message.ok === true && 'result' in message) entry.resolve(message.result)
        else entry.reject(new Error('error' in message && typeof message.error === 'string' ? message.error.slice(0, 256) : 'Box connection failed'))
      } catch { this.dispose() }
    })
    // The child is designed not to write credentials or diagnostics to stderr; discard it regardless.
    child.stderr.resume()
    const ended = () => {
      if (this.child !== child) return
      this.child = undefined
      for (const entry of this.pending.values()) { clearTimeout(entry.timer); entry.reject(new Error('Box connection is unavailable')) }
      this.pending.clear()
    }
    child.once('error', ended)
    child.once('exit', ended)
    return child
  }

  dispose(): void {
    const child = this.child
    this.child = undefined
    child?.kill()
    for (const entry of this.pending.values()) { clearTimeout(entry.timer); entry.reject(new Error('Box connection closed')) }
    this.pending.clear()
  }
}
