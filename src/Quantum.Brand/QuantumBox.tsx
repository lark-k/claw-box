/** Quantum's pending device connection surface replaces the official account UI. */
import { useEffect, useId, useRef, useState } from 'react'
import { Button, Input, Menu, Modal, IconEllipsisOutlineMedium, IconLinkOutlineRegular, IconSettingsOutlineMedium } from '@deepseek-ai/dsh-client-ui-primitives'
import type { InjectFace, PropsLocale, PropsRuntime } from '@deepseek-ai/dsh-client-ui-slots'
import type { ConfigForm } from '@deepseek-ai/dsh-client-ui-settings/client'
import type { BoxSettings } from '../box-settings.ts'
import { validBoxHost } from '../box-settings.ts'
import type { BoxKey } from './box-locales.ts'
import menuCss from './AccountMenu.module.css'
import css from './QuantumBox.module.css'

/** The only durable values are a pending address and port; credentials are not accepted here. */
export interface BoxInjected {
  hooks: { box: ConfigForm<BoxSettings> }
  save: (host: string, port: number, revision: number) => Promise<boolean>
  call?: (request: Record<string, unknown>) => Promise<unknown>
}
type EditorProps = InjectFace<BoxInjected> & PropsLocale<'settings.box'>

/** One editor is shared by the sidebar dialog and the settings page. */
export function BoxEditor({ useBox, save, call, t }: EditorProps) {
  const snapshot = useBox(value => value)
  const [draft, setDraft] = useState<{ host: string; port: string }>()
  const host = draft?.host ?? snapshot.value?.boxHost ?? ''
  const port = draft?.port ?? String(snapshot.value?.boxSshPort ?? 22)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<BoxKey>()
  const [connectionError, setConnectionError] = useState('')
  const [connected, setConnected] = useState(false)
  const [connectionMode, setConnectionMode] = useState('')
  const [mode, setMode] = useState<'wired' | 'wireless'>('wired')
  const [password, setPassword] = useState('')
  const [ssid, setSsid] = useState('')
  const [wifiPassword, setWifiPassword] = useState('')
  const [devices, setDevices] = useState<{ address: string; name: string }[]>([])
  const [device, setDevice] = useState('')
  useEffect(() => {
    let active = true
    const refresh = () => { if (call !== undefined) void call({ op: 'status' }).then(value => {
      if (active && typeof value === 'object' && value !== null && 'connected' in value) {
        setConnected(value.connected === true)
        setConnectionMode('connectionMode' in value && typeof value.connectionMode === 'string' ? value.connectionMode : '')
      }
    }).catch(() => {}) }
    refresh()
    const timer = setInterval(refresh, 5000)
    return () => { active = false; clearInterval(timer) }
  }, [call])
  const fieldId = useId()
  const update = (value: { host: string; port: string }) => { setDraft(value); setMessage(undefined) }
  const submit = async () => {
    const normalized = host.trim()
    if (!validBoxHost(normalized)) { setMessage('invalidHost'); return }
    if (!/^\d+$/u.test(port) || Number(port) < 1 || Number(port) > 65535) { setMessage('invalidPort'); return }
    if (snapshot.revision === undefined || !snapshot.writable) { setMessage('unavailable'); return }
    setBusy(true)
    setMessage(undefined)
    try {
      const accepted = await save(normalized, Number(port), snapshot.revision)
      setMessage(accepted ? 'saved' : 'failed')
      if (accepted) setDraft(undefined)
    } catch { setMessage('failed') } finally { setBusy(false) }
  }
  const error = message !== undefined && message !== 'saved'
  const perform = async (request: Record<string, unknown>) => {
    if (call === undefined) { setMessage('desktopOnly'); return }
    setBusy(true); setMessage(undefined); setConnectionError('')
    try {
      const value = await call(request)
      if (request.op === 'discover') {
        const found = Array.isArray(value) ? value as { address: string; name: string }[] : []
        setDevices(found); setDevice(found[0]?.address ?? '')
        if (found.length === 0) setMessage('noBoxes')
      } else if (typeof value === 'object' && value !== null && 'connected' in value) {
        setConnected(value.connected === true)
        setConnectionMode('connectionMode' in value && typeof value.connectionMode === 'string' ? value.connectionMode : '')
      }
    } catch (error) {
      setConnectionError(error instanceof Error ? error.message.replace(/^Error invoking remote method 'quantum-box:call': Error: /u, '') : t('connectionFailed'))
    } finally { setBusy(false); if (request.op !== 'discover') { setPassword(''); setWifiPassword('') } }
  }
  const chooseWireless = async () => {
    setMode('wireless'); setConnectionError(''); setSsid('')
    try {
      const result = await call?.({ op: 'current-wifi' })
      if (typeof result === 'object' && result !== null && 'name' in result && typeof result.name === 'string') setSsid(result.name)
    } catch (error) { setConnectionError(error instanceof Error ? error.message : t('connectionFailed')) }
  }
  return <div className={css.editor}>
    <div className={css.device}>
      <span className={css.deviceIcon}><IconLinkOutlineRegular size={22} /></span>
      <div><h3>{t('heading')}</h3><span className={css.status}>{t(connected ? connectionMode === 'wifi' ? 'connectedNetwork' : 'connected' : 'status')}</span></div>
    </div>
    <p className={css.description}>{t('description')}</p>
    {call === undefined && <p className={css.pending}>{t('pending')}</p>}
    {call === undefined && <form noValidate onSubmit={event => { event.preventDefault(); void submit() }} className={css.form}>
      <label htmlFor={`${fieldId}-host`}>{t('host')}</label>
      <Input id={`${fieldId}-host`} value={host} disabled={busy} placeholder={t('hostPlaceholder')} autoComplete="off"
        aria-describedby={`${fieldId}-hint`} aria-invalid={message === 'invalidHost'}
        onChange={event => update({ host: event.target.value, port })} />
      <span id={`${fieldId}-hint`} className={css.hint}>{t('hostHint')}</span>
      <label htmlFor={`${fieldId}-port`}>{t('port')}</label>
      <Input id={`${fieldId}-port`} value={port} disabled={busy} inputMode="numeric" aria-invalid={message === 'invalidPort'}
        onChange={event => update({ host, port: event.target.value })} />
      <div className={css.feedback} aria-live="polite" role={error ? 'alert' : 'status'}>
        {message !== undefined ? t(message) : snapshot.status === 'unavailable' ? t('unavailable') : null}
      </div>
      <div className={css.actions}><Button type="submit" variant="primary" disabled={busy || snapshot.status !== 'ready' || !snapshot.writable}>
        {t(busy ? 'saving' : 'save')}
      </Button></div>
    </form>}
    {call !== undefined && <div className={css.form}>
      {connectionError && <div className={css.connectionError} role="alert">{connectionError}</div>}
      <div className={css.actions}>
        <Button type="button" onClick={() => setMode('wired')}>{t('wired')}</Button>
        <Button type="button" onClick={() => { void chooseWireless() }}>{t('wireless')}</Button>
      </div>
      {mode === 'wired' ? <>
        <p className={css.hint}>{t('wiredGuide')}</p>
        <label htmlFor={`${fieldId}-password`}>{t('adminPassword')}</label>
        <Input id={`${fieldId}-password`} type="password" value={password} autoComplete="off" onChange={event => setPassword(event.target.value)} />
      </> : <>
        <Button type="button" disabled={busy} onClick={() => { void perform({ op: 'discover' }) }}>{t(busy ? 'scanning' : 'discover')}</Button>
        <label htmlFor={`${fieldId}-device`}>{t('selectBox')}</label>
        <select id={`${fieldId}-device`} value={device} onChange={event => setDevice(event.target.value)}>
          <option value="">{t('selectBox')}</option>{devices.map(item => <option key={item.address} value={item.address}>{item.name}</option>)}
        </select>
        <label htmlFor={`${fieldId}-ssid`}>{t('wifiSsid')}</label>
        <Input id={`${fieldId}-ssid`} value={ssid} readOnly />
        <label htmlFor={`${fieldId}-wifi`}>{t('wifiPassword')}</label>
        <Input id={`${fieldId}-wifi`} type="password" value={wifiPassword} autoComplete="off" onChange={event => setWifiPassword(event.target.value)} />
        <span className={css.hint}>{t('wifiHint')}</span>
      </>}
      <div className={css.actions}>
        <Button type="button" variant="primary" disabled={busy || (mode === 'wired' ? !password : !device || !ssid)}
          onClick={() => { void perform(mode === 'wired'
            ? { op: 'pair-wired', host: '192.168.137.2', port: 22, password }
            : { op: 'pair-wireless', address: device, port: 22, wifi: { password: wifiPassword } }) }}>
          {t(busy ? 'connecting' : 'connect')}
        </Button>
        {connected && <Button type="button" disabled={busy} onClick={() => { void perform({ op: 'disconnect' }) }}>{t('disconnect')}</Button>}
      </div>
    </div>}
  </div>
}

