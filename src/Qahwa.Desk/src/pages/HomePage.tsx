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
      <h2>Modules</h2>
      <p className="muted">Open a module to see its DocTypes.</p>
      <div className="tiles">
        {groups.map((group) => (
          <Link key={group.module || 'other'} className="card tile" to={`/module/${encodeURIComponent(group.module || group.label)}`} aria-label={group.label}>
            <strong>{group.label}</strong>
            <span className="muted">{group.description || `${group.items.length} ${group.items.length === 1 ? 'DocType' : 'DocTypes'}`}</span>
          </Link>
        ))}
      </div>
    </>
  )
}
