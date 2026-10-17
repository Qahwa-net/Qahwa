import { useId, useRef, useState } from 'react'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import { addChip, chipField, chipValues, editableFields, emptyRow, gridColumns, moveRow, removeChip, rowErrorList, rowErrors, type TableRow } from '../lib/tables.ts'
import { labelOf } from '../lib/fields.ts'
import type { FieldState } from '../lib/logic.ts'
import { FieldControl, FieldInput } from './FieldControl.tsx'
import { useToast } from './Toast.tsx'
import { ChipPicker } from './ChipPicker.tsx'
import { Icon, IconButton } from './Icon.tsx'
import { MAX_TABLE_CSV_BYTES, MAX_TABLE_ROWS, rowsFromCsv, rowsToCsv } from '../lib/tableCsv.ts'

interface Props {
  field: DocField
  child: DocTypeMeta
  rows: TableRow[]
  onChange: (rows: TableRow[]) => void
  readOnly?: boolean
  /** All the form's errors; the ones for this table are picked out by their `field[row].column` path. */
  errors: Record<string, string>
  /** Designer preview: hidden fields are shown (dimmed) instead of omitted. */
  preview?: boolean
  /** What the field logic says about a cell of a row right now. Without it cells are static. */
  rowState?: (row: TableRow, field: DocField) => FieldState
  /** Builds a new row (so defaults that read the document can be applied). */
  makeRow?: () => TableRow
  /** Called after a cell was edited, with the row's index and the field, e.g. to run "fetch from". */
  onCellEdited?: (rowIndex: number, fieldName: string, value: string | boolean) => void
}

