import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'

/** The slowest API requests this server has seen since it started (System Managers; ADR-112). */
export function SlowRequestsPage() {
  const result = useQuery({ queryKey: ['slow-requests'], queryFn: api.slowRequests, retry: false, refetchInterval: 15000 })
  if (result.isError) return <div className="banner error" role="alert">Only a System Manager can see this page.</div>
  const rows = result.data?.data ?? []
  return (
    <>
      <h1>Slow requests</h1>
      <p className="muted">
        The slowest requests this server answered since it started, when they took at least {result.data?.thresholdMs ?? 1000} ms. Only the address
        is kept (no query, no data). Each server keeps its own list. For the queries behind a slow request use your database's own tools.
      </p>
      {result.isPending && <p className="muted" role="status">Loading…</p>}
      {result.data && rows.length === 0 && <p className="muted">No slow requests were recorded.</p>}
      {rows.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Address</th><th>Method</th><th>Status</th><th className="num">Milliseconds</th><th>Who</th><th>When</th></tr></thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={`${r.at}-${i}`}>
                  <td className="mono">{r.path}</td><td>{r.method}</td><td>{r.status}</td><td className="num">{r.milliseconds}</td>
                  <td>{r.user ?? '—'}</td><td className="nowrap">{new Date(r.at).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}
