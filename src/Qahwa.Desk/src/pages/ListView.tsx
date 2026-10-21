import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useQueries, useQuery } from '@tanstack/react-query'
import { Link, Navigate, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import { BulkBar, type BulkReport } from '../controls/BulkBar.tsx'
import { useListScripts } from '../lib/useListScripts.ts'
import { ColumnsPanel } from '../controls/ColumnsPanel.tsx'
import { FilterBar } from '../controls/FilterBar.tsx'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { GroupBySidebar } from '../controls/GroupBySidebar.tsx'
import { RowStatus } from '../controls/StatusPill.tsx'
import { SavedFilters } from '../controls/SavedFilters.tsx'
import { ReportPanel } from './ReportPanel.tsx'
import { ImportDialog } from './ImportDialog.tsx'
import { STANDARD_FIELDS, formatCell, hasColumn, labelOf } from '../lib/fields.ts'
import { useI18n } from '../i18n.tsx'
import { decodeFilters, encodeFilters, type Filter } from '../lib/filters.ts'

const PAGE_SIZE = 20

export function ListView() {
  useI18n() // translatable values in the cells follow the language
  const { doctype = '' } = useParams()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const [reporting, setReporting] = useState(false)
  const [importing, setImporting] = useState(false)
  const [columnsOpen, setColumnsOpen] = useState(false)
  const [groupOpen, setGroupOpen] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [bulkReport, setBulkReport] = useState<BulkReport | null>(null)

  const metaQuery = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  const meta = metaQuery.data?.data
  const perms = metaQuery.data?.permissions
  // My own way of seeing this list (columns, page size, sort) and the filters I can pick from.
  // The first time a DocType is opened in a session it can land on its default view (ADR-111); the list icon always comes back here.
  const defaultView = meta?.defaultView
  useEffect(() => {
    if (!defaultView || defaultView === 'list') return
    const flag = `qahwa-default-view:${doctype}`
    try {
      if (window.sessionStorage.getItem(flag)) return
      window.sessionStorage.setItem(flag, '1')
    } catch {
      return
    }
    navigate(`/app/${encodeURIComponent(doctype)}/${defaultView}`, { replace: true })
  }, [defaultView, doctype, navigate])
  const viewState = useQuery({ queryKey: ['list-settings', doctype], queryFn: () => api.listSettings.get(doctype), retry: false })
  const settings = viewState.data?.settings ?? null
  const pageSize = settings?.pageSize ?? PAGE_SIZE

  const allFields = useMemo<DocField[]>(
    () => (meta ? [STANDARD_FIELDS[0]!, ...meta.fields.filter((f) => hasColumn(f.type) && f.type !== 'Password'), ...STANDARD_FIELDS.slice(1)] : []),
    [meta],
  )

  const columns = useMemo<DocField[]>(() => {
    if (!meta) return []
    if (settings && settings.columns.length > 0) {
      const byName = new Map(allFields.map((f) => [f.name, f]))
      return settings.columns.filter((n) => n !== 'name' && n !== 'modified').map((n) => byName.get(n)).filter((f): f is DocField => Boolean(f))
    }
    const data = meta.fields.filter((f) => hasColumn(f.type) && f.type !== 'Password')
    const chosen = data.filter((f) => f.inListView)
    return (chosen.length > 0 ? chosen : data.slice(0, 4)).slice(0, 6)
  }, [meta, settings, allFields])

  // With my own columns the title still names each row; with the usual ones it is shown when it is a list column.
  const titleField = meta?.titleField && (settings || columns.some((c) => c.name === meta.titleField)) ? meta.titleField : null
  const otherColumns = columns.filter((c) => c.name !== titleField)
  const searchField = meta?.titleField ?? 'name'

  const filters = useMemo(() => decodeFilters(params.get('f')), [params])
  const q = params.get('q') ?? ''
  const page = Math.max(1, Number(params.get('page')) || 1)
  const sortable = new Set(['name', 'modified', ...(titleField ? [titleField] : []), ...columns.map((c) => c.name)])
  const typeSort = meta?.sortField && sortable.has(meta?.sortField) ? meta?.sortField : 'modified'
  const defaultSort = settings?.sortField && sortable.has(settings.sortField) ? settings.sortField : typeSort
  const sort = sortable.has(params.get('sort') ?? '') ? params.get('sort')! : defaultSort
  const descending = params.has('dir') ? params.get('dir') !== 'asc' : (settings?.sortDescending ?? meta?.sortDescending ?? true)

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
  const fields = ['name', ...new Set([
    ...(titleField ? [titleField] : []), ...otherColumns.map((c) => c.name), 'modified',
    ...(meta?.statusField ? [meta.statusField] : []), ...(meta?.isSubmittable ? ['docstatus'] : []),
  ])]

  // What is ticked belongs to the rows on screen: a new page, filter or sort starts afresh.
  const filtersKey = encodeFilters(effectiveFilters)
  useEffect(() => setSelected(new Set()), [doctype, page, filtersKey, sort, descending])

  const list = useQuery({
    queryKey: ['list', doctype, fields, encodeFilters(effectiveFilters), sort, descending, page, pageSize],
    enabled: Boolean(meta) && perms?.read !== false,
    placeholderData: keepPreviousData,
    retry: false,
    queryFn: () =>
      api.list(doctype, {
        fields, filters: effectiveFilters, orderBy: sort, descending, limit: pageSize, offset: (page - 1) * pageSize,
      }),
  })

  // Link columns show the linked document's friendly name instead of its ID.
  const linkColumns = columns.filter((c) => c.type === 'Link' && c.options)
  const labelQueries = useQueries({
    queries: linkColumns.map((c) => {
      const names = [...new Set((list.data?.data ?? []).map((d) => d[c.name]).filter((v): v is string => typeof v === 'string' && v !== ''))].sort()
      return {
        queryKey: ['link', c.options, 'titles', names],
        queryFn: () => api.linkTitles(c.options!, names),
        enabled: names.length > 0,
        retry: false,
        staleTime: 60_000,
      }
    }),
  })
  const cellText = (column: DocField, value: unknown): string => {
    const index = linkColumns.findIndex((c) => c.name === column.name)
    const label = index >= 0 && typeof value === 'string' ? labelQueries[index]?.data?.[value]?.label : undefined
    return label ?? formatCell(column, value)
  }

  // How many comments each row on screen has (only documents the server lets me read are counted).
  const rowNames = (list.data?.data ?? []).map((d) => d.name)
  const commentCounts = useQuery({
    queryKey: ['comment-counts', doctype, rowNames],
    queryFn: () => api.commentCounts(doctype, rowNames),
    enabled: rowNames.length > 0 && meta?.trackChanges !== false,
    retry: false,
  })

  // Client scripts of this DocType for the list (buttons, a note, row colours, a filter).
  const listScripts = useListScripts({
    meta, rows: list.data?.data, selected: [...selected],
    setFilter: (field, value) => update({ f: encodeFilters([...filters.filter((f) => f.field !== field), { field, op: '=', value }]) }),
  })

  if (metaQuery.isError) {
    const forbidden = metaQuery.error instanceof ApiError && (metaQuery.error.status === 403 || metaQuery.error.status === 404)
    return <div className="banner error" role="alert">
      {forbidden ? `You do not have access to "${doctype}", or it does not exist.` : 'Could not load this DocType.'}
    </div>
  }
  if (!meta) return <p className="muted" role="status">Loading…</p>
  // A single DocType has one document and no list: go straight to it.
  if (meta.isSingle) return <Navigate to={`/app/${encodeURIComponent(doctype)}/${encodeURIComponent(doctype)}`} replace />

  const total = list.data?.total ?? 0
  const first = total === 0 ? 0 : (page - 1) * pageSize + 1
  const last = Math.min(page * pageSize, total)
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
        <IconButton icon="group" label="Group by" aria-expanded={groupOpen} pressed={groupOpen} onClick={() => setGroupOpen(!groupOpen)} />
        <IconButton icon="columns" label="Columns" aria-expanded={columnsOpen} pressed={columnsOpen} onClick={() => setColumnsOpen(!columnsOpen)} />
        {meta.isTree && <Link className="icon-btn" aria-label="Tree view" data-tip="Tree view" to={`${base}/tree`}><Icon name="tree" /></Link>}
        {meta.fields.some((f) => f.type === "Select" && f.options && !f.hidden) && !meta.isSingle && <Link className="icon-btn" aria-label="Board" data-tip="Board" to={`${base}/board`}><Icon name="board" /></Link>}
        {meta.fields.some((f) => (f.type === "Date" || f.type === "Datetime") && !f.hidden) && !meta.isSingle && <Link className="icon-btn" aria-label="Calendar" data-tip="Calendar" to={`${base}/calendar`}><Icon name="calendar" /></Link>}
        {meta.fields.filter((f) => (f.type === "Date" || f.type === "Datetime") && !f.hidden).length >= 2 && !meta.isSingle && <Link className="icon-btn" aria-label="Timeline" data-tip="Timeline" to={`${base}/timeline`}><Icon name="timeline" /></Link>}
        {meta.fields.some((f) => f.type === "AttachImage" && !f.hidden) && !meta.isSingle && <Link className="icon-btn" aria-label="Gallery" data-tip="Gallery" to={`${base}/gallery`}><Icon name="gallery" /></Link>}
        {meta.fields.some((f) => f.type === "Geolocation" && !f.hidden) && !meta.isSingle && <Link className="icon-btn" aria-label="Map" data-tip="Map" to={`${base}/map`}><Icon name="map" /></Link>}
        {listScripts.buttons.map((b) => <IconButton key={b.id} icon="play" label={b.label} onClick={() => listScripts.runButton(b.id)} />)}
        {perms?.report && <IconButton icon="chart" label="Report" aria-expanded={reporting} pressed={reporting} onClick={() => setReporting(!reporting)} />}
        {perms?.export && <a className="icon-btn" aria-label="Export CSV" data-tip="Export CSV" href={api.exportUrl(doctype, effectiveFilters)} download><Icon name="fileCsv" /></a>}
        {perms?.export && <a className="icon-btn" aria-label="Export Excel" data-tip="Export Excel" href={api.exportUrl(doctype, effectiveFilters, 'xlsx')} download><Icon name="fileExcel" /></a>}
        {perms?.create && perms?.import !== false && !meta.isSingle && <IconButton icon="upload" label="Import" onClick={() => setImporting(true)} />}
        {perms?.create && <Link className="icon-btn primary" aria-label={`New ${doctype}`} data-tip={`New ${doctype}`} to={`${base}/new`}><Icon name="plus" /></Link>}
      </div>

      {importing && <ImportDialog doctype={doctype} canUpdate={Boolean(perms?.write)} onClose={() => setImporting(false)} />}

      {columnsOpen && (
        <ColumnsPanel key={settings ? 'saved' : 'default'} doctype={doctype} fields={allFields.filter((f) => f.name !== 'name')}
          current={[...otherColumns.map((c) => c.name)]} initial={settings} onClose={() => setColumnsOpen(false)} />
      )}
      {reporting && <ReportPanel doctype={doctype} fields={allFields} filters={effectiveFilters} onClose={() => setReporting(false)} />}

      {listScripts.intro && <div className="banner info" role="status">{listScripts.intro}</div>}
      {listScripts.notice && <div className="banner info" role="status">{listScripts.notice} <button type="button" className="btn small" onClick={listScripts.dismissNotice}>OK</button></div>}

      <div className="toolbar">
        <input type="search" className="search" placeholder={`Search by ${searchField.replace(/_/g, ' ')}…`}
          aria-label="Search" value={searchText} onChange={(e) => setSearchText(e.target.value)} />
        <FilterBar fields={allFields} filters={filters} onChange={(next) => update({ f: encodeFilters(next) })} />
        <SavedFilters doctype={doctype} saved={viewState.data?.filters ?? []} current={filters} canShare={Boolean(perms?.write)}
          onApply={(next) => update({ f: encodeFilters(next) })} />
      </div>

      {selected.size > 0 && (
        <BulkBar doctype={doctype} names={[...selected]} canWrite={Boolean(perms?.write)} canDelete={Boolean(perms?.delete)} canRename={Boolean(meta.allowRename && perms?.write)}
          fields={meta.fields.filter((f) => hasColumn(f.type) && f.type !== 'Password' && !f.formula && !f.readOnly)}
          onDone={() => setSelected(new Set())} onClear={() => setSelected(new Set())} onResult={setBulkReport} />
      )}
      {bulkReport && (
        <div className={`banner ${bulkReport.failed.length > 0 ? 'error' : 'info'}`} role="status">
          {bulkReport.ok} done{bulkReport.failed.length > 0 && <>, {bulkReport.failed.length} could not be changed:
            <ul>{bulkReport.failed.slice(0, 5).map((r) => <li key={r.name}><strong>{r.name}</strong>: {r.error}</li>)}</ul></>}
          {' '}<button className="link-button" onClick={() => setBulkReport(null)}>Dismiss</button>
        </div>
      )}

      {list.isError && (
        <div className="banner error" role="alert">
          {list.error instanceof ApiError ? list.error.message : 'Could not load documents.'}
        </div>
      )}

      <div className={groupOpen ? 'list-layout' : undefined}>
      {groupOpen && (
        <GroupBySidebar doctype={doctype} filters={effectiveFilters} applied={filters}
          fields={[...meta.fields.filter((f) => ['Select', 'Link', 'Check'].includes(f.type) || f.inStandardFilter), ...STANDARD_FIELDS.filter((f) => f.name === 'owner' || (f.name === 'docstatus' && meta.isSubmittable))]}
          onChange={(next) => update({ f: encodeFilters(next) })} />
      )}
      <div className="list-main">
      <div className={`table-wrap${list.isFetching ? ' busy' : ''}`}>
        <table>
          <thead>
            <tr>
              <th className="select-cell">
                <input type="checkbox" aria-label="Select all on this page"
                  checked={Boolean(list.data?.data.length) && list.data!.data.every((d) => selected.has(d.name))}
                  onChange={(e) => setSelected(e.target.checked ? new Set(list.data?.data.map((d) => d.name)) : new Set())} />
              </th>
              {sortHeader(titleField ?? 'name', titleField ? labelOf(allFields.find((f) => f.name === titleField) ?? STANDARD_FIELDS[0]!) : 'Name')}
              {otherColumns.map((c) => sortHeader(c.name, labelOf(c)))}
              {sortHeader('modified', 'Last Modified')}
            </tr>
          </thead>
          <tbody>
            {list.data?.data.map((doc) => (
              <tr key={doc.name} onClick={() => navigate(`${base}/${encodeURIComponent(doc.name)}`)}>
                <td className="select-cell" onClick={(e) => e.stopPropagation()}>
                  <input type="checkbox" aria-label={`Select ${doc.name}`} checked={selected.has(doc.name)}
                    onChange={(e) => setSelected((s) => { const next = new Set(s); if (e.target.checked) next.add(doc.name); else next.delete(doc.name); return next })} />
                </td>
                <td>
                  <Link to={`${base}/${encodeURIComponent(doc.name)}`} onClick={(e) => e.stopPropagation()}>
                    {String((titleField ? doc[titleField] : null) || doc.name)}
                  </Link>
                  {(commentCounts.data?.[doc.name] ?? 0) > 0 && (
                    <span className="comment-count" title={`${commentCounts.data![doc.name]} comment(s)`} aria-label={`${commentCounts.data![doc.name]} comments`}> 💬 {commentCounts.data![doc.name]}</span>
                  )}
                  <RowStatus meta={meta} doc={doc} />
                  {listScripts.indicators[doc.name] && <span className={`pill pill-${listScripts.indicators[doc.name]}`} aria-label={`Marked ${listScripts.indicators[doc.name]}`}>●</span>}
                  {titleField && <div className="muted small-text">{doc.name}</div>}
                </td>
                {otherColumns.map((c) => <td key={c.name}>{cellText(c, doc[c.name])}</td>)}
                <td className="nowrap">{formatCell({ ...STANDARD_FIELDS[4]! }, doc.modified)}</td>
              </tr>
            ))}
            {list.data && list.data.data.length === 0 && (
              <tr><td colSpan={otherColumns.length + 3} className="empty">
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
      </div>
      </div>
    </>
  )
}
