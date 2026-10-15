import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { TreeNode } from '../api/types.ts'

/** The documents of a tree DocType as an outline; a group's children load when it is opened. */
export function TreeView() {
  const { doctype = '' } = useParams()
  const metaQuery = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  const base = `/app/${encodeURIComponent(doctype)}`

  if (metaQuery.isError) return <div className="banner error" role="alert">You do not have access to "{doctype}", or it does not exist.</div>
  if (!metaQuery.data) return <p className="muted" role="status">Loading…</p>
  if (!metaQuery.data.data.isTree) return <div className="banner error" role="alert">{doctype} is not a tree.</div>

  return (
    <>
      <div className="page-head">
        <div>
          <Link to={base} className="muted back">← {doctype} list</Link>
          <h1>{doctype} tree</h1>
        </div>
        <span className="spacer" />
        {metaQuery.data.permissions.create && <Link className="btn primary" to={`${base}/new`}>+ New {doctype}</Link>}
      </div>
      <div className="card"><TreeLevel doctype={doctype} parent={null} /></div>
    </>
  )
}

function TreeLevel({ doctype, parent }: { doctype: string; parent: string | null }) {
  const nodes = useQuery({ queryKey: ['tree', doctype, parent], queryFn: () => api.tree(doctype, parent), retry: false })

  if (nodes.isPending) return <p className="muted" role="status">Loading…</p>
  if (nodes.isError) return <p className="error-text" role="alert">{nodes.error instanceof ApiError ? nodes.error.message : 'Could not load.'}</p>
  if (nodes.data.length === 0) return <p className="muted">{parent ? 'No children.' : 'Nothing here yet.'}</p>

  return (
    <ul className="tree-level" role={parent ? 'group' : 'tree'}>
      {nodes.data.map((node) => <TreeItem key={node.name} doctype={doctype} node={node} />)}
    </ul>
  )
}

function TreeItem({ doctype, node }: { doctype: string; node: TreeNode }) {
  const [open, setOpen] = useState(false)
  const isGroup = Boolean(node.is_group)

  return (
    <li role="treeitem" aria-expanded={isGroup ? open : undefined}>
      <div className="tree-row">
        {isGroup
          ? <button className="btn small icon" aria-label={`${open ? 'Collapse' : 'Expand'} ${node.name}`} onClick={() => setOpen(!open)}>{open ? '▾' : '▸'}</button>
          : <span className="tree-spacer" />}
        <Link to={`/app/${encodeURIComponent(doctype)}/${encodeURIComponent(node.name)}`}>{node.name}</Link>
        {isGroup && <span className="badge">group</span>}
      </div>
      {isGroup && open && <TreeLevel doctype={doctype} parent={node.name} />}
    </li>
  )
}
