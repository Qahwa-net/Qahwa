import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { DocTypeMeta } from '../api/types.ts'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { PRESETS, toLocalInput, toRemindAt } from '../lib/reminders.ts'

export const reminderKey = (doctype: string, name: string) => ['reminders', doctype, name]

/** Ask to be told about a document later: it arrives in the notification bell at the time you choose. */
export function ReminderDialog({ meta, name, onClose }: { meta: DocTypeMeta; name: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const key = reminderKey(meta.name, name)
  const list = useQuery({ queryKey: key, queryFn: () => api.reminders.forDocument(meta.name, name), retry: false })
  const [at, setAt] = useState(() => toLocalInput(PRESETS[2]!.at(new Date())))
  const [message, setMessage] = useState('')
  const [error, setError] = useState<string | null>(null)
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const create = useMutation({
    mutationFn: () => {
      const remindAt = toRemindAt(at)
      if (!remindAt) throw new ApiError(400, 'validation', 'Choose a date and time.')
      return api.reminders.create(meta.name, name, { remindAt, message: message.trim() || undefined })
    },
    onSuccess: async () => { setError(null); setMessage(''); toast('Reminder set'); await queryClient.invalidateQueries({ queryKey: key }) },
    onError: fail,
  })
  const cancel = useMutation({
    mutationFn: (id: string) => api.reminders.cancel(id),
    onSuccess: async () => { toast('Reminder cancelled'); await queryClient.invalidateQueries({ queryKey: key }) },
    onError: fail,
  })
  const busy = create.isPending || cancel.isPending
  const items = list.data ?? []

  return (
    <Modal title="Remind me" subtitle={`Get a notification about ${meta.name} ${name} later.`} onClose={onClose} dismissible={!busy}
      footer={<>
        <button className="btn" onClick={onClose}>Close</button>
        <button className="btn primary" form="reminder-form" disabled={busy || !at}><Icon name="bell" /> {create.isPending ? 'Saving…' : 'Remind me'}</button>
      </>}>
      {error && <div className="banner error" role="alert">{error}</div>}
      {list.isPending && <p className="muted" role="status">Loading…</p>}

      <h3 className="modal-section">Your reminders</h3>
      <ul className="person-list">
        {items.map((r) => (
          <li key={r.id} className={r.status === 'Sent' ? 'done' : undefined}>
            <span className="person-text">
              <strong>{new Date(r.remindAt).toLocaleString()}</strong>
              <small className="chips">
                <span className={`chip status-${r.status.toLowerCase()}`}>{r.status === 'Pending' ? 'Waiting' : r.status}</span>
                {r.message && <span className="chip">{r.message}</span>}
              </small>
            </span>
            {r.status === 'Pending' && <span className="row-actions"><IconButton icon="trash" label="Cancel this reminder" tone="danger" disabled={busy} onClick={() => cancel.mutate(r.id)} /></span>}
          </li>
        ))}
        {list.data && items.length === 0 && <li className="empty-note">No reminders for this document.</li>}
      </ul>

      <form id="reminder-form" className="modal-form" onSubmit={(e) => { e.preventDefault(); if (!busy && at) create.mutate() }}>
        <h3 className="modal-section">New reminder</h3>
        <div className="chips">
          {PRESETS.map((p) => <button key={p.label} type="button" className="btn small" onClick={() => setAt(toLocalInput(p.at(new Date())))}>{p.label}</button>)}
        </div>
        <div className="field">
          <label htmlFor="reminder-at">Remind me on</label>
          <input id="reminder-at" type="datetime-local" value={at} min={toLocalInput(new Date())} onChange={(e) => setAt(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="reminder-note">Note <span className="muted">(optional)</span></label>
          <input id="reminder-note" maxLength={500} value={message} onChange={(e) => setMessage(e.target.value)} />
        </div>
      </form>
    </Modal>
  )
}
