/** What the server says happened: where, never what (the Desk asks for the data with its own requests). */
export interface LiveEvent {
  type: 'notification' | 'doc'
  doctype?: string
  name?: string
  /** For a document event, the person who changed it. */
  user?: string
  deleted?: boolean
}

/** The address of the event stream: the person's own, or the changes to one document they have open. */
export function realtimeUrl(doctype?: string, name?: string): string {
  if (doctype === undefined || name === undefined) return '/api/realtime'
  return `/api/realtime?doctype=${encodeURIComponent(doctype)}&name=${encodeURIComponent(name)}`
}

/** One message of the stream, or null when it is not one the Desk understands (nothing a server sends can make this throw). */
export function parseEvent(type: string, data: string): LiveEvent | null {
  if (type !== 'notification' && type !== 'doc') return null
  try {
    const value: unknown = JSON.parse(data)
    if (typeof value !== 'object' || value === null) return null
    const raw = value as Record<string, unknown>
    const text = (v: unknown) => (typeof v === 'string' ? v : undefined)
    return { type, doctype: text(raw.doctype), name: text(raw.name), user: text(raw.user), deleted: raw.deleted === true }
  } catch {
    return null
  }
}

export type LiveAction = { kind: 'ignore' } | { kind: 'refresh'; by: string } | { kind: 'warn'; by: string } | { kind: 'deleted'; by: string }

/**
 * What a form should do about a change to its document: nothing when the person made it themselves, show the new version when they
 * have no unsaved edits, and otherwise warn (their edits are never replaced behind their back).
 */
export function liveAction(event: LiveEvent, me: string | undefined, dirty: boolean): LiveAction {
  if (event.type !== 'doc' || event.user === me) return { kind: 'ignore' }
  const by = event.user ?? 'Someone'
  if (event.deleted) return { kind: 'deleted', by }
  return dirty ? { kind: 'warn', by } : { kind: 'refresh', by }
}
