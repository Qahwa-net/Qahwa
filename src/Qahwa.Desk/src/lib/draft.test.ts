import { describe, expect, it } from 'vitest'
import { DRAFT_MAX_AGE_MS, clearDraft, draftKey, loadDraft, saveDraft } from './draft.ts'

const memory = () => {
  const map = new Map<string, string>()
  return { getItem: (k: string) => map.get(k) ?? null, setItem: (k: string, v: string) => void map.set(k, v), removeItem: (k: string) => void map.delete(k), map }
}

describe('drafts', () => {
  it('keeps what was typed, leaves out secrets and returns it for the same version', () => {
    const store = memory()
    const key = draftKey('Ticket', 'TK-1')
    saveDraft(store, key, { values: { subject: 'Hi', pin: '1234' }, tables: { lines: [] }, modified: 'v1', at: 1000 }, new Set(['pin']))
    expect(store.map.get(key)).not.toContain('1234')
    const back = loadDraft<Record<string, unknown>, unknown>(store, key, 'v1', 2000)
    expect(back?.values).toEqual({ subject: 'Hi' })
  })

  it('drops a draft of an older version, an old draft and broken data', () => {
    const store = memory()
    const key = draftKey('Ticket', null)
    saveDraft(store, key, { values: { a: 1 }, tables: {}, modified: '', at: 1000 }, new Set())
    expect(loadDraft(store, key, 'other', 2000)).toBeNull()
    expect(store.map.has(key)).toBe(false)
    saveDraft(store, key, { values: { a: 1 }, tables: {}, modified: '', at: 1000 }, new Set())
    expect(loadDraft(store, key, '', 1000 + DRAFT_MAX_AGE_MS + 1)).toBeNull()
    store.setItem(key, '{not json')
    expect(loadDraft(store, key, '', 2000)).toBeNull()
    store.setItem(key, '"text"')
    expect(loadDraft(store, key, '', 2000)).toBeNull()
  })

  it('survives storage that fails', () => {
    const broken = { getItem: () => { throw new Error('no') }, setItem: () => { throw new Error('no') }, removeItem: () => { throw new Error('no') } }
    expect(() => saveDraft(broken, 'k', { values: {}, tables: {}, modified: '', at: 1 }, new Set())).not.toThrow()
    expect(loadDraft(broken, 'k', '', 2)).toBeNull()
    expect(() => clearDraft(broken, 'k')).not.toThrow()
  })

  it('keeps new documents and saved ones apart', () => {
    expect(draftKey('Ticket', null)).not.toBe(draftKey('Ticket', 'TK-1'))
  })
})
