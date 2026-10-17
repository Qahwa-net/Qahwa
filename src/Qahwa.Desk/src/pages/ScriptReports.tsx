import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { csvCell } from '../lib/tableCsv.ts'

const messageOf = (e: unknown): string =>
  e instanceof ApiError ? (e.details.length > 0 ? e.details.map((d) => d.message).join(' ') : e.message) : 'Something went wrong.'

const NUMERIC = new Set(['Int', 'Float', 'Decimal', 'Currency', 'Percent'])

/** Reports that installed apps wrote in code: pick one, fill in its filters, run it, and download the table as CSV. */
export function ScriptReportsPage() {
  const list = useQuery({ queryKey: ['script-reports'], queryFn: api.scriptReports.list, retry: false })
  const [id, setId] = useState('')
  const [filters, setFilters] = useState<Record<string, string>>({})
  const chosen = (list.data ?? []).find((r) => r.id === id)
  const run = useMutation({ mutationFn: () => api.scriptReports.run(id, filters) })
  const result = run.data

  function download() {
    if (!result) return
    const lines = [result.columns.map((c) => csvCell(c.label)).join(',')]
    for (const row of result.rows) lines.push(result.columns.map((c) => csvCell(row[c.key] === null || row[c.key] === undefined ? '' : String(row[c.key]))).join(','))
    const blob = new Blob(['﻿', lines.join('\r\n') + '\r\n'], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${result.id.replace(/[^A-Za-z0-9_.-]/g, '')}.csv`
    link.click()
    URL.revokeObjectURL(url)
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Script reports</h1>
          <p className="muted">Reports that installed apps ship. They show only what you may see.</p>
        </div>
      </div>
      {list.isPending && <p className="muted" role="status">Loading…</p>}
      {list.data && list.data.length === 0 && <p className="empty-note">No installed app has a report for you.</p>}
      {list.data && list.data.length > 0 && (
        <form className="card form-stack" onSubmit={(e) => { e.preventDefault(); if (id) run.mutate() }} aria-label="Run a report">
          <label className="field"><span>Report</span>
            <select value={id} onChange={(e) => { setId(e.target.value); setFilters({}); run.reset() }}>
              <option value="">Choose…</option>
              {list.data.map((r) => <option key={r.id} value={r.id}>{r.title}</option>)}
            </select>
          </label>
          {chosen?.filters.map((name) => (
            <label className="field" key={name}><span>{name.replace(/_/g, ' ')}</span>
              <input maxLength={200} value={filters[name] ?? ''} onChange={(e) => setFilters({ ...filters, [name]: e.target.value })} />
            </label>
          ))}
          <div><button className="btn primary" type="submit" disabled={!id || run.isPending}>Run</button></div>
        </form>
      )}
      {run.isError && <div className="banner error" role="alert">{messageOf(run.error)}</div>}
      {result && (
        <>
          {result.truncated && <div className="banner info" role="status">Only the first {result.rows.length} rows are shown.</div>}
          <p><button className="btn small" onClick={download} disabled={result.rows.length === 0}>Download CSV</button> <span className="muted">{result.rows.length} row(s)</span></p>
          <div className="table-wrap">
            <table className="grid">
              <thead><tr>{result.columns.map((c) => <th key={c.key}>{c.label}</th>)}</tr></thead>
              <tbody>
                {result.rows.map((row, i) => (
                  <tr key={i}>{result.columns.map((c) => <td key={c.key} className={NUMERIC.has(c.type) ? 'num' : undefined}>{row[c.key] === null || row[c.key] === undefined ? '' : String(row[c.key])}</td>)}</tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  )
}
