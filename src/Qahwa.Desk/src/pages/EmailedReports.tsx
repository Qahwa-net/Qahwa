import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { useAuth } from '../auth.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { describeSchedule, problemWith, type EmailedReport } from '../lib/emailedReports.ts'

const when = (value: string | null) => (value ? new Date(value).toLocaleString() : '—')

/** Reports emailed on a schedule: a saved report, who gets it, how often, and whose permissions it runs with. System Managers only. */
export function EmailedReportsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const { user } = useAuth()
  const items = useQuery({ queryKey: ['emailed-reports'], queryFn: api.emailedReports.list, retry: false })
  const reports = useQuery({ queryKey: ['reports'], queryFn: api.reports.list, retry: false })

  const [report, setReport] = useState('')
  const [recipients, setRecipients] = useState('')
  const [frequency, setFrequency] = useState('Daily')
  const [sendHour, setSendHour] = useState(8)
  const [runAs, setRunAs] = useState(user?.user ?? '')
  const [maxRows, setMaxRows] = useState(50)
  const [subject, setSubject] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState<EmailedReport | null>(null)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['emailed-reports'] })
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')
  const create = useMutation({
    mutationFn: () => api.emailedReports.create({ report, recipients, frequency, sendHour, runAs: runAs.trim(), maxRows, subject }),
    onSuccess: async () => { setError(null); setRecipients(''); setSubject(''); toast('Scheduled'); await refresh() },
    onError: fail,
  })
  const toggle = useMutation({
    mutationFn: (r: EmailedReport) => api.emailedReports.update(r.id, { status: r.status === 'Active' ? 'Paused' : 'Active' }),
    onSuccess: async () => { setError(null); await refresh() },
    onError: fail,
  })
  const send = useMutation({
    mutationFn: (r: EmailedReport) => api.emailedReports.send(r.id),
    onSuccess: async (queued) => { setError(null); toast(`${queued} email(s) queued`); await refresh() },
    onError: fail,
  })
  const remove = useMutation({
    mutationFn: (r: EmailedReport) => api.emailedReports.remove(r.id),
    onSuccess: async () => { setRemoving(null); toast('Removed'); await refresh() },
    onError: (e) => { setRemoving(null); fail(e) },
  })
  const busy = create.isPending || toggle.isPending || send.isPending || remove.isPending
  const problem = problemWith(recipients, report, runAs, maxRows)

  if (items.isError) return <div className="banner error" role="alert">You need the System Manager role to set up emailed reports.</div>

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Emailed reports</h1>
          <p className="muted">Send the first rows of a saved report to some people on a schedule. The report runs with the permissions of the person you choose, so it contains only what they may see.</p>
        </div>
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}

      <form className="card form-stack" onSubmit={(e) => { e.preventDefault(); if (!problem) create.mutate() }} aria-label="Schedule a report">
        <label className="field"><span>Report</span>
          <select value={report} onChange={(e) => setReport(e.target.value)}>
            <option value="">Choose…</option>
            {(reports.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.title}</option>)}
          </select>
        </label>
        <label className="field"><span>Send to (one address per line, up to 10)</span>
          <textarea rows={3} value={recipients} onChange={(e) => setRecipients(e.target.value)} />
        </label>
        <label className="field"><span>Subject (optional, the report's title if empty)</span>
          <input maxLength={200} value={subject} onChange={(e) => setSubject(e.target.value)} />
        </label>
        <div className="row">
          <label className="field"><span>How often</span>
            <select value={frequency} onChange={(e) => setFrequency(e.target.value)}>
              <option>Daily</option><option>Weekly</option><option>Monthly</option>
            </select>
          </label>
          <label className="field"><span>Hour (UTC)</span>
            <input type="number" min={0} max={23} value={sendHour} onChange={(e) => setSendHour(Number(e.target.value))} />
          </label>
          <label className="field"><span>Rows to send</span>
            <input type="number" min={1} max={200} value={maxRows} onChange={(e) => setMaxRows(Number(e.target.value))} />
          </label>
        </div>
        <label className="field"><span>Run as (email of an enabled user)</span>
          <input type="email" value={runAs} onChange={(e) => setRunAs(e.target.value)} />
        </label>
        {recipients && problem && <p className="muted" role="status">{problem}</p>}
        <div><button className="btn primary" type="submit" disabled={busy || problem !== null}>Schedule</button></div>
      </form>

      {items.isPending && <p className="muted" role="status">Loading…</p>}
      {items.data && items.data.length === 0 && <p className="empty-note">No reports are emailed yet.</p>}
      <ul className="repeat-page-list">
        {(items.data ?? []).map((r) => (
          <li key={r.id} className={`card${r.lastError ? ' attention' : ''}`}>
            <p><strong>{r.reportTitle ?? r.report}</strong> <small className="muted">· {describeSchedule(r.frequency, r.sendHour)} · {r.status}</small></p>
            <p className="muted small-text">To {r.recipients.join(', ')} · runs as {r.runAs} · {r.maxRows} rows</p>
            <p className="muted small-text">Next: {r.status === 'Active' ? when(r.nextSend) : 'paused'} · last sent {when(r.lastSent)} ({r.sentCount} sent)</p>
            {r.lastError && <p role="alert" className="error-text small-text">Last problem: {r.lastError}</p>}
            <div className="row-actions">
              <button className="btn small" disabled={busy} onClick={() => send.mutate(r)}>Send now</button>
              <button className="btn small" disabled={busy} onClick={() => toggle.mutate(r)}>{r.status === 'Active' ? 'Pause' : 'Resume'}</button>
              <button className="btn small danger" disabled={busy} onClick={() => setRemoving(r)}>Remove</button>
            </div>
          </li>
        ))}
      </ul>
      {removing && (
        <ConfirmDialog title="Stop emailing this report?" tone="danger" confirmLabel="Remove" busy={remove.isPending} onClose={() => setRemoving(null)}
          onConfirm={() => remove.mutate(removing)} message="No more emails will be sent for it. Emails already queued are still sent." />
      )}
    </>
  )
}
