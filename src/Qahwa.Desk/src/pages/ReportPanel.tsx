import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import type { Filter } from '../lib/filters.ts'
import { hasColumn, labelOf } from '../lib/fields.ts'

const NUMERIC = new Set(['Int', 'Float', 'Decimal', 'Currency', 'Percent', 'Rating', 'Duration'])
const FUNCTIONS = [['count', 'Count'], ['sum', 'Sum'], ['avg', 'Average'], ['min', 'Smallest'], ['max', 'Largest']] as const

/** A grouped summary of the documents the list is showing: count, or sum/average/min/max of a number, per value of a field. */
export function ReportPanel({ doctype, fields, filters, onClose }: {
  doctype: string
  fields: DocField[]
  filters: Filter[]
  onClose: () => void
}) {
  const groupable = fields.filter((f) => hasColumn(f.type) && f.type !== 'Password')
  const numeric = groupable.filter((f) => NUMERIC.has(f.type))
  const [groupBy, setGroupBy] = useState(groupable[0]?.name ?? '')
  const [aggregate, setAggregate] = useState<(typeof FUNCTIONS)[number][0]>('count')
  const [field, setField] = useState(numeric[0]?.name ?? '')

  const run = useMutation({
    mutationFn: () => api.report(doctype, groupBy, aggregate, aggregate === 'count' ? null : field, filters),
  })

  const needsField = aggregate !== 'count'
  const message = run.error instanceof ApiError ? run.error.details[0]?.message ?? run.error.message : run.isError ? 'Could not run the report.' : null

  return (
    <section className="card report-panel" aria-label="Report">
      <div className="page-head">
        <h3>Report</h3>
        <span className="spacer" />
        <button className="btn small" onClick={onClose}>Close</button>
      </div>
      <form className="report-form" onSubmit={(e) => { e.preventDefault(); run.mutate() }}>
        <label>Group by
          <select value={groupBy} onChange={(e) => setGroupBy(e.target.value)}>
            {groupable.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
          </select>
        </label>
        <label>Show
          <select value={aggregate} onChange={(e) => setAggregate(e.target.value as typeof aggregate)}>
            {FUNCTIONS.filter(([key]) => key === 'count' || numeric.length > 0).map(([key, label]) => <option key={key} value={key}>{label}</option>)}
          </select>
        </label>
        {needsField && (
          <label>of
            <select value={field} onChange={(e) => setField(e.target.value)}>
              {numeric.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
            </select>
          </label>
        )}
        <button className="btn primary" disabled={run.isPending || !groupBy || (needsField && !field)}>Run</button>
      </form>
      <p className="muted">Uses the filters of the list.</p>

      {message && <div className="banner error" role="alert">{message}</div>}
      {run.data && (
        <>
          <div className="table-wrap">
            <table>
              <thead><tr><th>{labelOf(groupable.find((f) => f.name === groupBy) ?? { name: groupBy } as DocField)}</th><th>Value</th></tr></thead>
              <tbody>
                {run.data.data.map((row) => <tr key={row.group ?? '(none)'}><td>{row.group ?? '(empty)'}</td><td>{row.value}</td></tr>)}
                {run.data.data.length === 0 && <tr><td colSpan={2} className="empty">No documents.</td></tr>}
              </tbody>
            </table>
          </div>
          {run.data.truncated && <p className="muted">Only the first 10,000 documents were counted.</p>}
        </>
      )}
    </section>
  )
}
