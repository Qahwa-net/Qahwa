import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client.ts'

/** What has been assigned to me, on the home page. */
export function MyAssignments() {
  const queryClient = useQueryClient()
  const mine = useQuery({ queryKey: ['assignments', 'mine'], queryFn: api.assignments.mine, retry: false })
  const done = useMutation({
    mutationFn: (id: string) => api.assignments.complete(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['assignments', 'mine'] }),
  })

  if (!mine.data || mine.data.length === 0) return null
  return (
    <section className="card" aria-label="My assignments">
      <h3>Assigned to me</h3>
      <ul className="assignments">
        {mine.data.map((a) => (
          <li key={a.id}>
            <span>
              <Link to={`/app/${encodeURIComponent(a.docType ?? '')}/${encodeURIComponent(a.docName ?? '')}`}>{a.docType} {a.docName}</Link>
              {a.description && <> · {a.description}</>}
              <small className="muted"> · from {a.assignedByName || a.assignedBy} · {a.priority}{a.due ? ` · due ${a.due}` : ''}</small>
            </span>
            <button className="link-button" disabled={done.isPending} onClick={() => done.mutate(a.id)}>Done</button>
          </li>
        ))}
      </ul>
    </section>
  )
}
