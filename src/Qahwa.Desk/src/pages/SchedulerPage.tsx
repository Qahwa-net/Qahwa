import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import { useToast } from '../controls/Toast.tsx'
import { agoText, paceText } from '../lib/scheduler.ts'

/** What the scheduler does, how often, and how each job went lately (System Managers). */
export function SchedulerPage() {
  const toast = useToast()
  const client = useQueryClient()
  const run = useMutation({
    mutationFn: (job: string) => api.runJob(job),
    onSuccess: async (done) => { toast(`${done.job}: ${done.message}`); await client.invalidateQueries({ queryKey: ['scheduler'] }) },
    onError: (e) => toast(e instanceof ApiError ? e.message : 'Could not run the job.'),
  })
  const overview = useQuery({ queryKey: ['scheduler'], queryFn: api.scheduler, retry: false, refetchInterval: 30_000 })
  if (overview.isError) return <div className="banner error" role="alert">Only a System Manager can see the scheduler.</div>
  const data = overview.data
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Scheduled jobs</h1>
          <p className="muted">
            The scheduler wakes up {data ? `every ${paceText(data.intervalSeconds)}` : 'regularly'} and does the work that is due. A job is written to the log when it
            had something to do or failed, so "last run" means the last time it had work. Every server of this site runs the scheduler and each job
            is safe to run on several at once.
          </p>
        </div>
      </div>
      {overview.isPending && <p className="muted" role="status">Loading…</p>}
      {data && !data.enabled && <div className="banner error" role="alert">The scheduler is turned off on this server (<code>Qahwa:Scheduler:Enabled</code>), so nothing below runs here.</div>}
      {data && (
        <>
          <div className="table-wrap">
            <table>
              <thead><tr><th>Job</th><th>What it does</th><th>Runs</th><th>Last run</th><th className="num">Last 24 h</th><th /></tr></thead>
              <tbody>
                {data.jobs.map((j) => (
                  <tr key={j.job}>
                    <td><strong>{j.job}</strong></td>
                    <td>{j.description}</td>
                    <td className="nowrap">{j.pace}</td>
                    <td>
                      {j.last
                        ? <><span className={`chip ${j.last.status === 'Failed' ? 'prio-high' : ''}`}>{j.last.status}</span> <bdi title={new Date(j.last.at).toLocaleString()}>{agoText(j.last.at)}</bdi>{j.last.message && <small className="muted"> · {j.last.message}</small>}</>
                        : <span className="muted">Nothing to do yet</span>}
                    </td>
                    <td className="num">{j.succeeded24h} ok{j.failed24h > 0 && <> · <strong>{j.failed24h} failed</strong></>}</td>
                    <td className="num">{!j.job.includes('.') && <button className="btn small" disabled={run.isPending} onClick={() => run.mutate(j.job)}>Run now</button>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <h2>Latest log lines</h2>
          {data.recent.length === 0 && <p className="muted">Nothing has been logged yet.</p>}
          <ul className="plain-list">
            {data.recent.map((r) => (
              <li key={r.id}><span className={`chip ${r.status === 'Failed' ? 'prio-high' : ''}`}>{r.status}</span> <strong>{r.job}</strong> <small className="muted">{new Date(r.at).toLocaleString()} · {r.durationMs} ms{r.message ? ` · ${r.message}` : ''}</small></li>
            ))}
          </ul>
          <p className="muted">The full history is in <Link to="/app/Scheduled%20Job%20Log">Scheduled Job Log</Link>; it is kept for 30 days unless the site says otherwise.</p>
        </>
      )}
    </>
  )
}
