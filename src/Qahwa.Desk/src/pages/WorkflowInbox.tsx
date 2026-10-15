import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client.ts'
import { StatusPill } from '../controls/StatusPill.tsx'

/** Documents waiting for something only I can do, on the home page. */
export function WorkflowInbox() {
  const pending = useQuery({ queryKey: ['workflow', 'pending'], queryFn: api.workflows.pending, retry: false })
  if (!pending.data || pending.data.length === 0) return null
  return (
    <section className="card" aria-label="Waiting for my action">
      <h3>Waiting for my action</h3>
      <ul className="assignments">
        {pending.data.map((p) => (
          <li key={`${p.docType}/${p.name}`}>
            <span>
              <Link to={`/app/${encodeURIComponent(p.docType)}/${encodeURIComponent(p.name)}`}>{p.docType} {p.title || p.name}</Link>{' '}
              <StatusPill text={p.state} color={p.color} />
              <small className="muted"> · {p.actions.map((a) => a.action).join(', ')}</small>
            </span>
          </li>
        ))}
      </ul>
    </section>
  )
}
