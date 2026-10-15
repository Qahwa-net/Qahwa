import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { ShareInfo } from '../api/types.ts'

/** Who this document is shared with, and a way to share it with someone else or stop sharing. */
export function SharePanel({ doctype, name, onClose }: { doctype: string; name: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const key = ['share', doctype, name]
  const shares = useQuery({ queryKey: key, queryFn: () => api.share.list(doctype, name), retry: false })
  const [user, setUser] = useState('')
  const [write, setWrite] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = useMutation({
    mutationFn: (s: { user: string; read: boolean; write: boolean }) => api.share.set(doctype, name, s.user, s.read, s.write),
    onSuccess: (next) => { setError(null); queryClient.setQueryData(key, next) },
    onError: (e) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not change sharing.'),
  })

  const forbidden = shares.error instanceof ApiError && shares.error.status === 403

  return (
    <section className="card share-panel" aria-label="Share">
      <div className="page-head">
        <h3>Share</h3>
        <span className="spacer" />
        <button className="btn small" onClick={onClose}>Close</button>
      </div>

      {forbidden && <p className="muted">You may not share this document.</p>}
      {shares.isPending && <p className="muted" role="status">Loading…</p>}
      {error && <div className="banner error" role="alert">{error}</div>}

      {shares.data && (
        <>
          <ul className="share-list">
            {shares.data.map((s: ShareInfo) => (
              <li key={s.user}>
                <span>{s.user}</span>
                <label><input type="checkbox" checked={s.write} disabled={set.isPending}
                  onChange={(e) => set.mutate({ user: s.user, read: true, write: e.target.checked })} /> Can edit</label>
                <button className="btn small danger" disabled={set.isPending}
                  onClick={() => set.mutate({ user: s.user, read: false, write: false })}>Stop sharing</button>
              </li>
            ))}
            {shares.data.length === 0 && <li className="muted">Not shared with anyone.</li>}
          </ul>

          <form className="share-add" onSubmit={(e) => {
            e.preventDefault()
            if (!user.trim()) return
            set.mutate({ user: user.trim(), read: true, write }, { onSuccess: () => { setUser(''); setWrite(false) } })
          }}>
            <input type="email" placeholder="Email of the person" aria-label="Share with" value={user} onChange={(e) => setUser(e.target.value)} />
            <label><input type="checkbox" checked={write} onChange={(e) => setWrite(e.target.checked)} /> Can edit</label>
            <button className="btn primary" disabled={!user.trim() || set.isPending}>Share</button>
          </form>
        </>
      )}
    </section>
  )
}
