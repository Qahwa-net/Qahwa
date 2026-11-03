import type { DocField, DocTypeMeta, FieldError } from '../api/types.ts'
import { fromInput, hasColumn, isEditable, toInput } from './fields.ts'

export type CellValue = string | boolean

/** One row of a child table while it is being edited. `name` is the server's id for rows that are already saved. */
export interface TableRow {
  key: string
  name?: string
  values: Record<string, CellValue>
  /** The tables inside this row (a child DocType may hold tables one level deep), by field name. */
  tables?: Record<string, TableRow[]>
}

let counter = 0
const nextKey = () => `r${++counter}`

/** The fields of a child DocType a user can type into. */
export const editableFields = (child: DocTypeMeta): DocField[] =>
  child.fields.filter((f) => isEditable(f.type))

/** Grid columns: the fields marked "show in list", or the first few if none are. Hidden fields never get a column. */
export function gridColumns(child: DocTypeMeta): DocField[] {
  const fields = editableFields(child).filter((f) => !f.hidden)
  const chosen = fields.filter((f) => f.inListView)
  return (chosen.length > 0 ? chosen : fields.slice(0, 4)).slice(0, 8)
}

function defaultValue(field: DocField): CellValue {
  if (field.type === 'Check') return field.default === '1' || field.default === 'true'
  return field.default ?? ''
}

export function emptyRow(child: DocTypeMeta): TableRow {
  const values: Record<string, CellValue> = {}
  for (const field of editableFields(child)) values[field.name] = defaultValue(field)
  return { key: nextKey(), values }
}

/** The Table fields a child DocType holds itself, with their child DocTypes (those the server sent along). */
export function nestedFieldsOf(child: DocTypeMeta, children?: Record<string, DocTypeMeta>): { field: DocField; child: DocTypeMeta }[] {
  if (!children) return []
  return child.fields
    .filter((f) => f.type === 'Table')
    .flatMap((field) => {
      const inner = field.options ? children[field.options] : undefined
      return inner ? [{ field, child: inner }] : []
    })
}

/** Rows as saved on the server become rows the grid can edit (with the rows inside them, when `children` says what those are). */
export function rowsFromDoc(child: DocTypeMeta, rows: unknown, children?: Record<string, DocTypeMeta>): TableRow[] {
  if (!Array.isArray(rows)) return []
  return rows.map((raw) => {
    const row = raw as Record<string, unknown>
    const values: Record<string, CellValue> = {}
    for (const field of editableFields(child)) values[field.name] = toInput(field, row[field.name])
    const nested = nestedFieldsOf(child, children)
    const tables = nested.length > 0 ? Object.fromEntries(nested.map(({ field, child: inner }) => [field.name, rowsFromDoc(inner, row[field.name], children)])) : undefined
    return { key: nextKey(), name: String(row.name), values, ...(tables ? { tables } : {}) }
  })
}

/** What the API expects for a Table field: rows in display order, saved rows identified by their name, tables inside rows nested in them. */
export function rowsToPayload(child: DocTypeMeta, rows: TableRow[], children?: Record<string, DocTypeMeta>): Record<string, unknown>[] {
  return rows.map((row) => {
    const payload: Record<string, unknown> = row.name ? { name: row.name } : {}
    for (const field of editableFields(child)) {
      const value = row.values[field.name]
      if (value !== undefined) payload[field.name] = fromInput(field, value)
    }
    for (const { field, child: inner } of nestedFieldsOf(child, children)) {
      const nested = row.tables?.[field.name]
      if (nested !== undefined) payload[field.name] = rowsToPayload(inner, nested, children)
    }
    return payload
  })
}

/** True when the rows differ from what was loaded (added, removed, reordered or edited, here or in a table inside a row). */
export function rowsChanged(before: TableRow[], after: TableRow[]): boolean {
  if (before.length !== after.length) return true
  return before.some((row, i) => {
    const other = after[i]!
    if (row.name !== other.name) return true
    if (Object.keys({ ...row.values, ...other.values }).some((k) => row.values[k] !== other.values[k])) return true
    return Object.keys({ ...row.tables, ...other.tables }).some((k) => rowsChanged(row.tables?.[k] ?? [], other.tables?.[k] ?? []))
  })
}

