import type { DocField, DocTypeMeta, FieldError } from '../api/types.ts'
import { fromInput, hasColumn, isEditable, toInput } from './fields.ts'

export type CellValue = string | boolean

/** One row of a child table while it is being edited. `name` is the server's id for rows that are already saved. */
export interface TableRow {
  key: string
  name?: string
  values: Record<string, CellValue>
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

/** Rows as saved on the server become rows the grid can edit. */
export function rowsFromDoc(child: DocTypeMeta, rows: unknown): TableRow[] {
  if (!Array.isArray(rows)) return []
  return rows.map((raw) => {
    const row = raw as Record<string, unknown>
    const values: Record<string, CellValue> = {}
    for (const field of editableFields(child)) values[field.name] = toInput(field, row[field.name])
    return { key: nextKey(), name: String(row.name), values }
  })
}

/** What the API expects for a Table field: rows in display order, saved rows identified by their name. */
export function rowsToPayload(child: DocTypeMeta, rows: TableRow[]): Record<string, unknown>[] {
  return rows.map((row) => {
    const payload: Record<string, unknown> = row.name ? { name: row.name } : {}
    for (const field of editableFields(child)) {
      const value = row.values[field.name]
      if (value !== undefined) payload[field.name] = fromInput(field, value)
    }
    return payload
  })
}

/** True when the rows differ from what was loaded (added, removed, reordered or edited). */
export function rowsChanged(before: TableRow[], after: TableRow[]): boolean {
  if (before.length !== after.length) return true
  return before.some((row, i) => {
    const other = after[i]!
    if (row.name !== other.name) return true
    return Object.keys({ ...row.values, ...other.values }).some((k) => row.values[k] !== other.values[k])
  })
}

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
