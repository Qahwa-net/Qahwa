import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { LinkOption, ShareInfo } from '../api/types.ts'
import { LinkSelect } from '../controls/LinkSelect.tsx'
import { Avatar, Icon, IconButton } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'

export const sharesKey = (doctype: string, name: string) => ['share', doctype, name]

/** Who a document is shared with, and a way to share it with someone else or stop sharing, in a dialog. */
export function ShareDialog({ doctype, name, onClose }: { doctype: string; name: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const key = sharesKey(doctype, name)
  const shares = useQuery({ queryKey: key, queryFn: () => api.share.list(doctype, name), retry: false })
  const [user, setUser] = useState<LinkOption | null>(null)
  const [write, setWrite] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = useMutation({
    mutationFn: (s: { user: string; read: boolean; write: boolean }) => api.share.set(doctype, name, s.user, s.read, s.write),
    onSuccess: (next) => { setError(null); queryClient.setQueryData(key, next); void queryClient.invalidateQueries({ queryKey: ['activity', doctype, name] }) },
    onError: (e) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not change sharing.'),
  })

  const forbidden = shares.error instanceof ApiError && shares.error.status === 403
  const share = () => {
    if (!user) return
    set.mutate({ user: user.value, read: true, write }, { onSuccess: () => { setUser(null); setWrite(false) } })
  }

  return (
    <Modal title="Share" subtitle={`Choose who can open ${doctype === name ? doctype : `${doctype} ${name}`}.`} onClose={onClose} dismissible={!set.isPending}
      footer={shares.data ? (
        <>
          <button className="btn" onClick={onClose}>Close</button>
          <button className="btn primary" form="share-form" disabled={!user || set.isPending}><Icon name="share" /> Share</button>
        </>
      ) : <button className="btn" onClick={onClose}>Close</button>}>
      {forbidden && <div className="banner info" role="status"><Icon name="lock" /> You may not share this document.</div>}
      {shares.isPending && <p className="muted" role="status">Loading…</p>}
      {error && <div className="banner error" role="alert">{error}</div>}

      {shares.data && (
        <>
          <h3 className="modal-section">People with access</h3>
          <ul className="person-list">
            {shares.data.map((s: ShareInfo) => (
              <li key={s.user}>
                <Avatar name={s.fullName || s.user} />
                <span className="person-text">
                  <strong>{s.fullName || s.user}</strong>
                  {s.fullName && <small className="muted">{s.user}</small>}
                </span>
                <span className="row-actions">
                  <select aria-label={`Access for ${s.fullName || s.user}`} className="compact" value={s.write ? 'write' : 'read'} disabled={set.isPending}
                    onChange={(e) => set.mutate({ user: s.user, read: true, write: e.target.value === 'write' })}>
                    <option value="read">Can view</option>
                    <option value="write">Can edit</option>
                  </select>
                  <IconButton icon="trash" label={`Stop sharing with ${s.fullName || s.user}`} tone="danger" disabled={set.isPending}
                    onClick={() => set.mutate({ user: s.user, read: false, write: false })} />
                </span>
              </li>
            ))}
            {shares.data.length === 0 && <li className="empty-note">Not shared with anyone yet.</li>}
          </ul>

          <form id="share-form" className="modal-form" onSubmit={(e) => { e.preventDefault(); share() }}>
            <h3 className="modal-section">Add a person</h3>
            <div className="field">
              <label>Person</label>
              <LinkSelect ariaLabel="Share with" placeholder="Search people by name or email…"
                value={user?.value ?? ''} knownLabel={user?.label} cacheKey={['share-users', doctype, name]}
                search={(text) => api.share.users(doctype, name, text)} onChange={(_, option) => setUser(option ?? null)} />
            </div>
            <label className="switch">
              <input type="checkbox" checked={write} onChange={(e) => setWrite(e.target.checked)} />
              <span>Allow them to edit</span>
            </label>
          </form>
        </>
      )}
    </Modal>
  )
}
