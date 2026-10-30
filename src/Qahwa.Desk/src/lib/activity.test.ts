import { describe, expect, it } from 'vitest'
import type { ActivityItem } from '../api/types.ts'
import { describeChanges, describeVersion, insertMention, mentionAt, showValue, splitMentions, timeAgo, versionHeadline } from './activity.ts'

const item = (event: string, data: unknown = null): ActivityItem => ({
  kind: 'version', id: 'v1', event, user: 'a@x.com', userName: 'Ann', at: '2026-01-01T00:00:00Z', data: data as ActivityItem['data'],
  content: null, edited: false, canEdit: false,
})

describe('showValue', () => {
  it('says empty, yes and no, and shortens long text', () => {
    expect(showValue(null)).toBe('empty')
    expect(showValue('')).toBe('empty')
    expect(showValue(true)).toBe('yes')
    expect(showValue(false)).toBe('no')
    expect(showValue(0)).toBe('0')
    expect(showValue('x'.repeat(200))).toHaveLength(81)
  })
})

describe('describeVersion', () => {
  const labels = { status: 'Status', total: 'Total' }

  it('lists changed fields by label', () => {
    expect(describeChanges([['status', 'Open', 'Won'], ['other', null, 5]], labels)).toEqual(['Status: Open → Won', 'other: empty → 5'])
    expect(describeVersion(item('Update', { changed: [['total', 5, 6]] }), labels)).toEqual(['Total: 5 → 6'])
  })

  it('describes document events in words', () => {
    expect(describeVersion(item('Insert'), labels)).toEqual(['created this'])
    expect(describeVersion(item('Submit', { changed: [['docstatus', 0, 1]] }), labels)).toEqual(['submitted this'])
    expect(describeVersion(item('Rename', { from: 'A', to: 'B' }), labels)).toEqual(['renamed this from A to B'])
    expect(describeVersion(item('Merge', { from: 'A', to: 'B' }), labels)).toEqual(['merged A into B'])
    expect(versionHeadline(item('Update'))).toBe('changed')
  })

  it('describes table rows added, removed and changed', () => {
    const lines = describeVersion(item('Update', { tables: { lines: {
      added: [{ item: 'pad', qty: 9 }], removed: [{ name: 'r1', item: 'ink' }], changed: [{ name: 'r2', changed: [['qty', 1, 5]] }],
    } } }), { lines: 'Items', qty: 'Qty' })

    expect(lines).toEqual(['Items: added pad · 9', 'Items: removed ink', 'Items: row changed (Qty: 1 → 5)'])
  })

  it('shows nothing for data it does not understand', () => {
    expect(describeVersion(item('Update', 'garbage'), {})).toEqual([])
    expect(describeVersion(item('Update'), {})).toEqual([])
  })
})

describe('timeAgo', () => {
  const now = new Date('2026-01-01T12:00:00Z')
  it('is relative', () => {
    expect(timeAgo('2026-01-01T11:59:50Z', now)).toBe('just now')
    expect(timeAgo('2026-01-01T11:55:00Z', now)).toBe('5 minutes ago')
    expect(timeAgo('2026-01-01T11:00:00Z', now)).toBe('1 hour ago')
    expect(timeAgo('2025-12-30T12:00:00Z', now)).toBe('2 days ago')
    expect(timeAgo('nonsense', now)).toBe('')
  })
})

describe('splitMentions', () => {
  it('marks @mentions and keeps all other text as text', () => {
    expect(splitMentions('Hi @ann.lee@example.com please <b>look</b> @bob')).toEqual([
      { text: 'Hi ', mention: false }, { text: '@ann.lee@example.com', mention: true },
      { text: ' please <b>look</b> ', mention: false }, { text: '@bob', mention: true },
    ])
  })

  it('does not treat the middle of an email address as a mention', () => {
    expect(splitMentions('mail ann@example.com now')).toEqual([{ text: 'mail ann@example.com now', mention: false }])
    expect(splitMentions('')).toEqual([])
  })
})

describe('mentionAt and insertMention', () => {
  it('finds the @word being typed at the caret', () => {
    expect(mentionAt('hello @al', 9)).toEqual({ start: 6, query: 'al' })
    expect(mentionAt('@', 1)).toEqual({ start: 0, query: '' })
    expect(mentionAt('hi @al and more', 6)).toEqual({ start: 3, query: 'al' })
  })

  it('ignores an @ inside a word or after the caret has moved on', () => {
    expect(mentionAt('mail me at a@b', 14)).toBeNull()
    expect(mentionAt('@al done', 8)).toBeNull()
    expect(mentionAt('no mention', 10)).toBeNull()
  })

  it('replaces the typed word with the chosen @email and a space', () => {
    expect(insertMention('hi @al and more', 6, 'alice@example.com')).toEqual({ text: 'hi @alice@example.com and more', caret: 21 })
    expect(insertMention('hi @al', 6, 'alice@example.com')).toEqual({ text: 'hi @alice@example.com ', caret: 22 })
    expect(insertMention('no mention', 10, 'a@b.co')).toBeNull()
  })
})
