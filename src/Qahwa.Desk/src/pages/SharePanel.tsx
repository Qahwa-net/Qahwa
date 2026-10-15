import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { LinkOption, ShareInfo } from '../api/types.ts'
import { LinkSelect } from '../controls/LinkSelect.tsx'

/** Who this document is shared with, and a way to share it with someone else or stop sharing. */
export function SharePanel({ doctype, name, onClose }: { doctype: string; name: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const key = ['share', doctype, name]
  const shares = useQuery({ queryKey: key, queryFn: () => api.share.list(doctype, name), retry: false })
  const [user, setUser] = useState<LinkOption | null>(null)
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
                <span>{s.fullName ? <>{s.fullName} <small className="muted">{s.user}</small></> : s.user}</span>
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
            if (!user) return
            set.mutate({ user: user.value, read: true, write }, { onSuccess: () => { setUser(null); setWrite(false) } })
          }}>
            <div className="share-user">
              <LinkSelect ariaLabel="Share with" placeholder="Search people by name or email…"
                value={user?.value ?? ''} knownLabel={user?.label}
                cacheKey={['share-users', doctype, name]}
                search={(text) => api.share.users(doctype, name, text)}
                onChange={(_, option) => setUser(option ?? null)} />
            </div>
            <label><input type="checkbox" checked={write} onChange={(e) => setWrite(e.target.checked)} /> Can edit</label>
            <button className="btn primary" disabled={!user || set.isPending}>Share</button>
          </form>
        </>
      )}
    </section>
  )
}