/** The errors that belong to the tables inside one row, with the row's part of the path taken off: `lines[1].parts[0].qty` becomes `parts[0].qty`. */
export function nestedErrors(errors: Record<string, string>, tableField: string, rowIndex: number): Record<string, string> {
  const prefix = `${tableField}[${rowIndex}].`
  return Object.fromEntries(Object.entries(errors).filter(([path]) => path.startsWith(prefix)).map(([path, message]) => [path.slice(prefix.length), message]))
}

/** The row with the rows of one of its inner tables replaced. */
export const withNested = (row: TableRow, field: string, rows: TableRow[]): TableRow => ({ ...row, tables: { ...row.tables, [field]: rows } })

/** Errors for one Table field, grouped by row: `items[2].qty` becomes row 2, field "qty". */
export function rowErrors(errors: Record<string, string>, tableField: string): { table?: string; rows: Map<number, Record<string, string>> } {
  const rows = new Map<number, Record<string, string>>()
  let table: string | undefined
  const prefix = `${tableField}[`

  for (const [path, message] of Object.entries(errors)) {
    if (!message) continue
    if (path === tableField) {
      table = message
      continue
    }
    if (!path.startsWith(prefix)) continue

    const match = /^\[(\d+)\](?:\.(.+))?$/.exec(path.slice(tableField.length))
    if (!match) continue
    const index = Number(match[1])
    rows.set(index, { ...(rows.get(index) ?? {}), [match[2] ?? '']: message })
  }

  return { table, rows }
}

export function moveRow(rows: TableRow[], from: number, to: number): TableRow[] {
  if (to < 0 || to >= rows.length || from === to) return rows
  const copy = [...rows]
  const [row] = copy.splice(from, 1)
  copy.splice(to, 0, row!)
  return copy
}

/** Table fields a form has to draw, with the child DocType the server sent along. */
export function tableFieldsOf(meta: DocTypeMeta, children: Record<string, DocTypeMeta>): { field: DocField; child: DocTypeMeta }[] {
  return meta.fields
    .filter((f) => f.type === 'Table' && !hasColumn(f.type))
    .flatMap((field) => {
      const child = field.options ? children[field.options] : undefined
      return child ? [{ field, child }] : []
    })
}

export const rowErrorList = (errors: Record<string, string>, tableField: string, fields: DocField[]): string[] => {
  const { rows } = rowErrors(errors, tableField)
  const label = (name: string) => fields.find((f) => f.name === name)?.label ?? name
  return [...rows.entries()].flatMap(([index, byField]) =>
    Object.entries(byField).map(([name, message]) => `Row ${index + 1}${name ? `, ${label(name)}` : ''}: ${message}`))
}

export type { FieldError }

/** The one Link field a multi-select child table is made of (null when the child does not fit; the server checks the same rule). */
export function chipField(child: DocTypeMeta): DocField | null {
  const data = child.fields.filter((f) => hasColumn(f.type))
  const links = data.filter((f) => f.type === 'Link')
  return links.length === 1 && data.every((f) => f === links[0] || !f.required) ? links[0]! : null
}

/** The documents chosen, in row order. */
export const chipValues = (rows: TableRow[], field: DocField): string[] =>
  rows.map((r) => r.values[field.name]).filter((v): v is string => typeof v === 'string' && v !== '')

/** The rows with one more chosen document (nothing changes when it is already there). */
export function addChip(rows: TableRow[], child: DocTypeMeta, field: DocField, value: string, makeRow?: () => TableRow): TableRow[] {
  if (chipValues(rows, field).includes(value)) return rows
  const row = makeRow ? makeRow() : emptyRow(child)
  return [...rows, { ...row, values: { ...row.values, [field.name]: value } }]
}

/** The rows without the document (every row that holds it). */
export const removeChip = (rows: TableRow[], field: DocField, value: string): TableRow[] =>
  rows.filter((r) => r.values[field.name] !== value)
