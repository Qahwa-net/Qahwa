import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client.ts'
import { Block } from './WorkspaceHome.tsx'

/** One workspace opened from the sidebar: its heading and blocks (shortcuts, text, dashboards), as a page of its own. */
export function WorkspacePage() {
  const { id = '' } = useParams()
  const list = useQuery({ queryKey: ['workspaces', 'mine'], queryFn: api.workspaces.mine, retry: false })
  if (list.isPending) return <p className="muted" role="status">Loading…</p>
  const workspace = (list.data ?? []).find((w) => w.id === id)
  const parent = workspace?.parent ? (list.data ?? []).find((w) => w.id === workspace.parent) : undefined
  if (!workspace) return <div className="banner info" role="status">This workspace is not available to you. <Link to="/">Back to Home</Link></div>
  return (
    <section className="workspace" aria-label={workspace.title}>
      <nav className="breadcrumb" aria-label="Breadcrumb"><Link to="/">Home</Link> <span aria-hidden="true">›</span> {parent && <><Link to={`/workspace/${encodeURIComponent(parent.id)}`}>{parent.title}</Link> <span aria-hidden="true">›</span> </>}<span>{workspace.title}</span></nav>
      <h1>{workspace.title}</h1>
      {workspace.blocks.length === 0 && <p className="muted">This workspace has no blocks yet.</p>}
      {workspace.blocks.map((block, i) => <Block key={i} block={block} />)}
    </section>
  )
}
