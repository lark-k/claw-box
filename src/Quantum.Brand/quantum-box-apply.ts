/** Quantum composition uses the existing settings service without an account dependency. */
import type { Context } from '@deepseek-ai/cordis'
import type {} from '@deepseek-ai/dsh-client-ui-settings/client'
import type {} from '@deepseek-ai/dsh-client-locale/client'
import type {} from '@deepseek-ai/dsh-client-ui-renderer/client'
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
  const injected = (): BoxInjected => ({
    hooks: { box: form },
    save: (host, port, revision) => {
      if (!validBoxHost(host) || !Number.isInteger(port) || port < 1 || port > 65535) return Promise.resolve(false)
      return form.mutate([{ op: 'set', path: ['boxHost'], value: host }, { op: 'set', path: ['boxSshPort'], value: port }], revision)
    },
  })
  ctx.slots.inject('settings.launcher', () => ctx.slots.register({
    name: 'settings.launcher', locale: 'settings.box', inject: injected,
  }, QuantumBoxMenu))
  ctx.slots.inject('settings.section', () => ctx.slots.register({
    name: 'settings.section', id: 'quantum-box', order: -10, label: () => t('title'), locale: 'settings.box', inject: injected,
  }, QuantumBoxSection))
}
