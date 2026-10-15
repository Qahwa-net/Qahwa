import type { DashboardItem, DocField, TileFilter, TileFunction } from '../api/types.ts'
import { hasColumn } from './fields.ts'

export const FUNCTIONS: { value: TileFunction; label: string }[] = [
  { value: 'count', label: 'Count' },
  { value: 'sum', label: 'Sum' },
  { value: 'avg', label: 'Average' },
  { value: 'min', label: 'Smallest' },
  { value: 'max', label: 'Largest' },
]

export const OPERATORS = ['=', '!=', '>', '<', '>=', '<=', 'like', 'is'] as const

const NUMERIC = new Set(['Int', 'Float', 'Decimal', 'Currency', 'Percent'])
export const numericFields = (fields: DocField[]) => fields.filter((f) => NUMERIC.has(f.type))
export const dateFields = (fields: DocField[]) => fields.filter((f) => f.type === 'Date' || f.type === 'Datetime')
export const groupableFields = (fields: DocField[]) => fields.filter((f) => hasColumn(f.type) && f.type !== 'Password')

export const newItem = (type: DashboardItem['type'] = 'Number', docType = ''): DashboardItem => ({
  id: '', type, title: '', docType, function: 'count', valueField: null, groupBy: null, period: null, chartType: 'bar',
  columns: [], orderBy: null, descending: true, limit: 10, filters: [], color: 'blue',
})

/** What to send: each type keeps only the settings it uses, and filters without a field are dropped. */
export function toItem(item: DashboardItem): DashboardItem {
  const chart = item.type === 'Chart'
  const summary = item.type !== 'List'
  return {
    ...item,
    title: item.title.trim(),
    function: summary ? item.function : 'count',
    valueField: summary && item.function !== 'count' ? item.valueField ?? null : null,
    groupBy: chart ? item.groupBy ?? null : null,
    period: chart && item.period ? item.period : null,
    columns: item.type === 'List' ? item.columns : [],
    orderBy: item.type === 'List' ? item.orderBy || null : null,
    filters: item.filters.filter((f) => f.field).map((f) => ({ ...f, value: f.op === 'is' ? f.value || 'set' : f.value ?? '' })),
  }
}

/** The title shown when a tile has none: "Sum of amount, by region". */
export function tileTitle(item: DashboardItem): string {
  if (item.title.trim()) return item.title.trim()
  const what = item.function === 'count' ? `${item.docType} count` : `${FUNCTIONS.find((f) => f.value === item.function)?.label ?? item.function} of ${item.valueField ?? '…'}`
  if (item.type === 'Chart') return `${what} by ${item.groupBy ?? '…'}`
  if (item.type === 'List') return `${item.docType} list`
  return what
}

/** Filters as the server wants them: numbers stay numbers, "true"/"false" become booleans. */
export function filterValue(filter: TileFilter, fieldType?: string): TileFilter['value'] {
  const raw = filter.value
  if (typeof raw !== 'string') return raw
  if (fieldType && NUMERIC.has(fieldType) && raw.trim() !== '' && !Number.isNaN(Number(raw))) return Number(raw)
  if (fieldType === 'Check') return raw === 'true' || raw === '1'
  return raw
}
