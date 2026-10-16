import type { DocField } from '../api/types.ts'
import { hasColumn } from './fields.ts'

export interface ReportFilterDraft {
  field: string
  op: string
  value: string
}

export interface ReportAggregateDraft {
  function: string
  field: string
}

/** What the builder edits. Sent to the server as a definition by <see cref="toDefinition"/>. */
export interface ReportDraft {
  title: string
  isPublic: boolean
  docType: string
  mode: 'list' | 'summary'
  columns: string[]
  filters: ReportFilterDraft[]
  orderBy: string
  descending: boolean
  limit: number
  groupBy: string
  aggregates: ReportAggregateDraft[]
}

export const OPERATORS: { value: string; label: string }[] = [
  { value: '=', label: 'is' },
  { value: '!=', label: 'is not' },
  { value: '>', label: 'is more than' },
  { value: '<', label: 'is less than' },
  { value: '>=', label: 'is at least' },
  { value: '<=', label: 'is at most' },
  { value: 'like', label: 'contains' },
  { value: 'in', label: 'is one of (comma separated)' },
  { value: 'is', label: 'is set / not set' },
]

export const FUNCTIONS: { value: string; label: string }[] = [
  { value: 'count', label: 'Count' },
  { value: 'sum', label: 'Total' },
  { value: 'avg', label: 'Average' },
  { value: 'min', label: 'Smallest' },
  { value: 'max', label: 'Largest' },
]

const NUMERIC = new Set(['Int', 'Float', 'Decimal', 'Currency', 'Percent', 'Rating', 'Duration'])

export const isNumeric = (field: DocField): boolean => NUMERIC.has(field.type)

/** Fields a report can show, group by or filter on: real columns, never passwords. */
export const reportFields = (fields: DocField[]): DocField[] => fields.filter((f) => hasColumn(f.type) && f.type !== 'Password')

export const newDraft = (docType = ''): ReportDraft => ({
  title: '', isPublic: false, docType, mode: 'list', columns: [], filters: [], orderBy: '', descending: true, limit: 200, groupBy: '',
  aggregates: [{ function: 'count', field: '' }],
})

/** A filter's value as the server wants it: a list for "is one of", plain text otherwise, and "set"/"not set" for "is". */
export function filterValue(filter: ReportFilterDraft): unknown {
  if (filter.op === 'in') return filter.value.split(',').map((v) => v.trim()).filter((v) => v.length > 0)
  return filter.value
}

export function toDefinition(draft: ReportDraft): Record<string, unknown> {
  const filters = draft.filters.filter((f) => f.field && (f.op === 'is' || f.value.trim() !== '')).map((f) => ({ field: f.field, op: f.op, value: filterValue(f) }))
  const common = { docType: draft.docType, filters, orderBy: draft.orderBy || null, descending: draft.descending, limit: draft.limit }
  return draft.mode === 'summary'
    ? { ...common, columns: [], groupBy: draft.groupBy, aggregates: draft.aggregates.map((a) => ({ function: a.function, field: a.function === 'count' ? null : a.field })) }
    : { ...common, columns: draft.columns, groupBy: null, aggregates: [] }
}

/** Turns a saved definition back into something the builder can edit. */
export function fromDefinition(title: string, isPublic: boolean, definition: Record<string, unknown>): ReportDraft {
  const filters = ((definition.filters as { field: string; op: string; value: unknown }[] | undefined) ?? []).map((f) => ({
    field: f.field, op: f.op, value: Array.isArray(f.value) ? f.value.join(', ') : f.value == null ? '' : String(f.value),
  }))
  const aggregates = ((definition.aggregates as { function: string; field: string | null }[] | undefined) ?? []).map((a) => ({ function: a.function, field: a.field ?? '' }))
  return {
    title, isPublic, docType: String(definition.docType ?? ''), mode: definition.groupBy ? 'summary' : 'list',
    columns: (definition.columns as string[] | undefined) ?? [], filters, orderBy: String(definition.orderBy ?? ''), descending: definition.descending !== false,
    limit: Number(definition.limit ?? 200), groupBy: String(definition.groupBy ?? ''), aggregates: aggregates.length > 0 ? aggregates : [{ function: 'count', field: '' }],
  }
}

/** Why the draft cannot be saved or run yet, in words (the server checks everything again). */
export function draftProblem(draft: ReportDraft): string | null {
  if (!draft.docType) return 'Choose a DocType.'
  if (draft.mode === 'list' && draft.columns.length === 0) return 'Choose at least one column to show.'
  if (draft.mode === 'summary') {
    if (!draft.groupBy) return 'Choose a field to group by.'
    if (draft.aggregates.length === 0) return 'Choose at least one number to show.'
    if (draft.aggregates.some((a) => a.function !== 'count' && !a.field)) return 'Choose a numeric field for each number.'
  }
  return null
}

export const toggle = (list: string[], item: string): string[] => (list.includes(item) ? list.filter((x) => x !== item) : [...list, item])
