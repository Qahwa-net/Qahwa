import type { DocField } from '../api/types.ts'

export const NO_VALUE = ''

export type CardDoc = Record<string, unknown> & { name: string }

/** The choices of a Select field, in the order they were defined. */
export function choicesOf(field: DocField): string[] {
  return (field.options ?? '').split('\n').map((o) => o.trim()).filter(Boolean)
}

/**
 * The board's columns: the field's choices in order, then any other value found in the documents (an old choice that was
 * removed), then "no value" last when some document has none (or always, so a card can be moved there).
 */
export function columnsOf(field: DocField, docs: CardDoc[]): string[] {
  const columns = choicesOf(field)
  const seen = new Set(columns)
  for (const doc of docs) {
    const value = valueOf(doc, field.name)
    if (value !== NO_VALUE && !seen.has(value)) {
      seen.add(value)
      columns.push(value)
    }
  }

  return [...columns, NO_VALUE]
}

export const valueOf = (doc: CardDoc, field: string): string => {
  const raw = doc[field]
  return raw === null || raw === undefined ? NO_VALUE : String(raw)
}

/** Puts every document in the column of its value. Every column exists, even when empty. */
export function groupCards(docs: CardDoc[], field: string, columns: string[]): Record<string, CardDoc[]> {
  const groups: Record<string, CardDoc[]> = Object.fromEntries(columns.map((c) => [c, []]))
  for (const doc of docs) {
    const value = valueOf(doc, field)
    ;(groups[value] ??= []).push(doc)
  }

  return groups
}

/** The documents with one card's value changed: used to show a move at once, before the server has answered. */
export function withMoved(docs: CardDoc[], name: string, field: string, to: string): CardDoc[] {
  return docs.map((d) => (d.name === name ? { ...d, [field]: to === NO_VALUE ? null : to } : d))
}

/** The fields a board can be built on: Select fields (their choices are the columns). */
export const boardFields = (fields: DocField[]): DocField[] => fields.filter((f) => f.type === 'Select' && choicesOf(f).length > 0 && !f.hidden)

export const columnLabel = (value: string): string => (value === NO_VALUE ? 'No value' : value)
