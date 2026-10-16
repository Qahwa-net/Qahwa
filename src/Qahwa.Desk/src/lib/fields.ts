import { formatDate, formatDateTime } from './display.ts'
import { formatDuration, placeLabel, starsLabel } from './fieldtypes.ts'
import type { DocField, FieldType } from '../api/types.ts'
import type { FilterOp } from './filters.ts'

export const isLayout = (type: FieldType) => type === 'SectionBreak' || type === 'ColumnBreak' || type === 'TabBreak'

/** Stored as a column on the document's own table (and therefore listable and filterable). */
export const hasColumn = (type: FieldType) => !isLayout(type) && type !== 'Table'

/** Types the Desk can edit today. Attachments, passwords and child tables arrive in later phases. */
export const isEditable = (type: FieldType) =>
  hasColumn(type) && type !== 'Password' && type !== 'Attach' && type !== 'AttachImage'

export type Kind = 'text' | 'number' | 'date' | 'datetime' | 'time' | 'check' | 'select' | 'link'

export function kindOf(type: FieldType): Kind {
  switch (type) {
    case 'Int': case 'Float': case 'Decimal': case 'Currency': case 'Percent': case 'Rating': case 'Duration':
      return 'number'
    case 'Date': return 'date'
    case 'Datetime': return 'datetime'
    case 'Time': return 'time'
    case 'Check': return 'check'
    case 'Select': return 'select'
    case 'Link': case 'DynamicLink': return 'link'
    default: return 'text'
  }
}

/** The framework's own columns, available for sorting and filtering on every DocType. */
export const STANDARD_FIELDS: DocField[] = [
  { name: 'name', type: 'Data', label: 'Name', required: false, unique: true, inListView: false, lowercase: false },
  { name: 'owner', type: 'Data', label: 'Created By', required: false, unique: false, inListView: false, lowercase: false },
  { name: 'creation', type: 'Datetime', label: 'Created On', required: false, unique: false, inListView: false, lowercase: false },
  { name: 'modified_by', type: 'Data', label: 'Modified By', required: false, unique: false, inListView: false, lowercase: false },
  { name: 'modified', type: 'Datetime', label: 'Last Modified', required: false, unique: false, inListView: false, lowercase: false },
]

export function humanize(name: string): string {
  const words = name.replace(/_/g, ' ').trim()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

export const labelOf = (field: DocField) => field.label || humanize(field.name)

export function selectOptions(field: DocField): string[] {
  return (field.options ?? '').split('\n').map((o) => o.trim()).filter(Boolean)
}

export function operatorsFor(type: FieldType): { op: FilterOp; label: string }[] {
  const compare = [
    { op: '=', label: 'equals' }, { op: '!=', label: 'not equals' },
    { op: '>', label: '>' }, { op: '<', label: '<' }, { op: '>=', label: '≥' }, { op: '<=', label: '≤' },
  ] as const
  const isSet = { op: 'is', label: 'is' } as const

  switch (kindOf(type)) {
    case 'number': case 'date': case 'datetime': case 'time':
      return [...compare, isSet]
    case 'check':
      return [{ op: '=', label: 'equals' }]
    case 'select':
      return [{ op: '=', label: 'equals' }, { op: '!=', label: 'not equals' }, isSet]
    case 'link':
      return [{ op: '=', label: 'equals' }, { op: '!=', label: 'not equals' }, { op: 'like', label: 'contains' }, isSet]
    default:
      return [
        { op: 'like', label: 'contains' }, { op: '=', label: 'equals' }, { op: '!=', label: 'not equals' }, isSet,
      ]
  }
}

const pad = (n: number) => String(n).padStart(2, '0')

/** Server value → the string or boolean an <input> works with. */
export function toInput(field: DocField, value: unknown): string | boolean {
  if (kindOf(field.type) === 'check') return value === true
  if (value === null || value === undefined) return ''

  if (field.type === 'Datetime' && typeof value === 'string') {
    const d = new Date(value)
    if (Number.isNaN(d.getTime())) return ''
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
  }
  if (field.type === 'Time' && typeof value === 'string') return value.slice(0, 8)
  return String(value)
}

/** Input value → what the API expects. Numbers stay strings; the server validates and reports field errors. */
export function fromInput(field: DocField, value: string | boolean): unknown {
  if (typeof value === 'boolean') return value
  if (field.type === 'Datetime' && value !== '') {
    const d = new Date(value)
    return Number.isNaN(d.getTime()) ? value : d.toISOString()
  }
  return value
}

export function formatCell(field: DocField, value: unknown): string {
  if (value === null || value === undefined || value === '') return ''
  switch (field.type) {
    case 'Check': return value ? '✓' : ''
    case 'Currency': case 'Decimal': case 'Float': case 'Percent':
      return typeof value === 'number' ? value.toLocaleString(undefined, { maximumFractionDigits: 9 }) : String(value)
    case 'Rating': return starsLabel(value)
    case 'Duration': return formatDuration(Number(value))
    case 'Geolocation': return placeLabel(value)
    case 'Signature': return '(signed)'
    case 'Date': return formatDate(value)
    case 'Datetime': return formatDateTime(value)
    default: return String(value)
  }
}
