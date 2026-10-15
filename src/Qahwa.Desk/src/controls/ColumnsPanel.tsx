import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { DocField, ListViewSettings } from '../api/types.ts'
import { labelOf } from '../lib/fields.ts'
import { moveItem } from '../lib/designer.ts'

const PAGE_SIZES = [10, 20, 50, 100]

/** Choose and order the columns, the rows per page and the default sort: kept for you, per DocType. */
export function ColumnsPanel({ doctype, fields, current, initial, onClose }: {
  doctype: string
  /** Every column that can be shown (the title column and "Last Modified" are always there). */
  fields: DocField[]
  /** The columns showing now. */
  current: string[]
  initial: ListViewSettings | null
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [columns, setColumns] = useState<string[]>(initial?.columns.length ? initial.columns : current)
  const [pageSize, setPageSize] = useState(initial?.pageSize ?? 20)
  const [sortField, setSortField] = useState(initial?.sortField ?? '')
  const [descending, setDescending] = useState(initial?.sortDescending ?? true)
  const [error, setError] = useState<string | null>(null)

  const byName = new Map(fields.map((f) => [f.name, f]))
  const hidden = fields.filter((f) => !columns.includes(f.name))
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['list-settings', doctype] })
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not save.')

  const save = useMutation({
    mutationFn: () => api.listSettings.save(doctype, { columns, pageSize, sortField: sortField || null, sortDescending: descending }),
    onSuccess: async () => { await refresh(); onClose() },
    onError: fail,
  })
  const reset = useMutation({
    mutationFn: () => api.listSettings.reset(doctype),
    onSuccess: async () => { await refresh(); onClose() },
    onError: fail,
  })

  return (
    <section className="card columns-panel" aria-label="List settings">
      <div className="page-head">
        <h3>List settings</h3>
        <span className="spacer" />
        <button className="btn small" onClick={onClose}>Close</button>
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}

      <div className="columns-grid">
        <div>
          <h4>Columns shown</h4>
          <ol className="column-list">
            {columns.map((name, i) => (
              <li key={name}>
                <span>{labelOf(byName.get(name) ?? { name } as DocField)}</span>
                <span className="comment-actions">
                  <button className="btn small icon" aria-label={`Move ${name} up`} disabled={i === 0} onClick={() => setColumns(moveItem(columns, i, i - 1))}>↑</button>
                  <button className="btn small icon" aria-label={`Move ${name} down`} disabled={i === columns.length - 1} onClick={() => setColumns(moveItem(columns, i, i + 1))}>↓</button>
                  <button className="btn small icon danger" aria-label={`Hide ${name}`} onClick={() => setColumns(columns.filter((c) => c !== name))}>✕</button>
                </span>
              </li>
            ))}
            {columns.length === 0 && <li className="muted">The usual columns.</li>}
          </ol>
          <select aria-label="Add a column" value="" onChange={(e) => { if (e.target.value) setColumns([...columns, e.target.value]) }}>
            <option value="">Add a column…</option>
            {hidden.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
          </select>
        </div>

        <div className="columns-options">
          <label className="field"><span>Rows per page</span>
            <select value={pageSize} onChange={(e) => setPageSize(Number(e.target.value))}>
              {PAGE_SIZES.map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </label>
          <label className="field"><span>Sort by</span>
            <select value={sortField} onChange={(e) => setSortField(e.target.value)}>
              <option value="">Last modified</option>
              {fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
            </select>
          </label>
          <label className="check-line"><input type="checkbox" checked={descending} onChange={(e) => setDescending(e.target.checked)} /> Newest or largest first</label>
        </div>
      </div>

      <div className="comment-actions">
        <button className="btn small" disabled={reset.isPending || !initial} onClick={() => reset.mutate()}>Back to the usual view</button>
        <button className="btn small primary" disabled={save.isPending} onClick={() => save.mutate()}>Save</button>
      </div>
    </section>
  )
}
