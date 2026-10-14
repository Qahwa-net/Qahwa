import { useId, useState } from 'react'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import { editableFields, emptyRow, gridColumns, moveRow, rowErrorList, rowErrors, type TableRow } from '../lib/tables.ts'
import { labelOf } from '../lib/fields.ts'
import type { FieldState } from '../lib/logic.ts'
import { FieldControl, FieldInput } from './FieldControl.tsx'

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

  function addRow() {
    const row = makeRow ? makeRow() : emptyRow(child)
    onChange([...rows, row])
    if (all.length > columns.length) setExpanded(null)
  }

  return (
    <div className={`field full table-field${field.hidden ? ' dimmed' : ''}`}>
      <span id={`${id}-label`}>
        {labelOf(field)}{field.required && <span className="req" aria-label="required"> *</span>}
        {field.hidden && <span className="badge"> hidden</span>}
        <span className="muted"> · {rows.length} {rows.length === 1 ? 'row' : 'rows'}</span>
      </span>

      <div className={`table-wrap grid${tableError ? ' invalid' : ''}`}>
        <table aria-labelledby={`${id}-label`}>
          <thead>
            <tr>
              <th className="row-no">#</th>
              {columns.map((c) => (
                <th key={c.name}>{labelOf(c)}{c.required && <span className="req"> *</span>}</th>
              ))}
              <th className="row-actions"><span className="sr-only">Row actions</span></th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => {
              const rowError = byRow.get(index) ?? {}
              const open = expanded === row.key
              return (
                <RowFragment key={row.key}>
                  <tr className={Object.keys(rowError).length > 0 ? 'row-error' : undefined}>
                    <td className="row-no">{index + 1}</td>
                    {columns.map((c) => {
                      const state = rowState?.(row, c)
                      return (
                        <td key={c.name} className={c.type === 'Check' ? 'center' : undefined}>
                          {state && !state.active && !preview
                            ? <span className="muted" aria-label={`${labelOf(c)}, row ${index + 1}: not applicable`}>—</span>
                            : <FieldInput field={c} value={row.values[c.name] ?? ''} readOnly={locked || c.readOnly || state?.readOnly}
                                invalid={Boolean(rowError[c.name])} ariaLabel={`${labelOf(c)}, row ${index + 1}`}
                                onChange={(v) => update(index, c.name, v)} />}
                        </td>
                      )
                    })}
                    <td className="row-actions">
                      {all.length > columns.length && (
                        <button type="button" className="btn small icon" aria-expanded={open}
                          aria-label={`${open ? 'Hide' : 'Show'} all fields of row ${index + 1}`}
                          onClick={() => setExpanded(open ? null : row.key)}>⋯</button>
                      )}
                      {!locked && (
                        <>
                          <button type="button" className="btn small icon" aria-label={`Move row ${index + 1} up`}
                            disabled={index === 0} onClick={() => onChange(moveRow(rows, index, index - 1))}>↑</button>
                          <button type="button" className="btn small icon" aria-label={`Move row ${index + 1} down`}
                            disabled={index === rows.length - 1} onClick={() => onChange(moveRow(rows, index, index + 1))}>↓</button>
                          <button type="button" className="btn small icon danger" aria-label={`Remove row ${index + 1}`}
                            onClick={() => onChange(rows.filter((_, i) => i !== index))}>✕</button>
                        </>
                      )}
                    </td>
                  </tr>
                  {open && (
                    <tr className="row-detail">
                      <td colSpan={columns.length + 2}>
                        <div className="form-grid">
                          {all.map((f) => (
                            <FieldControl key={f.name} field={f} preview={preview} readOnly={locked}
                              state={rowState?.(row, f)}
                              value={row.values[f.name] ?? ''} error={rowError[f.name]}
                              onChange={(v) => update(index, f.name, v)} />
                          ))}
                        </div>
                      </td>
                    </tr>
                  )}
                </RowFragment>
              )
            })}
            {rows.length === 0 && (
              <tr><td colSpan={columns.length + 2} className="empty">No rows yet.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {!locked && <button type="button" className="btn small btn-gap" onClick={addRow}>+ Add row</button>}
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
