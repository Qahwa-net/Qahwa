import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { AssignmentInfo, LinkOption } from '../api/types.ts'
import { LinkSelect } from '../controls/LinkSelect.tsx'
import { useAuth } from '../auth.tsx'

/** Who is asked to do something about this document, with a way to ask someone else. */
export function AssignPanel({ doctype, name, canAssign }: { doctype: string; name: string; canAssign: boolean }) {
  const queryClient = useQueryClient()
  const { user: me } = useAuth()
  const key = ['assignments', doctype, name]
  const list = useQuery({ queryKey: key, queryFn: () => api.assignments.forDocument(doctype, name), retry: false })
  const [person, setPerson] = useState<LinkOption | null>(null)
  const [description, setDescription] = useState('')
  const [date, setDate] = useState('')
  const [priority, setPriority] = useState('Medium')
  const [error, setError] = useState<string | null>(null)

  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: key }),
    queryClient.invalidateQueries({ queryKey: ['activity', doctype, name] }),
    queryClient.invalidateQueries({ queryKey: ['assignments', 'mine'] }),
  ])
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const assign = useMutation({
    mutationFn: () => api.assignments.assign(doctype, name, person!.value, description.trim(), date, priority),
    onSuccess: async () => { setPerson(null); setDescription(''); setDate(''); setError(null); await refresh() },
    onError: fail,
  })
  const complete = useMutation({ mutationFn: (id: string) => api.assignments.complete(id), onSuccess: refresh, onError: fail })
  const remove = useMutation({ mutationFn: (id: string) => api.assignments.remove(id), onSuccess: refresh, onError: fail })

  const items = list.data ?? []
  if (list.isError || (!canAssign && items.length === 0)) return null

  return (
    <section className="card assign" aria-label="Assignments">
      <h3>Assigned to</h3>
      {error && <div className="banner error" role="alert">{error}</div>}
      <ul className="assignments">
        {items.map((a: AssignmentInfo) => (
          <li key={a.id} className={a.status === 'Closed' ? 'done' : undefined}>
            <span><strong>{a.userName || a.user}</strong>{a.description && <> · {a.description}</>}
              <small className="muted"> · {a.priority}{a.due ? ` · due ${a.due}` : ''}{a.status === 'Closed' ? ' · done' : ''}</small></span>
            {a.status === 'Open' && (
              <span className="comment-actions">
                {(a.user === me?.user || a.assignedBy === me?.user) && <button className="link-button" disabled={complete.isPending} onClick={() => complete.mutate(a.id)}>Done</button>}
                {(a.user === me?.user || a.assignedBy === me?.user) && <button className="link-button" disabled={remove.isPending} onClick={() => remove.mutate(a.id)}>Remove</button>}
              </span>
            )}
          </li>
        ))}
        {items.length === 0 && <li className="muted">Nobody yet.</li>}
      </ul>

      {canAssign && (
        <form className="assign-form" onSubmit={(e) => { e.preventDefault(); if (person) assign.mutate() }}>
          <div className="share-user">
            <LinkSelect ariaLabel="Assign to" placeholder="Assign to… (search by name or email)" value={person?.value ?? ''} knownLabel={person?.label}
              cacheKey={['assign-users', doctype, name]} search={(text) => api.share.users(doctype, name, text)}
              onChange={(_, option) => setPerson(option ?? null)} />
          </div>
          <input aria-label="What to do" placeholder="What needs doing?" maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
          <input type="date" aria-label="Due date" value={date} onChange={(e) => setDate(e.target.value)} />
          <select aria-label="Priority" value={priority} onChange={(e) => setPriority(e.target.value)}>
            <option>Low</option><option>Medium</option><option>High</option>
          </select>
          <button className="btn" disabled={!person || assign.isPending}>Assign</button>
        </form>
      )}
    </section>
  )
}
