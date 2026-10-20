import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client.ts'
import { groupByModule, type ModuleDef } from '../lib/modules.ts'
import { useAuth } from '../auth.tsx'
import { MyAssignments } from './MyAssignments.tsx'
import { WorkflowInbox } from './WorkflowInbox.tsx'
import { WorkspaceHome } from './WorkspaceHome.tsx'

export function HomePage() {
  const { user } = useAuth()
  const metas = useQuery({ queryKey: ['metas'], queryFn: api.metas })
  const defs = useQuery({
    queryKey: ['module-definitions'], retry: false,
    queryFn: async () => (await api.list('Module Definition', { fields: ['module_name', 'label', 'sort_order', 'description', 'hidden'], filters: [], orderBy: 'module_name', descending: false, limit: 200, offset: 0 })).data as unknown as ModuleDef[],
  })
  const groups = groupByModule(metas.data ?? [], defs.data ?? [])

  return (
    <>
      <h1>Welcome, {user?.full_name}</h1>
      <WorkflowInbox />
      <MyAssignments />
      <WorkspaceHome />
      <p className="muted">Pick a DocType to see its documents.</p>
      {groups.map((group) => (
        <section key={group.module || 'other'} aria-label={group.label}>
          <h2>{group.label}</h2>
          {group.description && <p className="muted">{group.description}</p>}
          <div className="tiles">
            {group.items.map((meta) => (
              <Link key={meta.name} className="card tile" to={`/app/${encodeURIComponent(meta.name)}`}>
                <strong>{meta.name}</strong>
              </Link>
            ))}
          </div>
        </section>
      ))}
    </>
  )
}
