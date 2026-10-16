import type { DocField, DocTypeMeta } from '../api/types.ts'
import { labelOf } from './fields.ts'
import { editableFields, emptyRow, type CellValue, type TableRow } from './tables.ts'

/** The most rows a table holds and the largest file taken (the server limits are the same or stricter). */
export const MAX_TABLE_ROWS = 1000
export const MAX_TABLE_CSV_BYTES = 1024 * 1024

const NUMBER = /^-?\d+(\.\d+)?$/
const FORMULA_START = /^[=+\-@\t\r]/

/** A cell as CSV text: quoted when needed, and made harmless to spreadsheet programs (text starting with = + - @ gets a leading quote). */
export function csvCell(value: string): string {
  let text = value
  if (FORMULA_START.test(text) && !NUMBER.test(text)) text = `'${text}`
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

const shown = (field: DocField, value: CellValue | undefined): string => {
  if (field.type === 'Check') return value === true ? 'Yes' : 'No'
  return value === undefined ? '' : String(value)
}

/** The fields a table CSV has columns for: every field a person can type into, in the DocType's order. */
export const csvFields = (child: DocTypeMeta): DocField[] => editableFields(child)

/** The rows as CSV text with a header of field labels (a file that can be edited in a spreadsheet and loaded back). */
export function rowsToCsv(child: DocTypeMeta, rows: TableRow[]): string {
  const fields = csvFields(child)
  const lines = [fields.map((f) => csvCell(labelOf(f))).join(',')]
  for (const row of rows) lines.push(fields.map((f) => csvCell(shown(f, row.values[f.name]))).join(','))
  return lines.join('\r\n') + '\r\n'
}

/** Splits CSV text into records of cells: quotes, doubled quotes, line breaks inside quotes, CRLF or LF, and a leading byte order mark. */
export function parseCsv(text: string): string[][] {
  const rows: string[][] = []
  let row: string[] = []
  let cell = ''
  let quoted = false
  let any = false
  const src = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text
  for (let i = 0; i < src.length; i++) {
    const c = src[i]!
    if (quoted) {
      if (c === '"') {
        if (src[i + 1] === '"') { cell += '"'; i++ } else quoted = false
      } else cell += c
      continue
    }
    if (c === '"' && cell === '') { quoted = true; any = true } else if (c === ',') { row.push(cell); cell = ''; any = true }
    else if (c === '\n' || c === '\r') {
      if (c === '\r' && src[i + 1] === '\n') i++
      if (any || cell !== '') { row.push(cell); rows.push(row) }
      row = []; cell = ''; any = false
    } else { cell += c; any = true }
  }
  if (any || cell !== '') { row.push(cell); rows.push(row) }
  return rows
}

const YES = new Set(['yes', 'true', '1', 'y', '✓'])

function readCell(field: DocField, raw: string): CellValue {
  const text = /^'[=+\-@\t\r]/.test(raw) ? raw.slice(1) : raw // take back the quote the export put in front
  if (field.type === 'Check') return YES.has(text.trim().toLowerCase())
  return text
}

export interface TableCsvResult {
  rows: TableRow[]
  /** Columns of the file that match no field of the table (ignored). */
  ignored: string[]
  /** The fields of the table the file had no column for. */
  missing: string[]
  error?: string
}

const norm = (s: string) => s.trim().toLowerCase()

/**
 * Rows from a CSV file made by "Download CSV" (or any file with a header of field labels or names). Columns are matched by header;
 * unknown columns are ignored; empty lines are skipped. Nothing is checked beyond the shape: the server validates the values when the
 * document is saved, exactly as for rows typed in.
 */
export function rowsFromCsv(child: DocTypeMeta, text: string, limit = MAX_TABLE_ROWS): TableCsvResult {
  const empty = { rows: [], ignored: [], missing: [] }
  if (text.length > MAX_TABLE_CSV_BYTES) return { ...empty, error: 'The file is too large.' }
  const records = parseCsv(text)
  if (records.length === 0) return { ...empty, error: 'The file is empty.' }

  const fields = csvFields(child)
  const byHeader = new Map<string, DocField>()
  for (const f of fields) {
    byHeader.set(norm(labelOf(f)), f)
    byHeader.set(norm(f.name), f)
  }

  const header = records[0]!
  const columns = header.map((h) => byHeader.get(norm(h)))
  const used = new Set(columns.filter((f): f is DocField => f !== undefined).map((f) => f.name))
  if (used.size === 0) return { ...empty, error: 'None of the columns match this table. Download the CSV first to see the columns.' }
  const ignored = header.filter((h, i) => columns[i] === undefined && h.trim() !== '')
  const missing = fields.filter((f) => !used.has(f.name)).map((f) => labelOf(f))

  const body = records.slice(1).filter((r) => r.some((c) => c.trim() !== ''))
  if (body.length > limit) return { ...empty, ignored, missing, error: `A table holds at most ${limit} rows; the file has ${body.length}.` }

  const rows = body.map((record) => {
    const row = emptyRow(child)
    columns.forEach((field, i) => {
      if (field) row.values[field.name] = readCell(field, record[i] ?? '')
    })
    return row
  })
  return { rows, ignored, missing }
}