/** An editable grid for a child table: type into the cells, add, remove and reorder rows. */
export function TableControl({ field, child, rows, onChange, readOnly, errors, preview, rowState, makeRow, onCellEdited }: Props) {
  const id = useId()
  const toast = useToast()
  const picker = useRef<HTMLInputElement>(null)
  const [csvMessage, setCsvMessage] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const columns = gridColumns(child)
  const all = editableFields(child).filter((f) => preview || !f.hidden)
  const locked = Boolean(readOnly || field.readOnly)
  const { table: tableError, rows: byRow } = rowErrors(errors, field.name)
  const messages = rowErrorList(errors, field.name, child.fields)

  const update = (index: number, name: string, value: string | boolean) => {
    onChange(rows.map((r, i) => (i === index ? { ...r, values: { ...r.values, [name]: value } } : r)))
    onCellEdited?.(index, name, value)
  }

  function download() {
    const blob = new Blob(['\uFEFF', rowsToCsv(child, rows)], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${field.name.replace(/[^A-Za-z0-9_-]/g, '') || 'table'}.csv`
    link.click()
    URL.revokeObjectURL(url)
  }

  async function upload(file: File | undefined) {
    if (!file) return
    setCsvMessage(null)
    if (file.size > MAX_TABLE_CSV_BYTES) return setCsvMessage('The file is too large (1 MB at most).')
    if (!/\.(csv|txt)$/i.test(file.name)) return setCsvMessage('Choose a CSV file.')
    const result = rowsFromCsv(child, await file.text())
    if (result.error) return setCsvMessage(result.error)
    if (rows.length + result.rows.length > MAX_TABLE_ROWS) return setCsvMessage(`A table holds at most ${MAX_TABLE_ROWS} rows.`)
    onChange([...rows, ...result.rows])
    toast(`${result.rows.length} row(s) added`)
    if (result.ignored.length > 0) setCsvMessage(`Ignored columns: ${result.ignored.join(', ')}.`)
  }

  // A table with one Link field can be shown as chips: the rows are the chosen documents.
  const chips = field.multiSelect ? chipField(child) : undefined
  if (chips) {
    return (
      <div className={`field full table-field${field.hidden ? ' dimmed' : ''}`}>
        <span id={`${id}-label`}>
          {labelOf(field)}{field.required && <span className="req" aria-label="required"> *</span>}
          {field.hidden && <span className="badge"> hidden</span>}
        </span>
        <ChipPicker doctype={chips.options ?? ''} values={chipValues(rows, chips)} label={labelOf(field)} placeholder="Search to add…" readOnly={locked}
          onChange={(values) => {
            const current = chipValues(rows, chips)
            const added = values.find((v) => !current.includes(v))
            const removed = current.find((v) => !values.includes(v))
            if (added !== undefined) onChange(addChip(rows, child, chips, added, makeRow))
            else if (removed !== undefined) onChange(removeChip(rows, chips, removed))
          }} />
        {tableError && <small className="error-text" role="alert">{tableError}</small>}
        {messages.length > 0 && (
          <ul className="row-messages" role="alert">
            {messages.map((m) => <li key={m} className="error-text">{m}</li>)}
          </ul>
        )}
        {field.description && <small className="muted">{field.description}</small>}
      </div>
    )
  }

  function addRow() {
    const row = makeRow ? makeRow() : emptyRow(child)
    onChange([...rows, row])
    if (all.length > columns.length) setExpanded(null)
  }

  return (
    <div className={`field full table-field${field.hidden ? ' dimmed' : ''}`}>
      <div className="child-table-header">
      <span id={`${id}-label`}>
        {labelOf(field)}{field.required && <span className="req" aria-label="required"> *</span>}
        {field.hidden && <span className="badge"> hidden</span>}
        <span className="child-table-count">{rows.length} {rows.length === 1 ? 'row' : 'rows'}</span>
      </span>
      <div className="child-table-tools">
        {!preview && rows.length > 0 && <IconButton icon="download" label="Download rows as CSV" onClick={download} />}
        {!preview && !locked && <IconButton icon="upload" label="Import rows from CSV" onClick={() => picker.current?.click()} />}
        {!locked && <button type="button" className="btn small primary" disabled={rows.length >= MAX_TABLE_ROWS} onClick={addRow}><Icon name="plus" size={16} /> Add row</button>}
      </div>
      </div>

      <div className={`table-wrap grid${tableError ? ' invalid' : ''}`}>
        <table aria-labelledby={`${id}-label`}>
          <thead>
            <tr>
              <th className="row-no">#</th>
              {columns.map((c) => (
                <th key={c.name} className={`child-column child-column-${c.type.toLowerCase()}`}>{labelOf(c)}{c.required && <span className="req"> *</span>}</th>
              ))}
              <th scope="col" className="child-actions-cell">Actions</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => {
              const rowError = byRow.get(index) ?? {}
              const open = expanded === row.key
              return (
                <RowFragment key={row.key}>
                  <tr className={`${Object.keys(rowError).length > 0 ? 'row-error ' : ''}${open ? 'row-expanded' : ''}`}>
                    <td className="row-no">{index + 1}</td>
                    {columns.map((c) => {
                      const state = rowState?.(row, c)
                      return (
                        <td key={c.name} className={`child-cell child-column-${c.type.toLowerCase()}${c.type === 'Check' ? ' center' : ''}`}>
                          {state && !state.active && !preview
                            ? <span className="muted" aria-label={`${labelOf(c)}, row ${index + 1}: not applicable`}>—</span>
                            : <FieldInput field={c} value={row.values[c.name] ?? ''} siblings={row.values} readOnly={locked || c.readOnly || state?.readOnly}
                                invalid={Boolean(rowError[c.name])} ariaLabel={`${labelOf(c)}, row ${index + 1}`}
                                onChange={(v) => update(index, c.name, v)} />}
                        </td>
                      )
                    })}
                    <td className="child-actions-cell">
                      <div className="child-row-actions">
                        <IconButton icon="pencil" label={`${open ? 'Close' : 'Open'} details of row ${index + 1}`} aria-expanded={open} pressed={open}
                          aria-controls={`${id}-row-${row.key}`} onClick={() => setExpanded(open ? null : row.key)} />
                      {!locked && (
                        <>
                          <IconButton icon="arrowUp" label={`Move row ${index + 1} up`}
                            disabled={index === 0} onClick={() => onChange(moveRow(rows, index, index - 1))} />
                          <IconButton icon="arrowDown" label={`Move row ${index + 1} down`}
                            disabled={index === rows.length - 1} onClick={() => onChange(moveRow(rows, index, index + 1))} />
                          <IconButton icon="trash" tone="danger" label={`Remove row ${index + 1}`}
                            onClick={() => { onChange(rows.filter((_, i) => i !== index)); if (open) setExpanded(null) }} />
                        </>
                      )}
                      </div>
                    </td>
                  </tr>
                  {open && (
                    <tr className="row-detail">
                      <td colSpan={columns.length + 2}>
                        <div className="child-row-detail" id={`${id}-row-${row.key}`}>
                        <div className="child-detail-heading"><strong>Row {index + 1} details</strong><IconButton icon="x" label={`Close details of row ${index + 1}`} onClick={() => setExpanded(null)} /></div>
                        <div className="form-grid">
                          {all.map((f) => (
                            <FieldControl key={f.name} field={f} preview={preview} readOnly={locked} siblings={row.values}
                              state={rowState?.(row, f)}
                              value={row.values[f.name] ?? ''} error={rowError[f.name]}
                              onChange={(v) => update(index, f.name, v)} />
                          ))}
                        </div>
                        </div>
                      </td>
                    </tr>
                  )}
                </RowFragment>
              )
            })}
            {rows.length === 0 && (
              <tr><td colSpan={columns.length + 2} className="empty"><div className="child-table-empty"><Icon name="columns" size={28} /><strong>No {labelOf(field).toLowerCase()} yet</strong><span>{locked ? 'This table has no entries.' : 'Add a row to get started, or import rows from a CSV file.'}</span>{!locked && <button type="button" className="btn small" onClick={addRow}><Icon name="plus" size={16} /> Add first row</button>}</div></td></tr>
            )}
          </tbody>
        </table>
      </div>

      {rows.length > 0 && <div className="child-table-footer"><span>{rows.length} {rows.length === 1 ? 'entry' : 'entries'}{locked ? ' · Read only' : ' · Changes save with this document'}</span>{!locked && <button type="button" className="btn small ghost" disabled={rows.length >= MAX_TABLE_ROWS} onClick={addRow}><Icon name="plus" size={16} /> Add row</button>}</div>}
      {!preview && (
        <span className="table-csv">
          {!locked && (
            <>
              <input ref={picker} type="file" accept=".csv,.txt,text/csv" hidden aria-label={`Upload rows of ${labelOf(field)} from a CSV file`}
                onChange={(e) => { void upload(e.target.files?.[0]); e.target.value = '' }} />
            </>
          )}
        </span>
      )}
      {csvMessage && <small className="muted" role="status">{csvMessage}</small>}
      {tableError && <small className="error-text" role="alert">{tableError}</small>}
      {messages.length > 0 && (
        <ul className="row-messages" role="alert">
          {messages.map((m) => <li key={m} className="error-text">{m}</li>)}
        </ul>
      )}
      {field.description && <small className="muted">{field.description}</small>}
    </div>
  )
}

/** Groups a row with its detail row without adding an element to the table. */
function RowFragment({ children }: { children: React.ReactNode }) {
  return <>{children}</>
}
