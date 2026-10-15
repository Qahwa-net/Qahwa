import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { SeriesInfo } from '../api/types.ts'

/** The counters behind series names such as CUST-.#####: see them, or move one. The next document gets the number after it. */
export function NamingSeriesPage() {
  const queryClient = useQueryClient()
  const series = useQuery({ queryKey: ['naming', 'series'], queryFn: api.naming.list, retry: false })
  const [message, setMessage] = useState<{ text: string; ok: boolean } | null>(null)

  const save = useMutation({
    mutationFn: (s: { prefix: string; current: number }) => api.naming.set(s.prefix, s.current),
    onSuccess: (next) => { queryClient.setQueryData(['naming', 'series'], next); setMessage({ text: 'Saved.', ok: true }) },
    onError: (e) => setMessage({ text: e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not save.', ok: false }),
  })

  if (series.isError) {
    const forbidden = series.error instanceof ApiError && series.error.status === 403
    return <div className="banner error" role="alert">{forbidden ? 'Only a System Manager can manage naming series.' : 'Could not load the series.'}</div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Naming Series</h1>
          <p className="muted">The number a series has reached. Setting it lower than a number already used makes new documents clash with old ones.</p>
        </div>
      </div>
      {message && <div className={`banner ${message.ok ? 'info' : 'error'}`} role={message.ok ? 'status' : 'alert'}>{message.text}</div>}
      <div className="table-wrap">
        <table>
          <thead><tr><th>Prefix</th><th>Digits</th><th>Used by</th><th>Last number</th><th /></tr></thead>
          <tbody>
            {series.data?.map((s) => <SeriesRow key={s.prefix} series={s} saving={save.isPending} onSave={(current) => save.mutate({ prefix: s.prefix, current })} />)}
            {series.isPending && <tr><td colSpan={5} className="empty">Loading…</td></tr>}
            {series.data?.length === 0 && <tr><td colSpan={5} className="empty">No DocType uses a naming series yet.</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}

function SeriesRow({ series, saving, onSave }: { series: SeriesInfo; saving: boolean; onSave: (current: number) => void }) {
  const [value, setValue] = useState(String(series.current))
  const parsed = Number(value)
  const valid = value.trim() !== '' && Number.isInteger(parsed) && parsed >= 0
  return (
    <tr>
      <td><code>{series.prefix}</code></td>
      <td>{series.digits || ''}</td>
      <td>{series.docTypes.join(', ') || <span className="muted">no DocType</span>}</td>
      <td><input type="number" min={0} aria-label={`Last number of ${series.prefix}`} value={value} onChange={(e) => setValue(e.target.value)} /></td>
      <td><button className="btn small" disabled={saving || !valid || parsed === series.current} onClick={() => onSave(parsed)}>Save</button></td>
    </tr>
  )
}
