/** Non-secret connection intent; no transport is started by saving these fields. */
import z from '@deepseek-ai/schemastery'

/** Pending SSH target; trust and credentials are provisioned separately. */
export interface BoxSettings { boxHost?: string; boxSshPort?: number }

const OCTET = '(?:25[0-5]|2[0-4]\\d|1\\d{2}|[1-9]?\\d)'
const LABEL = '[a-z\\d](?:[a-z\\d-]{0,61}[a-z\\d])?'
const HOST = new RegExp(`^(?=.{1,253}$)(?:${OCTET}(?:\\.${OCTET}){3}|(?=.*[a-z-])${LABEL}(?:\\.${LABEL})*)$(?![\\s\\S])`, 'iu')

/** Accept IPv4 or DNS/mDNS names without URLs, credentials, paths or command text. */
export function validBoxHost(value: string): boolean {
  return HOST.test(value)
}

/** Host schema validates settings writes even when the client is bypassed. */
export const BoxSettingsFields = {
  boxHost: z.string().pattern(HOST),
  boxSshPort: z.number().min(1).max(65535).step(1),
}
