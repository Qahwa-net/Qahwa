import type { ActivityItem } from '../api/types.ts'

type Entry = [string, unknown, unknown]

const SHORT = 80

/** A value as the history shows it: short, readable, with nothing shown as "empty". */
export function showValue(value: unknown): string {
  if (value === null || value === undefined || value === '') return 'empty'
  if (value === true) return 'yes'
  if (value === false) return 'no'
  const text = String(value)
  return text.length > SHORT ? `${text.slice(0, SHORT)}…` : text
}

const asEntries = (value: unknown): Entry[] => (Array.isArray(value) ? (value as Entry[]) : [])

/** "Status: Open → Won" for each changed field, using the field's label. */
export function describeChanges(entries: Entry[], labels: Record<string, string>): string[] {
  return entries.map(([field, before, after]) => `${labels[field] ?? field}: ${showValue(before)} → ${showValue(after)}`)
}

const rowSummary = (row: Record<string, unknown>) =>
  Object.entries(row).filter(([key]) => !['name', 'idx'].includes(key)).slice(0, 4).map(([, v]) => showValue(v)).join(' · ')

/** What an entry of the updates log says happened, as short lines of plain text. */
export function describeVersion(item: ActivityItem, labels: Record<string, string>): string[] {
  const data = (item.data ?? {}) as Record<string, unknown>
  switch (item.event) {
    case 'Insert': return ['created this']
    case 'Delete': return ['deleted this']
    case 'Rename': return [`renamed this from ${showValue(data.from)} to ${showValue(data.to)}`]
    case 'Merge': return [`merged ${showValue(data.from)} into ${showValue(data.to)}`]
    case 'Submit': return ['submitted this']
    case 'Cancel': return ['cancelled this']
  }

  const lines = describeChanges(asEntries(data.changed), labels)
  const tables = (data.tables ?? {}) as Record<string, { added?: Record<string, unknown>[]; removed?: Record<string, unknown>[]; changed?: { name: string; changed: Entry[] }[] }>
  for (const [field, diff] of Object.entries(tables)) {
    const label = labels[field] ?? field
    for (const row of diff.added ?? []) lines.push(`${label}: added ${rowSummary(row)}`)
    for (const row of diff.removed ?? []) lines.push(`${label}: removed ${rowSummary(row)}`)
    for (const row of diff.changed ?? []) lines.push(`${label}: row changed (${describeChanges(row.changed, labels).join(', ')})`)
  }
  return lines
}

/** Submit and cancel also record the status change; the headline says it already. */
export function versionHeadline(item: ActivityItem): string {
  return { Insert: 'created', Update: 'changed', Submit: 'submitted', Cancel: 'cancelled', Rename: 'renamed', Merge: 'merged', Delete: 'deleted' }[item.event] ?? item.event.toLowerCase()
}

const UNITS: [number, string][] = [[60, 'minute'], [60, 'hour'], [24, 'day'], [30, 'month'], [12, 'year']]

/** "5 minutes ago", "just now". */
export function timeAgo(iso: string, now: Date = new Date()): string {
  let seconds = Math.round((now.getTime() - new Date(iso).getTime()) / 1000)
  if (!Number.isFinite(seconds)) return ''
  if (seconds < 45) return 'just now'
  let unit = 'second'
  for (const [size, next] of UNITS) {
    if (seconds < size) break
    seconds = Math.round(seconds / size)
    unit = next
  }
  return `${seconds} ${unit}${seconds === 1 ? '' : 's'} ago`
}

export interface Piece {
  text: string
  mention: boolean
}

/** Splits a comment into plain text and @mentions, so the Desk can highlight mentions without ever treating text as markup. */
export function splitMentions(text: string): Piece[] {
  const pieces: Piece[] = []
  let last = 0
  for (const match of text.matchAll(/(^|\s)(@[\w.+-]+(?:@[\w.-]+)?)/g)) {
    const start = (match.index ?? 0) + match[1]!.length
    if (start > last) pieces.push({ text: text.slice(last, start), mention: false })
    pieces.push({ text: match[2]!, mention: true })
    last = start + match[2]!.length
  }
  if (last < text.length) pieces.push({ text: text.slice(last), mention: false })
  return pieces
}
