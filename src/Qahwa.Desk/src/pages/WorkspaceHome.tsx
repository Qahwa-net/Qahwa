import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client.ts'
import type { WorkspaceBlockView } from '../api/types.ts'
import { Icon } from '../controls/Icon.tsx'
import { DashboardTiles } from './Dashboards.tsx'

/** The workspaces made for this person, as tabs on the home page. Nothing is shown when there are none. */
export function WorkspaceHome() {
  const list = useQuery({ queryKey: ['workspaces', 'mine'], queryFn: api.workspaces.mine, retry: false })
  const [picked, setPicked] = useState<string | null>(null)
  const workspaces = list.data ?? []
  if (workspaces.length === 0) return null
  const current = workspaces.find((w) => w.id === picked) ?? workspaces[0]!

  return (
    <section className="workspace" aria-label="Workspaces">
      {workspaces.length > 1 && (
        <div className="tabs" role="tablist" aria-label="Workspaces">
          {workspaces.map((w) => (
            <button key={w.id} type="button" role="tab" aria-selected={w.id === current.id} className={w.id === current.id ? 'on' : undefined} onClick={() => setPicked(w.id)}>{w.title}</button>
          ))}
        </div>
      )}
      {workspaces.length === 1 && <h2>{current.title}</h2>}
      {current.blocks.map((block, i) => <Block key={i} block={block} />)}
    </section>
  )
}

function Block({ block }: { block: WorkspaceBlockView }) {
  if (block.type === 'Heading') return <h3 className="workspace-heading">{block.text}</h3>
  if (block.type === 'Dashboard' && block.dashboardId) {
    return (
      <div className="workspace-dashboard">
        <h3 className="workspace-heading"><Icon name="chart" size={16} /> <Link to={`/dashboards/${encodeURIComponent(block.dashboardId)}`}>{block.dashboardTitle}</Link></h3>
        <DashboardTiles id={block.dashboardId} />
      </div>
    )
  }
  return (
    <ul className="tiles workspace-links">
      {(block.shortcuts ?? []).map((s, i) => (
        <li key={i}><Link className="card tile" to={s.path}><strong>{s.label}</strong><span className="muted">{s.kind === 'DocType' ? 'Documents' : s.kind}</span></Link></li>
      ))}
    </ul>
  )
}
