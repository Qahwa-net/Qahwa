import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'

const SOURCE_LABEL = { Core: 'Framework', App: 'App', Custom: 'Custom' } as const

export function DesignerList() {
  const [filter, setFilter] = useState('')
  const items = useQuery({ queryKey: ['designer', 'list'], queryFn: api.designer.list, retry: false })

  const shown = useMemo(
    () => (items.data ?? []).filter((i) => i.name.toLowerCase().includes(filter.trim().toLowerCase())),
    [items.data, filter],
  )

  if (items.isError) {
    const forbidden = items.error instanceof ApiError && items.error.status === 403
    return <div className="banner error" role="alert">
      {forbidden ? 'Only a System Manager can design DocTypes.' : 'Could not load the DocTypes.'}
    </div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>DocType Designer</h1>
          <p className="muted">Create your own document types without writing code. Custom ones can be edited here; framework and app DocTypes are read-only.</p>
        </div>
        <span className="spacer" />
        <Link className="btn primary" to="/designer/new">+ New DocType</Link>
      </div>

      <div className="toolbar">
        <input type="search" className="search" placeholder="Filter DocTypes…" aria-label="Filter DocTypes"
          value={filter} onChange={(e) => setFilter(e.target.value)} />
      </div>

      <div className="table-wrap">
        <table>
          <thead>
            <tr><th>Name</th><th>Module</th><th>Source</th><th>Fields</th><th /></tr>
          </thead>
          <tbody>
            {shown.map((item) => (
              <tr key={item.name}>
                <td><Link to={`/designer/${encodeURIComponent(item.name)}`}>{item.name}</Link></td>
                <td>{item.module ?? ''}</td>
                <td>
                  <span className={`badge source-${item.source.toLowerCase()}`}>{SOURCE_LABEL[item.source]}</span>
                  {item.isChildTable && <span className="badge" title="Rows live inside other documents"> child table</span>}
                </td>
                <td>{item.fieldCount}</td>
                <td className="nowrap">
                  {item.isChildTable ? <span className="muted">used inside other DocTypes</span> : <Link to={`/app/${encodeURIComponent(item.name)}`}>Open documents</Link>}
                </td>
              </tr>
            ))}
            {items.isPending && <tr><td colSpan={5} className="empty">Loading…</td></tr>}
            {items.data && shown.length === 0 && <tr><td colSpan={5} className="empty">No DocTypes match.</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}
