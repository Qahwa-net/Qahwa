/**
 * Unsaved form changes kept in this browser (ADR-109), so a closed tab or a lost connection does not lose typing.
 * Nothing leaves the browser. Secrets are never kept (the caller names the fields to leave out), a draft is ignored when the document
 * has been saved by someone else since (its "modified" differs) or when it is older than a week, and it is removed once the form is saved.
 */
export interface Draft<V, T> {
  values: V
  tables: T
  /** The document's "modified" when the draft was made; empty for a new document. */
  modified: string
  at: number
}

export const DRAFT_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000
const PREFIX = 'qahwa-draft:'

export const draftKey = (doctype: string, name: string | null): string => `${PREFIX}${doctype}:${name ?? '(new)'}`

type Store = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

export function saveDraft<V extends Record<string, unknown>, T>(store: Store, key: string, draft: Draft<V, T>, leaveOut: ReadonlySet<string>): void {
  try {
    const values = Object.fromEntries(Object.entries(draft.values).filter(([name]) => !leaveOut.has(name)))
    store.setItem(key, JSON.stringify({ ...draft, values }))
  } catch {
    // Storage may be full or blocked (a private window): keeping a draft is a convenience only.
  }
}

export function loadDraft<V, T>(store: Store, key: string, modified: string, now: number): Draft<V, T> | null {
  try {
    const text = store.getItem(key)
    if (!text) return null
    const draft = JSON.parse(text) as Draft<V, T>
    const ok = draft && typeof draft === 'object' && typeof draft.at === 'number' && draft.values && typeof draft.values === 'object' &&
      draft.modified === modified && now - draft.at >= 0 && now - draft.at <= DRAFT_MAX_AGE_MS
    if (!ok) {
      store.removeItem(key)
      return null
    }
    return draft
  } catch {
    try { store.removeItem(key) } catch { /* nothing to do */ }
    return null
  }
}

export function clearDraft(store: Store, key: string): void {
  try { store.removeItem(key) } catch { /* nothing to do */ }
}
