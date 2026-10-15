import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { AssignmentInfo, LinkOption } from '../api/types.ts'
import { LinkSelect } from '../controls/LinkSelect.tsx'
import { Avatar, Icon, IconButton } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useAuth } from '../auth.tsx'

const PRIORITIES = ['Low', 'Medium', 'High'] as const

export const assignmentsKey = (doctype: string, name: string) => ['assignments', doctype, name]

/** One assignment: who, what to do, how urgent. */
export function AssignmentRow({ a }: { a: AssignmentInfo }) {
  return (
    <>
      <Avatar name={a.userName || a.user} />
      <span className="person-text">
        <strong>{a.userName || a.user}</strong>
        <small className="muted">
          {a.description || 'No instructions'}
        </small>
        <small className="chips">
          <span className={`chip prio-${a.priority.toLowerCase()}`}><Icon name="flag" size={12} /> {a.priority}</span>
          {a.due && <span className="chip"><Icon name="calendar" size={12} /> {a.due}</span>}
          {a.status === 'Closed' && <span className="chip done"><Icon name="check" size={12} /> Done</span>}
        </small>
      </span>
    </>
  )
}

/** Assign a document to someone, and see who it is assigned to, in a dialog. */
export function AssignDialog({ doctype, name, canAssign, onClose }: { doctype: string; name: string; canAssign: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const { user: me } = useAuth()
  const key = assignmentsKey(doctype, name)
  const list = useQuery({ queryKey: key, queryFn: () => api.assignments.forDocument(doctype, name), retry: false })
  const [person, setPerson] = useState<LinkOption | null>(null)
  const [description, setDescription] = useState('')
  const [date, setDate] = useState('')
  const [priority, setPriority] = useState<(typeof PRIORITIES)[number]>('Medium')
  const [error, setError] = useState<string | null>(null)

  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: key }),
    queryClient.invalidateQueries({ queryKey: ['activity', doctype, name] }),
    queryClient.invalidateQueries({ queryKey: ['assignments', 'mine'] }),
  ])
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const assign = useMutation({
    mutationFn: () => api.assignments.assign(doctype, name, person!.value, description.trim(), date, priority),
    onSuccess: async () => { setPerson(null); setDescription(''); setDate(''); setPriority('Medium'); setError(null); await refresh() },
    onError: fail,
  })
  const complete = useMutation({ mutationFn: (id: string) => api.assignments.complete(id), onSuccess: refresh, onError: fail })
  const remove = useMutation({ mutationFn: (id: string) => api.assignments.remove(id), onSuccess: refresh, onError: fail })
  const busy = assign.isPending || complete.isPending || remove.isPending
  const items = list.data ?? []

  return (
    <Modal title="Assign" subtitle={`Ask someone to take care of ${doctype === name ? doctype : `${doctype} ${name}`}.`} onClose={onClose} dismissible={!busy}
      footer={canAssign ? (
        <>
          <button className="btn" onClick={onClose}>Close</button>
          <button className="btn primary" form="assign-form" disabled={!person || busy}><Icon name="userPlus" /> {assign.isPending ? 'Assigning…' : 'Assign'}</button>
        </>
      ) : <button className="btn" onClick={onClose}>Close</button>}>
      {error && <div className="banner error" role="alert">{error}</div>}
      {list.isPending && <p className="muted" role="status">Loading…</p>}

      <h3 className="modal-section">Assigned to</h3>
      <ul className="person-list">
        {items.map((a) => {
          const mine = a.user === me?.user || a.assignedBy === me?.user
          return (
            <li key={a.id} className={a.status === 'Closed' ? 'done' : undefined}>
              <AssignmentRow a={a} />
              {a.status === 'Open' && mine && (
                <span className="row-actions">
                  <IconButton icon="checkCircle" label="Mark as done" tone="primary" disabled={busy} onClick={() => complete.mutate(a.id)} />
                  <IconButton icon="trash" label="Remove assignment" tone="danger" disabled={busy} onClick={() => remove.mutate(a.id)} />
                </span>
              )}
            </li>
          )
        })}
        {list.data && items.length === 0 && <li className="empty-note">Nobody is assigned yet.</li>}
      </ul>

      {canAssign && (
        <form id="assign-form" className="modal-form" onSubmit={(e) => { e.preventDefault(); if (person && !busy) assign.mutate() }}>
          <h3 className="modal-section">New assignment</h3>
          <div className="field">
            <label>Person</label>
            <LinkSelect ariaLabel="Assign to" placeholder="Search by name or email…" value={person?.value ?? ''} knownLabel={person?.label}
              cacheKey={['assign-users', doctype, name]} search={(text) => api.share.users(doctype, name, text)}
              onChange={(_, option) => setPerson(option ?? null)} />
          </div>
          <div className="field">
            <label htmlFor="assign-what">What needs doing</label>
            <textarea id="assign-what" rows={3} maxLength={500} placeholder="Optional instructions" value={description} onChange={(e) => setDescription(e.target.value)} />
          </div>
          <div className="modal-row">
            <div className="field">
              <label htmlFor="assign-due">Due date</label>
              <input id="assign-due" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
            </div>
            <div className="field">
              <span id="assign-prio" className="label">Priority</span>
              <div className="segmented" role="radiogroup" aria-labelledby="assign-prio">
                {PRIORITIES.map((p) => (
                  <button key={p} type="button" role="radio" aria-checked={priority === p} className={priority === p ? `on prio-${p.toLowerCase()}` : undefined}
                    onClick={() => setPriority(p)}>{p}</button>
                ))}
              </div>
            </div>
          </div>
        </form>
      )}
    </Modal>
  )
}
