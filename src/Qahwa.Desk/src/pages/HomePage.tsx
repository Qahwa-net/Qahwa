import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client.ts'
import { useAuth } from '../auth.tsx'
import { MyAssignments } from './MyAssignments.tsx'
import { WorkflowInbox } from './WorkflowInbox.tsx'
import { WorkspaceHome } from './WorkspaceHome.tsx'

export function HomePage() {
  const { user } = useAuth()
  const metas = useQuery({ queryKey: ['metas'], queryFn: api.metas })

  return (
    <>
      <h1>Welcome, {user?.full_name}</h1>
      <WorkflowInbox />
      <MyAssignments />
      <WorkspaceHome />
      <p className="muted">Pick a DocType to see its documents.</p>
      <div className="tiles">
        {metas.data?.map((meta) => (
          <Link key={meta.name} className="card tile" to={`/app/${encodeURIComponent(meta.name)}`}>
            <strong>{meta.name}</strong>
            <span className="muted">{meta.module ?? 'Other'}</span>
          </Link>
        ))}
      </div>
    </>
  )
}
