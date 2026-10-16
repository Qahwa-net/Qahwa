import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { ago, describeAgent } from '../lib/sessions.ts'

/** Where you are signed in, and a way to end any of them (a lost phone, a shared computer you forgot to sign out of). */
export function SessionsSection() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const list = useQuery({ queryKey: ['sign-ins'], queryFn: api.sessions.list, retry: false })
  const [confirmAll, setConfirmAll] = useState(false)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['sign-ins'] })
  const fail = (e: unknown) => toast(e instanceof ApiError ? e.message : 'Something went wrong.')

  const end = useMutation({ mutationFn: api.sessions.end, onSuccess: () => { toast('Signed out.'); void refresh() }, onError: fail })
  const endOthers = useMutation({
    mutationFn: api.sessions.endOthers,
    onSuccess: (n) => { setConfirmAll(false); toast(n === 0 ? 'There was nowhere else.' : `Signed out of ${n} other place${n === 1 ? '' : 's'}.`); void refresh() },
    onError: fail,
  })

  if (list.isPending || list.isError) return null
  const others = list.data.filter((s) => !s.current).length

  return (
    <section className="card narrow" aria-labelledby="sessions-title">
      <h2 id="sessions-title">Where you are signed in</h2>
      <ul className="plain">
        {list.data.map((s) => (
          <li key={s.id} className="session-row">
            <div>
              <strong>{describeAgent(s.agent)}</strong>{s.current && <span className="badge"> this device</span>}
              <br /><small className="muted">{s.address ?? 'unknown address'} · signed in {ago(s.created)} · last used {ago(s.last_seen)}</small>
            </div>
            {!s.current && <button className="btn small" onClick={() => end.mutate(s.id)} disabled={end.isPending}>Sign out</button>}
          </li>
        ))}
      </ul>
      {others > 0 && <button className="btn" onClick={() => setConfirmAll(true)}>Sign out of all other places</button>}
      {confirmAll && (
        <ConfirmDialog title="Sign out everywhere else?" confirmLabel="Sign out" tone="danger" busy={endOthers.isPending} onClose={() => setConfirmAll(false)} onConfirm={() => endOthers.mutate()}
          message="You stay signed in here. Every other browser and phone will have to sign in again." />
      )}
    </section>
  )
}
