import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client.ts'
import { groupByModule, type ModuleDef } from '../lib/modules.ts'

/** A business module's own page, like a Frappe workspace: its description and a card for each DocType in it. */
export function ModulePage() {
  const { name = '' } = useParams()
  const metas = useQuery({ queryKey: ['metas'], queryFn: api.metas })
  const defs = useQuery({
    queryKey: ['module-definitions'], retry: false,
    queryFn: async () => (await api.list('Module Definition', { fields: ['module_name', 'label', 'sort_order', 'description', 'hidden'], filters: [], orderBy: 'module_name', descending: false, limit: 200, offset: 0 })).data as unknown as ModuleDef[],
  })
  if (metas.isPending) return <p className="muted" role="status">Loading…</p>
  const group = groupByModule(metas.data ?? [], defs.data ?? []).find((g) => g.module === name || g.label === name)
  if (!group) return <div className="banner info" role="status">There is nothing called {name} that you can open. <Link to="/">Back to Home</Link></div>
  return (
    <>
      <nav className="breadcrumb" aria-label="Breadcrumb"><Link to="/">Home</Link> <span aria-hidden="true">›</span> <span>{group.label}</span></nav>
      <h1>{group.label}</h1>
      {group.description && <p className="muted">{group.description}</p>}
      <div className="tiles">
        {group.items.map((meta) => (
          <Link key={meta.name} className="card tile" to={`/app/${encodeURIComponent(meta.name)}`}><strong>{meta.name}</strong></Link>
        ))}
      </div>
    </>
  )
}
