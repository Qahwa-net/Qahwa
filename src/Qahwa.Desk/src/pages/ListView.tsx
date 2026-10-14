import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import { FilterBar } from '../controls/FilterBar.tsx'
import { STANDARD_FIELDS, formatCell, hasColumn, labelOf } from '../lib/fields.ts'
import { decodeFilters, encodeFilters, type Filter } from '../lib/filters.ts'

const PAGE_SIZE = 20

export function ListView() {
  const { doctype = '' } = useParams()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()

  const metaQuery = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  const meta = metaQuery.data?.data
  const perms = metaQuery.data?.permissions

  const allFields = useMemo<DocField[]>(
    () => (meta ? [STANDARD_FIELDS[0]!, ...meta.fields.filter((f) => hasColumn(f.type) && f.type !== 'Password'), ...STANDARD_FIELDS.slice(1)] : []),
    [meta],
  )

  const columns = useMemo<DocField[]>(() => {
    if (!meta) return []
    const data = meta.fields.filter((f) => hasColumn(f.type) && f.type !== 'Password')
    const chosen = data.filter((f) => f.inListView)
    return (chosen.length > 0 ? chosen : data.slice(0, 4)).slice(0, 6)
  }, [meta])

  const titleField = meta?.titleField && columns.some((c) => c.name === meta.titleField) ? meta.titleField : null
  const otherColumns = columns.filter((c) => c.name !== titleField)
  const searchField = meta?.titleField ?? 'name'

  const filters = useMemo(() => decodeFilters(params.get('f')), [params])
  const q = params.get('q') ?? ''
  const page = Math.max(1, Number(params.get('page')) || 1)
  const sortable = new Set(['name', 'modified', ...columns.map((c) => c.name)])
  const sort = sortable.has(params.get('sort') ?? '') ? params.get('sort')! : 'modified'
  const descending = params.get('dir') !== 'asc'

  function update(changes: Record<string, string | null>, resetPage = true) {
    const next = new URLSearchParams(params)
    for (const [key, value] of Object.entries(changes)) {
      if (value === null || value === '') next.delete(key)
      else next.set(key, value)
    }
    if (resetPage) next.delete('page')
    setParams(next, { replace: true })
  }

  // The search box edits local text; the URL (and therefore the query) follows after a short pause.
  const [searchText, setSearchText] = useState(q)
  useEffect(() => {
    const timer = setTimeout(() => {
      if (searchText !== q) update({ q: searchText })
    }, 300)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchText])

  const effectiveFilters: Filter[] = q ? [...filters, { field: searchField, op: 'like', value: q }] : filters
  const fields = ['name', ...new Set([...(titleField ? [titleField] : []), ...otherColumns.map((c) => c.name), 'modified'])]

  const list = useQuery({
    queryKey: ['list', doctype, fields, encodeFilters(effectiveFilters), sort, descending, page],
    enabled: Boolean(meta) && perms?.read !== false,
    placeholderData: keepPreviousData,
    retry: false,
    queryFn: () =>
      api.list(doctype, {
        fields, filters: effectiveFilters, orderBy: sort, descending, limit: PAGE_SIZE, offset: (page - 1) * PAGE_SIZE,
      }),
  })

  if (metaQuery.isError) {
    const forbidden = metaQuery.error instanceof ApiError && (metaQuery.error.status === 403 || metaQuery.error.status === 404)
    return <div className="banner error" role="alert">
      {forbidden ? `You do not have access to "${doctype}", or it does not exist.` : 'Could not load this DocType.'}
    </div>
  }
  if (!meta) return <p className="muted" role="status">Loading…</p>

  const total = list.data?.total ?? 0
  const first = total === 0 ? 0 : (page - 1) * PAGE_SIZE + 1
  const last = Math.min(page * PAGE_SIZE, total)
  const base = `/app/${encodeURIComponent(doctype)}`

  const sortHeader = (name: string, label: string) => (
    <th key={name} aria-sort={sort === name ? (descending ? 'descending' : 'ascending') : 'none'}>
      <button className="th-button"
        onClick={() => update({ sort: name, dir: sort === name ? (descending ? 'asc' : 'desc') : 'asc' })}>
        {label}{sort === name ? (descending ? ' ↓' : ' ↑') : ''}
      </button>
    </th>
  )

  return (
    <>
      <div className="page-head">
        <h1>{doctype}</h1>
        <span className="spacer" />
        {perms?.create && <Link className="btn primary" to={`${base}/new`}>+ New {doctype}</Link>}
      </div>

      <div className="toolbar">
        <input type="search" className="search" placeholder={`Search by ${searchField.replace(/_/g, ' ')}…`}
          aria-label="Search" value={searchText} onChange={(e) => setSearchText(e.target.value)} />
        <FilterBar fields={allFields} filters={filters} onChange={(next) => update({ f: encodeFilters(next) })} />
      </div>

      {list.isError && (
        <div className="banner error" role="alert">
          {list.error instanceof ApiError ? list.error.message : 'Could not load documents.'}
        </div>
      )}

      <div className={`table-wrap${list.isFetching ? ' busy' : ''}`}>
        <table>
          <thead>
            <tr>
              {sortHeader(titleField ?? 'name', titleField ? labelOf(columns.find((c) => c.name === titleField)!) : 'Name')}
              {otherColumns.map((c) => sortHeader(c.name, labelOf(c)))}
              {sortHeader('modified', 'Last Modified')}
            </tr>
          </thead>
          <tbody>
            {list.data?.data.map((doc) => (
              <tr key={doc.name} onClick={() => navigate(`${base}/${encodeURIComponent(doc.name)}`)}>
                <td>
                  <Link to={`${base}/${encodeURIComponent(doc.name)}`} onClick={(e) => e.stopPropagation()}>
                    {String((titleField ? doc[titleField] : null) || doc.name)}
                  </Link>
                  {titleField && <div className="muted small-text">{doc.name}</div>}
                </td>
                {otherColumns.map((c) => <td key={c.name}>{formatCell(c, doc[c.name])}</td>)}
                <td className="nowrap">{formatCell({ ...STANDARD_FIELDS[4]! }, doc.modified)}</td>
              </tr>
            ))}
            {list.data && list.data.data.length === 0 && (
              <tr><td colSpan={otherColumns.length + 2} className="empty">
                {effectiveFilters.length > 0 ? 'No documents match these filters.' : `No ${doctype} documents yet.`}
              </td></tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="pager">
        <span className="muted">{total === 0 ? '0 documents' : `${first}–${last} of ${total}`}</span>
        <span className="spacer" />
        <button className="btn small" disabled={page <= 1} onClick={() => update({ page: String(page - 1) }, false)}>Previous</button>
        <button className="btn small" disabled={last >= total} onClick={() => update({ page: String(page + 1) }, false)}>Next</button>
      </div>
    </>
  )
}
