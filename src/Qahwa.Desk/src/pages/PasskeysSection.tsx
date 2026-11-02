import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { IconButton } from '../controls/Icon.tsx'
import { ConfirmDialog, Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { createPasskey, passkeyErrorText, passkeysSupported } from '../lib/passkeys.ts'

/** Your passkeys: sign in with a fingerprint, face or device PIN instead of a password. */
export function PasskeysSection() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const enabled = useQuery({ queryKey: ['passkeys-enabled'], queryFn: api.passkeys.enabled, retry: false })
  const keys = useQuery({ queryKey: ['passkeys'], queryFn: api.passkeys.list, retry: false, enabled: enabled.data === true })
  const [adding, setAdding] = useState(false)
  const [removing, setRemoving] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const remove = useMutation({
    mutationFn: (id: string) => api.passkeys.remove(id),
    onSuccess: async () => { setRemoving(null); toast('Passkey removed'); await queryClient.invalidateQueries({ queryKey: ['passkeys'] }) },
    onError: (e) => { setRemoving(null); setError(e instanceof ApiError ? e.message : 'Could not remove the passkey.') },
  })

  if (!enabled.data) return null
  const items = keys.data ?? []

  return (
    <section className="card api-keys" aria-label="Passkeys">
      <div className="page-head">
        <div>
          <h2>Passkeys</h2>
          <p className="muted">Sign in with your fingerprint, face or device PIN instead of a password. A passkey only works on this site, so a look-alike site cannot trick you into using it. Keep your password as well, in case you lose the device.</p>
        </div>
        <span className="spacer" />
        {passkeysSupported() && <button className="btn" onClick={() => { setError(null); setAdding(true) }}>Add a passkey</button>}
      </div>
      {!passkeysSupported() && <div className="banner info">This browser cannot make passkeys.</div>}
      {error && <div className="banner error" role="alert">{error}</div>}
      <ul className="person-list">
        {items.map((k) => (
          <li key={k.id}>
            <span className="person-text">
              <strong>{k.label}</strong>
              <small className="muted">Added {new Date(k.created).toLocaleDateString()} · {k.lastUsed ? `used ${new Date(k.lastUsed).toLocaleString()}` : 'never used'}</small>
            </span>
            <IconButton icon="trash" label={`Remove passkey ${k.label}`} tone="danger" onClick={() => setRemoving(k.id)} />
          </li>
        ))}
        {keys.data && items.length === 0 && <li className="empty-note">You have no passkeys.</li>}
      </ul>
      {adding && <AddDialog onClose={() => setAdding(false)} onAdded={async () => { setAdding(false); toast('Passkey added'); await queryClient.invalidateQueries({ queryKey: ['passkeys'] }) }} />}
      {removing && <ConfirmDialog title="Remove this passkey?" tone="danger" confirmLabel="Remove" busy={remove.isPending} onClose={() => setRemoving(null)} onConfirm={() => remove.mutate(removing)}
        message="You will no longer be able to sign in with it. Your password still works." />}
    </section>
  )
}

function AddDialog({ onClose, onAdded }: { onClose: () => void; onAdded: () => void }) {
  const [label, setLabel] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  const add = useMutation({
    mutationFn: async () => api.passkeys.register(await createPasskey(await api.passkeys.begin(password), label.trim())),
    onSuccess: onAdded,
    onError: (e) => setError(
      e instanceof ApiError
        ? (e.status === 401 ? 'The password is not right.' : (e.details[0]?.message ?? e.message))
        : passkeyErrorText(e)),
  })

  return (
    <Modal title="Add a passkey" size="sm" onClose={onClose} dismissible={!add.isPending}
      footer={<><button className="btn" onClick={onClose} disabled={add.isPending}>Cancel</button><button className="btn primary" form="add-passkey" type="submit" disabled={add.isPending || !password}>Continue</button></>}>
      <form id="add-passkey" onSubmit={(e: FormEvent) => { e.preventDefault(); setError(null); add.mutate() }}>
        {error && <div className="banner error" role="alert">{error}</div>}
        <label className="field"><span>Name <span className="muted">(so you can tell them apart, e.g. "Work laptop")</span></span>
          <input value={label} maxLength={60} autoFocus onChange={(e) => setLabel(e.target.value)} /></label>
        <label className="field"><span>Your password <span className="muted">(to be sure it is you)</span></span>
          <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></label>
      </form>
    </Modal>
  )
}
