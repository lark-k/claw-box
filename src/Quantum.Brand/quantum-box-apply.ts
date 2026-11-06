/** Quantum composition uses the existing settings service without an account dependency. */
import type { Context } from '@deepseek-ai/cordis'
import type {} from '@deepseek-ai/dsh-client-ui-settings/client'
import type {} from '@deepseek-ai/dsh-client-locale/client'
import type {} from '@deepseek-ai/dsh-client-ui-renderer/client'
import type {} from '@deepseek-ai/dsh-api-remotes/client'
import type { BoxSettings } from '../box-settings.ts'
import { validBoxHost } from '../box-settings.ts'
import { en, zh, type BoxKey } from './box-locales.ts'
import { QuantumBoxMenu, QuantumBoxSection, type BoxInjected } from './QuantumBox.tsx'

declare module '@deepseek-ai/dsh-client-ui-slots' {
  interface LocaleNamespaceMap { 'settings.box': BoxKey }
}

/** Register only device UI, with no account watch, sign-in or onboarding effects. */
export function applyQuantumBox(ctx: Context): void {
  ctx.effect(() => ctx.locale.register('settings.box', { en, zh, ja: en, es: en, fr: en, de: en, ru: en, pt: en }), 'quantum: box copy')
  const t = ctx.locale.bind('settings.box')
  const form = ctx.configForms.get<BoxSettings>('ui-settings-account')
  let observedConnected = false
  const removeModel = async () => {
    const removed = await ctx.remote.settings.mutate('llm-pi-ai', [
      { op: 'unset', path: ['providers', 'quantum-box-device'] },
    ], undefined)
    if (!removed.ok) throw new Error('已断开盒子，但无法移除盒子模型，请重试。')
    const credential = await ctx.remote.credentials.unset('QUANTUM_BOX_DEVICE_API_KEY')
    if (!credential.ok) throw new Error('已断开盒子，但无法移除盒子模型凭据。')
  }
  const call = async (request: Record<string, unknown>): Promise<unknown> => {
    const bridge = (globalThis as unknown as { quantumBox?: { call: (value: unknown) => Promise<unknown> } }).quantumBox
    if (bridge === undefined) throw new Error('请在桌面应用中连接盒子')
    const value = await bridge.call(request)
    if (request.op === 'disconnect') {
      await removeModel()
      observedConnected = false
      return value
    }
    if (typeof value !== 'object' || value === null || !('connected' in value)) return value
    if (value.connected !== true) {
      if (observedConnected) { await removeModel(); observedConnected = false }
      return value
    }
    if (request.op === 'status') return { connected: true, connectionMode: 'connectionMode' in value ? value.connectionMode : undefined }
    const status = value as { connected: boolean; host?: string; model?: string; localPort?: number; token?: string; contextWindow?: number; connectionMode?: string }
    if (typeof status.token !== 'string' || typeof status.model !== 'string' || !Number.isInteger(status.localPort)) throw new Error('盒子模型信息无效')
    const credential = await ctx.remote.credentials.set('QUANTUM_BOX_DEVICE_API_KEY', status.token)
    if (!credential.ok) throw new Error('保存盒子模型凭据失败')
    const provider = await ctx.remote.settings.mutate('llm-pi-ai', [{ op: 'set', path: ['providers', 'quantum-box-device'], value: {
      displayName: 'Quantum Box', apiKeyEnv: 'QUANTUM_BOX_DEVICE_API_KEY', api: 'openai-completions',
      baseURL: `http://127.0.0.1:${status.localPort}/v1`,
      compat: { supportsDeveloperRole: false, maxTokensField: 'max_tokens', thinkingFormat: 'chat-template', chatTemplateKwargs: { enable_thinking: { $var: 'thinking.enabled' } } },
      models: [{ id: status.model, input: ['text'], reasoningEfforts: { off: null, medium: 'medium' }, ...(status.contextWindow ? { contextWindow: status.contextWindow } : {}) }],
    } }], undefined)
    if (!provider.ok) throw new Error('配置盒子模型失败')
    const selection = await ctx.remote.settings.mutate('agent-default-model', [
      { op: 'set', path: ['provider'], value: 'quantum-box-device' }, { op: 'set', path: ['model'], value: status.model },
      { op: 'unset', path: ['reasoningEffort'] },
    ], undefined)
    if (!selection.ok) throw new Error('选择盒子模型失败')
    observedConnected = true
    return { connected: true, host: status.host, model: status.model, localPort: status.localPort, connectionMode: status.connectionMode }
  }
  const injected = (): BoxInjected => ({
    hooks: { box: form },
    call,
    save: (host, port, revision) => {
      if (!validBoxHost(host) || !Number.isInteger(port) || port < 1 || port > 65535) return Promise.resolve(false)
      return form.mutate([{ op: 'set', path: ['boxHost'], value: host }, { op: 'set', path: ['boxSshPort'], value: port }], revision)
    },
  })
  ctx.effect(() => {
    const bridge = (globalThis as unknown as { quantumBox?: unknown }).quantumBox
    if (bridge === undefined) return () => {}
    void call({ op: 'reconnect' }).catch(() => { void removeModel().catch(() => {}) })
    const timer = setInterval(() => { void call({ op: 'status' }).catch(() => {}) }, 5000)
    return () => { clearInterval(timer) }
  }, 'quantum: reconnect box')
  ctx.slots.inject('settings.launcher', () => ctx.slots.register({
    name: 'settings.launcher', locale: 'settings.box', inject: injected,
  }, QuantumBoxMenu))
  ctx.slots.inject('settings.section', () => ctx.slots.register({
    name: 'settings.section', id: 'quantum-box', order: -10, label: () => t('title'), locale: 'settings.box', inject: injected,
  }, QuantumBoxSection))
}
