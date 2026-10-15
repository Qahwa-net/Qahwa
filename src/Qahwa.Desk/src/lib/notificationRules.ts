import type { NotificationEvent, NotificationRule, NotificationRuleSet } from '../api/types.ts'

export const EVENTS: { value: NotificationEvent; label: string; help: string }[] = [
  { value: 'New', label: 'A document is created', help: 'Right after someone creates one.' },
  { value: 'Update', label: 'A document is changed', help: 'Whenever a saved change alters something (saving without changes does nothing).' },
  { value: 'ValueChange', label: 'A field changes', help: 'When one chosen field gets a different value.' },
  { value: 'Submit', label: 'A document is submitted', help: 'When it goes from draft to submitted.' },
  { value: 'Cancel', label: 'A document is cancelled', help: 'When a submitted document is cancelled.' },
  { value: 'DaysBefore', label: 'Days before a date', help: 'Every day the date is this many days away (checked about once an hour).' },
  { value: 'DaysAfter', label: 'Days after a date', help: 'Every day the date was this many days ago.' },
]

export const isTimeBased = (event: NotificationEvent) => event === 'DaysBefore' || event === 'DaysAfter'

export const newRule = (existing: NotificationRule[]): NotificationRule => {
  let n = existing.length + 1
  while (existing.some((r) => r.name === `Rule ${n}`)) n++
  return {
    name: `Rule ${n}`, active: true, event: 'New', field: null, dateField: null, days: null, condition: '',
    users: [], roles: [], userFields: [], notifyOwner: false, notifyFollowers: false, email: false, message: '',
  }
}

/** What to send: each event keeps only the settings it uses, and blank texts become null. */
export function toSet(draft: NotificationRuleSet): NotificationRuleSet {
  const blank = (text?: string | null) => (text && text.trim() ? text.trim() : null)
  return {
    ...draft,
    rules: draft.rules.map((r) => ({
      ...r,
      field: r.event === 'ValueChange' ? blank(r.field) : null,
      dateField: isTimeBased(r.event) ? blank(r.dateField) : null,
      days: isTimeBased(r.event) ? r.days ?? null : null,
      condition: blank(r.condition),
      message: r.message.trim(),
    })),
  }
}

/** Adds <c>{{field}}</c> to the end of a message, with a space before it when needed. */
export function withPlaceholder(message: string, name: string): string {
  const gap = message === '' || /\s$/.test(message) ? '' : ' '
  return `${message}${gap}{{${name}}}`
}

/** The names a message uses that are not in the list of known ones (so the page can warn before saving). */
export function unknownPlaceholders(message: string, known: string[]): string[] {
  const names = [...message.matchAll(/\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}/g)].map((m) => m[1]!)
  return [...new Set(names)].filter((n) => !known.includes(n))
}

export const SPECIAL_NAMES = ['name', 'doctype', 'user']
