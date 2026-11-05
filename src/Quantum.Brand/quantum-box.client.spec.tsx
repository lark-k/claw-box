// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import type { GlobalStandardProps } from '@deepseek-ai/dsh-client-ui-slots'
import { createClientTest, webApp } from '@deepseek-ai/dsh-client-test-runtime/src/assembly/index.ts'
import { BoxEditor, QuantumBoxMenu, type BoxInjected } from '../src/client/QuantumBox.tsx'
import type { ConfigFormSnapshot } from '@deepseek-ai/dsh-client-ui-settings/client'
import { BoxSettingsFields, validBoxHost, type BoxSettings } from '../src/box-settings.ts'
import { en, zh, type BoxKey } from '../src/client/box-locales.ts'

afterEach(() => { cleanup(); vi.unstubAllEnvs(); vi.restoreAllMocks() })
const globals = {} as GlobalStandardProps
const ready: ConfigFormSnapshot<BoxSettings> = { status: 'ready', value: {}, base: {}, user: {}, revision: 7, writable: true }

function editor(copy: typeof en | typeof zh = zh, state = ready, save = vi.fn(async () => true)) {
  const props = { ...globals, useBox: <T,>(selector: (value: ConfigFormSnapshot<BoxSettings>) => T) => selector(state),
    save, t: (key: BoxKey) => copy[key] }
  render(<BoxEditor {...props} />)
  return { props, save }
}

