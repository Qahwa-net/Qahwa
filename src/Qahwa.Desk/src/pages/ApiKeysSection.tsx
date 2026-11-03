import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { ApiKeyCreated } from '../api/types.ts'
import { ChipPicker } from '../controls/ChipPicker.tsx'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { ConfirmDialog, Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'

/** Your API keys: programs can act as you with these, within what you may do yourself. */
export function ApiKeysSection() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const keys = useQuery({ queryKey: ['api-keys'], queryFn: () => api.apiKeys.list(), retry: false })
  const [creating, setCreating] = useState(false)
  const [created, setCreated] = useState<ApiKeyCreated | null>(null)
  const [revoking, setRevoking] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const revoke = useMutation({
    mutationFn: (id: string) => api.apiKeys.revoke(id),
    onSuccess: async () => { setRevoking(null); toast('API key deleted'); await queryClient.invalidateQueries({ queryKey: ['api-keys'] }) },
    onError: (e) => { setRevoking(null); setError(e instanceof ApiError ? e.message : 'Could not delete the key.') },
  })
  const items = keys.data ?? []

  return (
    <section className="card api-keys" aria-label="API keys">
      <div className="page-head">
        <div>
          <h2>API keys</h2>
          <p className="muted">Let a program act as you, without your password. A key can do what you can do, no more, and works until it expires or you delete it.</p>
        </div>
        <span className="spacer" />
        <button className="btn" onClick={() => setCreating(true)}><Icon name="plus" /> New key</button>
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}
      <ul className="person-list">
        {items.map((k) => (
          <li key={k.id}>
            <span className="person-text">
              <strong>{k.description || 'No description'}</strong>
              <small className="muted"><code>qk_{k.keyId}…</code></small>
              <small className="chips">
                {k.readOnly && <span className="chip">Read only</span>}
                {k.docTypes.length > 0 && <span className="chip" title={k.docTypes.join(', ')}>Only {k.docTypes.length === 1 ? k.docTypes[0] : `${k.docTypes.length} DocTypes`}</span>}
                {k.ratePerMinute ? <span className="chip">{k.ratePerMinute}/minute</span> : null}
                {k.expired ? <span className="chip prio-high">Expired</span> : k.expiresOn ? <span className="chip">Expires {k.expiresOn}</span> : <span className="chip">No expiry</span>}
                <span className="chip">{k.lastUsed ? `Used ${new Date(k.lastUsed).toLocaleString()}` : 'Never used'}</span>
              </small>
            </span>
            <IconButton icon="trash" label={`Delete key ${k.description || k.keyId}`} tone="danger" onClick={() => setRevoking(k.id)} />
          </li>
        ))}
        {keys.data && items.length === 0 && <li className="empty-note">You have no API keys.</li>}
      </ul>
      {creating && <CreateDialog onClose={() => setCreating(false)} onCreated={async (k) => { setCreating(false); setCreated(k); await queryClient.invalidateQueries({ queryKey: ['api-keys'] }) }} />}
      {created && <TokenDialog created={created} onClose={() => setCreated(null)} />}
      {revoking && <ConfirmDialog title="Delete this API key?" tone="danger" confirmLabel="Delete" busy={revoke.isPending} onClose={() => setRevoking(null)} onConfirm={() => revoke.mutate(revoking)}
        message="Programs using it will stop working at once." />}
    </section>
  )
}

function CreateDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (k: ApiKeyCreated) => void }) {
  const [description, setDescription] = useState('')
  const [readOnly, setReadOnly] = useState(false)
  const [expiresOn, setExpiresOn] = useState('')
  const [docTypes, setDocTypes] = useState<string[]>([])
  const [rate, setRate] = useState('')
  const [error, setError] = useState<string | null>(null)
  const create = useMutation({
    mutationFn: () => api.apiKeys.create({ description: description.trim(), readOnly, expiresOn: expiresOn || undefined, docTypes: docTypes.length > 0 ? docTypes : undefined, ratePerMinute: rate.trim() === '' ? undefined : Number(rate) }),
    onSuccess: onCreated,
    onError: (e) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not make the key.'),
  })
  const tomorrow = new Date(Date.now() + 86_400_000).toISOString().slice(0, 10)
  return (
    <Modal title="New API key" subtitle="You will see the key once, when it is made." size="sm" onClose={onClose} dismissible={!create.isPending}
      footer={<>
        <button className="btn" onClick={onClose} disabled={create.isPending}>Cancel</button>
        <button className="btn primary" form="key-form" disabled={create.isPending}><Icon name="plus" /> {create.isPending ? 'Making…' : 'Make key'}</button>
      </>}>
      {error && <div className="banner error" role="alert">{error}</div>}
      <form id="key-form" className="modal-form" onSubmit={(e) => { e.preventDefault(); create.mutate() }}>
        <div className="field">
          <label htmlFor="key-desc">What is it for?</label>
          <input id="key-desc" data-autofocus maxLength={140} value={description} placeholder="e.g. Nightly report script" onChange={(e) => setDescription(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="key-exp">Expires on <span className="muted">(optional)</span></label>
          <input id="key-exp" type="date" min={tomorrow} value={expiresOn} onChange={(e) => setExpiresOn(e.target.value)} />
        </div>
        <div className="field">
          <label>Only these DocTypes <span className="muted">(optional: with none chosen it can use all you can)</span></label>
          <ChipPicker doctype="DocType" values={docTypes} onChange={setDocTypes} label="DocTypes this key may use" placeholder="Search a DocType…" />
          <small className="muted">A limited key can only read and write those DocTypes. Everything else is closed to it, including your account and other keys.</small>
        </div>
        <div className="field">
          <label htmlFor="key-rate">Most requests per minute <span className="muted">(optional)</span></label>
          <input id="key-rate" type="number" min={1} max={10000} inputMode="numeric" value={rate} placeholder="no limit" onChange={(e) => setRate(e.target.value)} />
        </div>
        <label className="switch"><input type="checkbox" checked={readOnly} onChange={(e) => setReadOnly(e.target.checked)} /><span>Read only (it can look but not change anything)</span></label>
      </form>
    </Modal>
  )
}

function TokenDialog({ created, onClose }: { created: ApiKeyCreated; onClose: () => void }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(created.token)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }
  return (
    <Modal title="Your new API key" size="md" onClose={onClose}
      footer={<button className="btn primary" onClick={onClose}>I have saved it</button>}>
      <div className="banner info" role="status"><Icon name="lock" /> Copy it now. For your safety it cannot be shown again.</div>
      <div className="field">
        <label htmlFor="key-token">Key</label>
        <input id="key-token" data-autofocus readOnly className="mono" value={created.token} onFocus={(e) => e.currentTarget.select()} />
        <button type="button" className="btn small" onClick={() => void copy()}><Icon name="check" size={14} /> {copied ? 'Copied' : 'Copy'}</button>
      </div>
      <p className="muted small-text">Use it as a header: <code>Authorization: Bearer {created.token.slice(0, 11)}…</code></p>
    </Modal>
  )
}