/** Settings retains the normal navigation and close behavior of its host. */
export function QuantumBoxSection(props: EditorProps & PropsRuntime<'settings.section'>) {
  return <section className={css.section} aria-label={props.t('title')}><BoxEditor {...props} /></section>
}

/** The More menu never calls account APIs or opens a platform login URL. */
export function QuantumBoxMenu({ wide, openSettings, settingsShortcut, ...props }: EditorProps & PropsRuntime<'settings.launcher'>) {
  const [open, setOpen] = useState(false)
  const [dialog, setDialog] = useState(false)
  const trigger = useRef<HTMLButtonElement>(null)
  const { t } = props
  return <div className={menuCss.root}>
    <Menu open={open} side="top" portal autoFocus className={menuCss.anchor} listClassName={menuCss.signedOutMenu}
      anchor={<button ref={trigger} type="button" className={menuCss.trigger} data-collapsed={!wide} data-signed-out="true"
        aria-label={t('menu')} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(value => !value)}>
        <IconEllipsisOutlineMedium size={14} />{wide && <span className={menuCss.label}>{t('menu')}</span>}
      </button>}
      items={[
        { id: 'settings', label: t('settings'), icon: <IconSettingsOutlineMedium size={16} />,
          ...(settingsShortcut === undefined ? {} : { shortcut: settingsShortcut }) },
        { id: 'box', label: t('title'), icon: <IconLinkOutlineRegular size={16} /> },
      ]}
      onClose={() => setOpen(false)} onSelect={id => {
        setOpen(false)
        trigger.current?.focus()
        if (id === 'settings') openSettings()
        else setDialog(true)
      }} />
    <Modal open={dialog} title={t('title')} closeLabel={t('close')} onClose={() => setDialog(false)} className={css.dialog as string}>
      <BoxEditor {...props} />
    </Modal>
  </div>
}
