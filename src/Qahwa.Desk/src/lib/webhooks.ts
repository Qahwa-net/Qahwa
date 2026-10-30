import type { Webhook, WebhookEvent, WebhookSet } from '../api/types.ts'

export const EVENTS: { value: WebhookEvent; label: string }[] = [
  { value: 'New', label: 'A document is created' },
  { value: 'Update', label: 'A document is changed' },
  { value: 'Submit', label: 'A document is submitted' },
  { value: 'Cancel', label: 'A document is cancelled' },
  { value: 'Delete', label: 'A document is deleted' },
]

export const newHook = (existing: Webhook[]): Webhook => {
  let n = existing.length + 1
  while (existing.some((h) => h.name === `Webhook ${n}`)) n++
  return { name: `Webhook ${n}`, active: true, event: 'New', condition: '', url: 'https://', secret: '', secretSet: false, fields: [], headers: [] }
}

/** A random secret in the browser, for people who do not want to invent one: 32 random bytes as hex. */
export function randomSecret(random: (n: number) => Uint8Array = (n) => crypto.getRandomValues(new Uint8Array(n))): string {
  return [...random(32)].map((b) => b.toString(16).padStart(2, '0')).join('')
}

/** What to send: blank conditions removed, and a secret only when the person typed one (blank keeps the stored secret). */
export function toSet(draft: WebhookSet): WebhookSet {
  return {
    ...draft,
    hooks: draft.hooks.map((h) => ({
      ...h,
      name: h.name.trim(),
      url: h.url.trim(),
      condition: h.condition?.trim() ? h.condition.trim() : null,
      secret: h.secret ? h.secret : '',
      headers: (h.headers ?? []).filter((x) => x.name.trim() !== '').map((x) => ({ name: x.name.trim(), value: x.value ? x.value : '', valueSet: x.valueSet })),
    })),
  }
}

/** A quick check of an address before saving; the server decides for real. */
export function urlHint(url: string): string | null {
  const text = url.trim()
  if (text === '' || text === 'https://') return null
  if (!/^https:\/\//i.test(text)) return 'Use an https:// address.'
  if (/^https:\/\/([^/]*@)/i.test(text)) return 'The address cannot contain a user name or password.'
  if (/^https:\/\/(localhost|127\.|10\.|192\.168\.|169\.254\.|\[::1\])/i.test(text)) return 'Local and private addresses cannot be used.'
  return null
}