it.each(['192.168.137.2', '127.0.0.1', 'box', 'quantum-box.local', 'box.example.com'])('accepts device host %s in both validators', host => {
  expect(validBoxHost(host)).toBe(true)
  expect(BoxSettingsFields.boxHost(host)).toBe(host)
})
it.each(['', 'https://box.local', 'user@box', 'box:22', 'box/a', 'box;whoami', '-box', '256.1.1.1', '01.1.1.1', '1.1', 'x'.repeat(254)])('rejects invalid host %s in both validators', host => {
  expect(validBoxHost(host)).toBe(false)
  expect(() => BoxSettingsFields.boxHost(host)).toThrow()
})
it.each([0, 65536, 1.5])('rejects invalid SSH port %s on the Host', port => {
  expect(() => BoxSettingsFields.boxSshPort(port)).toThrow()
})
it.each([en, zh])('saves pending details and explicitly remains disconnected', async copy => {
  const { save } = editor(copy)
  fireEvent.change(screen.getByLabelText(copy.host), { target: { value: '  quantum-box.local  ' } })
  fireEvent.change(screen.getByLabelText(copy.port), { target: { value: '2222' } })
  fireEvent.click(screen.getByRole('button', { name: copy.save }))
  await screen.findByText(copy.saved)
  expect(save).toHaveBeenCalledWith('quantum-box.local', 2222, 7)
  expect(screen.getByText(copy.status)).toBeTruthy()
  expect(screen.queryByRole('link')).toBeNull()
})
it('keeps invalid input visible without saving', () => {
  const { save } = editor()
  fireEvent.change(screen.getByLabelText(zh.host), { target: { value: 'box;whoami' } })
  fireEvent.click(screen.getByRole('button', { name: zh.save }))
  expect(screen.getByRole('alert').textContent).toBe(zh.invalidHost)
  expect(save).not.toHaveBeenCalled()
  fireEvent.change(screen.getByLabelText(zh.host), { target: { value: 'box.local' } })
  fireEvent.change(screen.getByLabelText(zh.port), { target: { value: '22.5' } })
  fireEvent.click(screen.getByRole('button', { name: zh.save }))
  expect(screen.getByRole('alert').textContent).toBe(zh.invalidPort)
  expect(save).not.toHaveBeenCalled()
})
it.each([false, 'throw'])('reports failed save and preserves the draft (%s)', async outcome => {
  const { save } = editor(zh, ready, vi.fn(async () => { if (outcome === 'throw') throw new Error('offline'); return false }))
  fireEvent.change(screen.getByLabelText(zh.host), { target: { value: 'box.local' } })
  fireEvent.click(screen.getByRole('button', { name: zh.save }))
  await waitFor(() => expect(screen.getByRole('alert').textContent).toBe(zh.failed))
  expect((screen.getByLabelText(zh.host) as HTMLInputElement).value).toBe('box.local')
  expect(save).toHaveBeenCalledTimes(1)
})
it('prevents saving before settings load or when writes are unavailable', () => {
  const { save } = editor(zh, { ...ready, status: 'unavailable', writable: false })
  expect((screen.getByRole('button', { name: zh.save }) as HTMLButtonElement).disabled).toBe(true)
  expect(screen.getByText(zh.unavailable)).toBeTruthy()
  expect(save).not.toHaveBeenCalled()
})
it('connects a wired box and clears the administrator password', async () => {
  const call = vi.fn(async (request: Record<string, unknown>) => ({ connected: request.op === 'pair-wired' }))
  const props = { ...globals, useBox: <T,>(selector: (value: ConfigFormSnapshot<BoxSettings>) => T) => selector(ready),
    save: vi.fn(async () => true), call, t: (key: BoxKey) => zh[key] }
  render(<BoxEditor {...props} />)
  await waitFor(() => expect(call).toHaveBeenCalledWith({ op: 'status' }))
  fireEvent.change(screen.getByLabelText(zh.adminPassword), { target: { value: 'test-password' } })
  fireEvent.click(screen.getByRole('button', { name: zh.connect }))
  await waitFor(() => expect(call).toHaveBeenCalledWith({ op: 'pair-wired', host: '192.168.137.2', port: 22, password: 'test-password' }))
  await screen.findByText(zh.connected)
  expect((screen.getByLabelText(zh.adminPassword) as HTMLInputElement).value).toBe('')
})
it('uses the computer Wi-Fi and does not ask for a pairing key', async () => {
  const call = vi.fn(async (request: Record<string, unknown>) => {
    if (request.op === 'current-wifi') return { name: 'Office Wi-Fi' }
    if (request.op === 'discover') return [{ address: '42', name: 'Quantum Box' }]
    return { connected: request.op === 'pair-wireless' }
  })
  const props = { ...globals, useBox: <T,>(selector: (value: ConfigFormSnapshot<BoxSettings>) => T) => selector(ready),
    save: vi.fn(async () => true), call, t: (key: BoxKey) => zh[key] }
  render(<BoxEditor {...props} />)
  fireEvent.click(screen.getByRole('button', { name: zh.wireless }))
  await waitFor(() => expect((screen.getByLabelText(zh.wifiSsid) as HTMLInputElement).value).toBe('Office Wi-Fi'))
  expect((screen.getByLabelText(zh.wifiSsid) as HTMLInputElement).readOnly).toBe(true)
  expect(screen.queryByLabelText(zh.pairingCode)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: zh.discover }))
  await screen.findByText('Quantum Box')
  fireEvent.change(screen.getByLabelText(zh.wifiPassword), { target: { value: 'test-password' } })
  fireEvent.click(screen.getByRole('button', { name: zh.connect }))
  await waitFor(() => expect(call).toHaveBeenCalledWith({ op: 'pair-wireless', address: '42', port: 22, wifi: { password: 'test-password' } }))
})
it('offers box configuration instead of login and closes the dialog', async () => {
  const { props } = editor()
  cleanup()
  render(<QuantumBoxMenu {...props} wide settingsOpen={false} openSettings={vi.fn()} openOnboarding={vi.fn()} />)
  fireEvent.click(screen.getByRole('button', { name: zh.menu }))
  fireEvent.click(await screen.findByRole('menuitem', { name: zh.title }))
  expect(screen.getByRole('dialog', { name: zh.title })).toBeTruthy()
  expect(screen.queryByText('登录')).toBeNull()
  expect(screen.queryByRole('link')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: zh.close }))
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
})

const integrated = createClientTest({ roster: webApp })
integrated('Quantum registers box UI without account streams, login or official onboarding', async ({ start }) => {
  vi.stubEnv('DSH_CLIENT_BUILD_PROFILE', 'quantum')
  const c = await start()
  expect(c.ctx.slots.entries('settings.section').some(entry => entry.options.id === 'quantum-box')).toBe(true)
  expect(c.ctx.slots.entries('settings.section').some(entry => entry.options.id === 'account')).toBe(false)
  expect(c.ctx.slots.entries('settings.onboarding').some(entry => entry.options.id === 'deepseek-official')).toBe(false)
  expect(c.ctx.slots.entries('shell.overlay').some(entry => entry.options.id === 'desktop-onboarding')).toBe(false)
  expect(c.mock.streams.end('account/watch')).toBe(0)
  const entry = c.ctx.slots.entries('settings.launcher')[0]!
  expect(entry.component).toBe(QuantumBoxMenu)
  const value: object = entry.inject!()
  const injected = value as BoxInjected
  expect(await injected.save('bad;command', 22, 1)).toBe(false)
  expect(await injected.save('box.local', 65536, 1)).toBe(false)
}, 60_000)
