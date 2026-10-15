import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { RepeatRow } from './RepeatDialog.tsx'
import { needsAttention } from '../lib/autoRepeat.ts'

/** Every document you have set to repeat (everyone's, for a System Manager), with what to do about each. */
export function AutoRepeatPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const mine = useQuery({ queryKey: ['auto-repeat', 'mine'], queryFn: api.autoRepeat.mine, retry: false })
  const [error, setError] = useState<string | null>(null)
  const [stopping, setStopping] = useState<string | null>(null)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['auto-repeat'] })
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')
  const run = useMutation({ mutationFn: (id: string) => api.autoRepeat.runNow(id), onSuccess: async () => { setError(null); toast('Copy made'); await refresh() }, onError: fail })
  const toggle = useMutation({
    mutationFn: (r: { id: string; to: 'Active' | 'Paused' }) => api.autoRepeat.update(r.id, { status: r.to }),
    onSuccess: async () => { setError(null); await refresh() }, onError: fail,
  })
  const remove = useMutation({
    mutationFn: (id: string) => api.autoRepeat.remove(id),
    onSuccess: async () => { setStopping(null); toast('Repeat stopped'); await refresh() },
    onError: (e) => { setStopping(null); fail(e) },
  })
  const busy = run.isPending || toggle.isPending || remove.isPending
  const items = mine.data ?? []

  if (mine.isError) return <div className="banner error" role="alert">Could not load your repeats.</div>

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Auto Repeat</h1>
          <p className="muted">Documents that make a fresh draft copy of themselves on a schedule. To repeat a document, open it and click the repeat icon.</p>
        </div>
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}
      {mine.isPending && <p className="muted" role="status">Loading…</p>}
      {mine.data && items.length === 0 && <p className="empty-note">Nothing repeats yet.</p>}
      <ul className="repeat-page-list">
        {items.map((r) => (
          <li key={r.id} className={`card${needsAttention(r) ? ' attention' : ''}`}>
            <p className="repeat-doc">
              <Link to={`/app/${encodeURIComponent(r.docType)}/${encodeURIComponent(r.name)}`}>{r.docType} {r.name}</Link>
              <small className="muted"> · set up by {r.ownerName || r.owner}</small>
              {r.lastCreated && <small className="muted"> · last copy <Link to={`/app/${encodeURIComponent(r.docType)}/${encodeURIComponent(r.lastCreated)}`}>{r.lastCreated}</Link></small>}
            </p>
            <ul className="person-list repeat-list">
              <RepeatRow repeat={r} busy={busy} onRun={() => run.mutate(r.id)}
                onToggle={() => toggle.mutate({ id: r.id, to: r.status === 'Active' ? 'Paused' : 'Active' })} onDelete={() => setStopping(r.id)} />
            </ul>
          </li>
        ))}
      </ul>
      {stopping && (
        <ConfirmDialog title="Stop repeating?" tone="danger" confirmLabel="Stop repeating" busy={remove.isPending} onClose={() => setStopping(null)}
          onConfirm={() => remove.mutate(stopping)} message="No more copies will be made. Copies already made stay." />
      )}
    </>
  )
}
