import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { AutoRepeatInfo, DocTypeMeta, RepeatFrequency } from '../api/types.ts'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { FREQUENCIES, describeRepeat, tomorrow } from '../lib/autoRepeat.ts'

export const repeatKey = (doctype: string, name: string) => ['auto-repeat', doctype, name]

/** One repeat: how often, what is next, what happened last, with the actions that make sense for its status. */
export function RepeatRow({ repeat, busy, onRun, onToggle, onDelete }: {
  repeat: AutoRepeatInfo
  busy: boolean
  onRun: () => void
  onToggle: () => void
  onDelete: () => void
}) {
  return (
    <li className={repeat.status === 'Finished' ? 'done' : undefined}>
      <span className="person-text">
        <strong>{describeRepeat(repeat)}</strong>
        <small className="chips">
          <span className={`chip status-${repeat.status.toLowerCase()}`}>{repeat.status}</span>
          {repeat.nextDate && repeat.status === 'Active' && <span className="chip"><Icon name="calendar" size={12} /> next {repeat.nextDate}</span>}
          <span className="chip">{repeat.created} made</span>
        </small>
        {repeat.lastError && <small className="error-text" role="alert">Last problem: {repeat.lastError}</small>}
      </span>
      <span className="row-actions">
        {repeat.status === 'Active' && <IconButton icon="play" label="Make a copy now" tone="primary" disabled={busy} onClick={onRun} />}
        {repeat.status !== 'Finished' && (
          <IconButton icon={repeat.status === 'Active' ? 'pause' : 'play'} label={repeat.status === 'Active' ? 'Pause' : 'Resume'} disabled={busy} onClick={onToggle} />
        )}
        <IconButton icon="trash" label="Stop repeating" tone="danger" disabled={busy} onClick={onDelete} />
      </span>
    </li>
  )
}

/** Make a document copy itself every day, week, month, quarter or year. */
export function RepeatDialog({ meta, name, onClose }: { meta: DocTypeMeta; name: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const key = repeatKey(meta.name, name)
  const list = useQuery({ queryKey: key, queryFn: () => api.autoRepeat.forDocument(meta.name, name), retry: false })
  const [frequency, setFrequency] = useState<RepeatFrequency>('Monthly')
  const [start, setStart] = useState(tomorrow())
  const [end, setEnd] = useState('')
  const [dateFields, setDateFields] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const fields = meta.fields.filter((f) => f.type === 'Date' || f.type === 'Datetime')

  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: key }),
    queryClient.invalidateQueries({ queryKey: ['auto-repeat', 'mine'] }),
    queryClient.invalidateQueries({ queryKey: ['list', meta.name] }),
  ])
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const create = useMutation({
    mutationFn: () => api.autoRepeat.create(meta.name, name, { frequency, startDate: start, endDate: end || undefined, dateFields }),
    onSuccess: async () => { setError(null); setEnd(''); setDateFields([]); toast('Repeat set up'); await refresh() },
    onError: fail,
  })
  const run = useMutation({ mutationFn: (id: string) => api.autoRepeat.runNow(id), onSuccess: async () => { setError(null); toast('Copy made'); await refresh() }, onError: fail })
  const toggle = useMutation({
    mutationFn: (r: { id: string; to: 'Active' | 'Paused' }) => api.autoRepeat.update(r.id, { status: r.to }),
    onSuccess: async () => { setError(null); await refresh() }, onError: fail,
  })
  const remove = useMutation({ mutationFn: (id: string) => api.autoRepeat.remove(id), onSuccess: async () => { toast('Repeat stopped'); await refresh() }, onError: fail })
  const busy = create.isPending || run.isPending || toggle.isPending || remove.isPending
  const items = list.data ?? []

  return (
    <Modal title="Repeat" subtitle={`Make a fresh draft copy of ${meta.name} ${name} on a schedule.`} onClose={onClose} dismissible={!busy}
      footer={<>
        <button className="btn" onClick={onClose}>Close</button>
        <button className="btn primary" form="repeat-form" disabled={busy || !start}><Icon name="repeat" /> {create.isPending ? 'Saving…' : 'Repeat'}</button>
      </>}>
      {error && <div className="banner error" role="alert">{error}</div>}
      {list.isPending && <p className="muted" role="status">Loading…</p>}

      <h3 className="modal-section">Repeats</h3>
      <ul className="person-list repeat-list">
        {items.map((r) => (
          <RepeatRow key={r.id} repeat={r} busy={busy} onRun={() => run.mutate(r.id)}
            onToggle={() => toggle.mutate({ id: r.id, to: r.status === 'Active' ? 'Paused' : 'Active' })} onDelete={() => remove.mutate(r.id)} />
        ))}
        {list.data && items.length === 0 && <li className="empty-note">Not repeating yet.</li>}
      </ul>

      <form id="repeat-form" className="modal-form" onSubmit={(e) => { e.preventDefault(); if (!busy && start) create.mutate() }}>
        <h3 className="modal-section">New repeat</h3>
        <div className="modal-row">
          <div className="field">
            <label htmlFor="repeat-frequency">How often</label>
            <select id="repeat-frequency" value={frequency} onChange={(e) => setFrequency(e.target.value as RepeatFrequency)}>
              {FREQUENCIES.map((f) => <option key={f}>{f}</option>)}
            </select>
          </div>
          <div className="field">
            <label htmlFor="repeat-start">First copy on</label>
            <input id="repeat-start" type="date" value={start} min={tomorrow(0)} onChange={(e) => setStart(e.target.value)} />
          </div>
        </div>
        <div className="field">
          <label htmlFor="repeat-end">Stop after <span className="muted">(optional)</span></label>
          <input id="repeat-end" type="date" value={end} min={start} onChange={(e) => setEnd(e.target.value)} />
        </div>
        {fields.length > 0 && (
          <fieldset className="field check-group">
            <legend>Set these dates to the date of each copy</legend>
            {fields.map((f) => (
              <label key={f.name} className="switch">
                <input type="checkbox" checked={dateFields.includes(f.name)}
                  onChange={(e) => setDateFields(e.target.checked ? [...dateFields, f.name] : dateFields.filter((n) => n !== f.name))} />
                <span>{f.label || f.name}</span>
              </label>
            ))}
          </fieldset>
        )}
        <p className="muted small-text">Copies are drafts owned by you. Table rows are copied too.</p>
      </form>
    </Modal>
  )
}
