import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { DashboardItem, DashboardResult, DocField } from '../api/types.ts'
import { BarChart, LineChart, PieChart } from '../controls/Charts.tsx'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { ConfirmDialog, Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { formatNumber } from '../lib/charts.ts'
import { FUNCTIONS, OPERATORS, dateFields, filterValue, groupableFields, newItem, numericFields, tileTitle, toItem } from '../lib/dashboards.ts'
import { STANDARD_FIELDS, labelOf } from '../lib/fields.ts'
import { Select } from '../controls/Select.tsx'

/** Dashboards: tiles of numbers, charts and lists over your documents, each one shown with the permissions of whoever is looking. */
export function DashboardsPage() {
  const { id } = useParams()
  return id ? <DashboardView id={id} /> : <DashboardList />
}

function DashboardList() {
  const navigate = useNavigate()
  const list = useQuery({ queryKey: ['dashboards'], queryFn: api.dashboards.list, retry: false })
  const [creating, setCreating] = useState(false)
  const items = list.data ?? []
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Dashboards</h1>
          <p className="muted">Numbers, charts and lists over your documents, always up to date. A shared dashboard shows each person only what they are allowed to see.</p>
        </div>
        <span className="spacer" />
        <button className="btn primary" onClick={() => setCreating(true)}><Icon name="plus" /> New dashboard</button>
      </div>
      {list.isPending && <p className="muted" role="status">Loading…</p>}
      {list.data && items.length === 0 && <p className="empty-note">No dashboards yet. Create one to start.</p>}
      <ul className="tiles dashboards-list">
        {items.map((d) => (
          <li key={d.id}>
            <Link className="card tile" to={`/dashboards/${encodeURIComponent(d.id)}`}>
              <strong><Icon name="chart" size={16} /> {d.title}</strong>
              <span className="muted">{d.items} tile{d.items === 1 ? '' : 's'}{d.isPublic ? ' · shared' : ''}{d.mine ? '' : ` · by ${d.ownerName || d.owner}`}</span>
            </Link>
          </li>
        ))}
      </ul>
      {creating && <DashboardEditor onClose={() => setCreating(false)} onSaved={(saved) => navigate(`/dashboards/${encodeURIComponent(saved)}`)} />}
    </>
  )
}

function DashboardView({ id }: { id: string }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const toast = useToast()
  const detail = useQuery({ queryKey: ['dashboard', id], queryFn: () => api.dashboards.get(id), retry: false })
  const data = useQuery({ queryKey: ['dashboard', 'data', id], queryFn: () => api.dashboards.data(id), retry: false, staleTime: 0 })
  const [editing, setEditing] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const remove = useMutation({
    mutationFn: () => api.dashboards.remove(id),
    onSuccess: async () => { toast('Dashboard deleted'); await queryClient.invalidateQueries({ queryKey: ['dashboards'] }); navigate('/dashboards') },
  })

  if (detail.isError) return <div className="banner error" role="alert">This dashboard does not exist, or is not shared with you. <Link to="/dashboards">Back to dashboards</Link></div>
  if (!detail.data) return <p className="muted" role="status">Loading…</p>
  const { info, items, canEdit } = detail.data
  
  return (
    <>
      <header className="doc-bar">
        <div className="doc-title">
          <Link to="/dashboards" className="icon-btn" aria-label="Back to dashboards" data-tip="Back to dashboards"><Icon name="arrowLeft" /></Link>
          <div className="doc-heading">
            <p className="doc-kicker">Dashboard{info.isPublic ? ' · shared' : ''}</p>
            <h1>{info.title}</h1>
          </div>
        </div>
        <div className="doc-actions">
          <IconButton icon="repeat" label="Refresh" disabled={data.isFetching} onClick={() => void data.refetch()} />
          {canEdit && <IconButton icon="pencil" label="Edit dashboard" onClick={() => setEditing(true)} />}
          {canEdit && <IconButton icon="trash" label="Delete dashboard" tone="danger" onClick={() => setDeleting(true)} />}
        </div>
      </header>
      {items.length === 0 && <p className="empty-note">This dashboard has no tiles yet.{canEdit && ' Use the pencil to add some.'}</p>}
      <TileGrid items={items} data={data.data} loading={data.isPending} />
      {editing && <DashboardEditor id={id} initial={detail.data} onClose={() => setEditing(false)} onSaved={() => setEditing(false)} />}
      {deleting && <ConfirmDialog title="Delete this dashboard?" tone="danger" confirmLabel="Delete" busy={remove.isPending} onClose={() => setDeleting(false)} onConfirm={() => remove.mutate()}
        message={`"${info.title}" will be deleted for everyone it is shared with.`} />}
    </>
  )
}

/** The tiles of a dashboard as a grid. */
export function TileGrid({ items, data, loading }: { items: DashboardItem[]; data?: DashboardResult[]; loading?: boolean }) {
  const results = new Map((data ?? []).map((r) => [r.id, r]))
  return (
    <div className="tile-grid">
      {items.map((item) => <Tile key={item.id} item={item} result={results.get(item.id)} loading={loading} />)}
    </div>
  )
}

/** A dashboard's tiles, loaded by id, for pages that embed one (the home page). */
export function DashboardTiles({ id }: { id: string }) {
  const detail = useQuery({ queryKey: ['dashboard', id], queryFn: () => api.dashboards.get(id), retry: false })
  const data = useQuery({ queryKey: ['dashboard', 'data', id], queryFn: () => api.dashboards.data(id), retry: false, staleTime: 0 })
  if (detail.isError) return null
  if (!detail.data) return <p className="muted" role="status">Loading…</p>
  return <TileGrid items={detail.data.items} data={data.data} loading={data.isPending} />
}

/** One tile. A tile the viewer may not see says so instead of showing anything. */
export function Tile({ item, result, loading }: { item: DashboardItem; result?: DashboardResult; loading?: boolean }) {
  const title = tileTitle(item)
  return (
    <section className={`card tile-card tile-${item.type.toLowerCase()} accent-${item.color}`} aria-label={title}>
      <h3>{title}</h3>
      {loading && !result && <div className="skeleton line input" role="status" aria-label="Loading" />}
      {result?.error && <p className="muted"><Icon name="lock" size={14} /> {result.error}</p>}
      {result && !result.error && <TileBody item={item} result={result} title={title} />}
      {result?.truncated && <small className="muted">Shows part of the data.</small>}
    </section>
  )
}

function TileBody({ item, result, title }: { item: DashboardItem; result: DashboardResult; title: string }) {
  if (item.type === 'Number') return <p className="big-number">{formatNumber(result.number ?? 0)}</p>
  if (item.type === 'Chart') {
    const points = (result.points ?? []).map((p) => ({ label: p.label, value: p.value }))
    if (points.length === 0) return <p className="muted">No data.</p>
    return item.chartType === 'pie' ? <PieChart points={points} title={title} /> : item.chartType === 'line' ? <LineChart points={points} title={title} /> : <BarChart points={points} title={title} />
  }
  const columns = result.columns ?? []
  const rows = result.rows ?? []
  if (rows.length === 0) return <p className="muted">No documents.</p>
  return (
    <div className="table-wrap">
      <table>
        <thead><tr>{columns.map((c) => <th key={c}>{c}</th>)}</tr></thead>
        <tbody>{rows.map((r, i) => <tr key={i}>{columns.map((c) => <td key={c}>{String(r[c] ?? '')}</td>)}</tr>)}</tbody>
      </table>
    </div>
  )
}

// ---- the editor ----

function DashboardEditor({ id, initial, onClose, onSaved }: {
  id?: string
  initial?: { info: { title: string; isPublic: boolean; modified: string }; items: DashboardItem[] }
  onClose: () => void
  onSaved: (id: string) => void
}) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const metas = useQuery({ queryKey: ['metas'], queryFn: api.metas })
  const [title, setTitle] = useState(initial?.info.title ?? '')
  const [isPublic, setIsPublic] = useState(initial?.info.isPublic ?? false)
  const [items, setItems] = useState<DashboardItem[]>(() => structuredClone(initial?.items ?? []))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<string | null>(null)
  const [open, setOpen] = useState(0)

  const prepare = async () => Promise.all(items.map(async (item) => {
    const meta = item.docType ? await queryClient.ensureQueryData({ queryKey: ['meta', item.docType], queryFn: () => api.meta(item.docType), staleTime: 60_000 }).catch(() => null) : null
    const types = new Map([...STANDARD_FIELDS, ...(meta?.data.fields ?? [])].map((f) => [f.name, f.type]))
    const prepared = toItem(item)
    return { ...prepared, filters: prepared.filters.map((f) => ({ ...f, value: f.op === 'is' ? f.value : filterValue(f, types.get(f.field)) })) }
  }))

  const save = useMutation({
    mutationFn: async () => {
      const body = { title: title.trim(), isPublic, items: await prepare() }
      return id ? api.dashboards.update(id, { ...body, modified: initial?.info.modified }) : api.dashboards.create(body)
    },
    onSuccess: async (saved) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['dashboards'] }),
        queryClient.invalidateQueries({ queryKey: ['dashboard'] }),
      ])
      toast('Dashboard saved')
      onSaved(saved.id)
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner('Could not save the dashboard.')
      setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
      const general = error.details.filter((d) => !d.field).map((d) => d.message).join(' ')
      setBanner(general || (error.details.length > 0 ? 'Please fix the highlighted tiles.' : error.message))
      const first = error.details.map((d) => /^items\[(\d+)\]/.exec(d.field ?? '')).find(Boolean)
      if (first) setOpen(Number(first[1]))
    },
  })

  const update = (index: number, patch: Partial<DashboardItem>) => setItems(items.map((it, i) => (i === index ? { ...it, ...patch } : it)))

  return (
    <Modal title={id ? 'Edit dashboard' : 'New dashboard'} size="lg" onClose={onClose} dismissible={!save.isPending}
      footer={<>
        <button className="btn" onClick={onClose} disabled={save.isPending}>Cancel</button>
        <button className="btn primary" disabled={save.isPending || !title.trim()} onClick={() => { setBanner(null); setErrors({}); save.mutate() }}>
          <Icon name="save" /> {save.isPending ? 'Saving…' : 'Save'}
        </button>
      </>}>
      {banner && <div className="banner error" role="alert">{banner}</div>}
      <div className="modal-row">
        <div className="field">
          <label htmlFor="dash-title">Title</label>
          <input id="dash-title" data-autofocus maxLength={100} value={title} aria-invalid={Boolean(errors.title)} onChange={(e) => setTitle(e.target.value)} />
          {errors.title && <small className="error-text" role="alert">{errors.title}</small>}
        </div>
        <label className="switch"><input type="checkbox" checked={isPublic} onChange={(e) => setIsPublic(e.target.checked)} /><span>Share with everyone</span></label>
      </div>
      {errors.items && <div className="banner error" role="alert">{errors.items}</div>}

      <h3 className="modal-section">Tiles</h3>
      <ol className="rule-list">
        {items.map((item, i) => (
          <ItemEditor key={i} index={i} item={item} open={open === i} onToggle={() => setOpen(open === i ? -1 : i)}
            errors={Object.fromEntries(Object.entries(errors).filter(([k]) => k.startsWith(`items[${i}].`)).map(([k, v]) => [k.slice(`items[${i}].`.length), v]))}
            doctypes={(metas.data ?? []).map((m) => m.name)} onChange={(patch) => update(i, patch)} prepare={async () => (await prepare())[i]!}
            onRemove={() => { setItems(items.filter((_, j) => j !== i)); setOpen(-1) }} />
        ))}
      </ol>
      {items.length === 0 && <p className="empty-note">No tiles yet.</p>}
      <div className="form-actions">
        <button className="btn" onClick={() => { setItems([...items, newItem('Number')]); setOpen(items.length) }}><Icon name="plus" /> Add a tile</button>
      </div>
    </Modal>
  )
}

function ItemEditor({ index, item, open, errors, doctypes, onToggle, onChange, onRemove, prepare }: {
  index: number
  item: DashboardItem
  open: boolean
  errors: Record<string, string>
  doctypes: string[]
  onToggle: () => void
  onChange: (patch: Partial<DashboardItem>) => void
  onRemove: () => void
  prepare: () => Promise<DashboardItem>
}) {
  const meta = useQuery({ queryKey: ['meta', item.docType], queryFn: () => api.meta(item.docType), enabled: Boolean(item.docType) && open, retry: false, staleTime: 60_000 })
  const fields: DocField[] = meta.data?.data.fields ?? []
  const withStandard = [...fields.filter((f) => groupableFields([f]).length > 0), ...STANDARD_FIELDS]
  const [preview, setPreview] = useState<DashboardResult | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const run = useMutation({
    mutationFn: async () => api.dashboards.preview(await prepare()),
    onSuccess: (r) => { setPreview(r); setPreviewError(null) },
    onError: (e) => { setPreview(null); setPreviewError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not preview.') },
  })
  const label = `Tile ${index + 1}`
  const Err = ({ k }: { k: string }) => (errors[k] ? <small className="error-text" role="alert">{errors[k]}</small> : null)
  const summary = item.type !== 'List'

  return (
    <li className={`card rule-card${Object.keys(errors).length ? ' has-error' : ''}`}>
      <header className="rule-head">
        <span className="rule-no" aria-hidden="true">{index + 1}</span>
        <button type="button" className="link-button rule-name" aria-expanded={open} onClick={onToggle}>{tileTitle(item)} <small className="muted">· {item.type}</small></button>
        <IconButton icon="trash" label={`Remove ${label}`} tone="danger" onClick={onRemove} />
      </header>
      {open && (
        <div className="form-grid">
          <div className="field">
            <label>Type</label>
            <Select aria-label={`${label} type`} value={item.type} onChange={(e) => onChange({ type: e.target.value as DashboardItem['type'] })}>
              <option value="Number">Number</option><option value="Chart">Chart</option><option value="List">List of documents</option>
            </Select>
            <Err k="type" />
          </div>
          <div className="field">
            <label htmlFor={`t${index}-title`}>Title <span className="muted">(optional)</span></label>
            <input id={`t${index}-title`} maxLength={100} value={item.title} onChange={(e) => onChange({ title: e.target.value })} />
            <Err k="title" />
          </div>
          <div className="field">
            <label>DocType</label>
            <Select aria-label={`${label} DocType`} value={item.docType} aria-invalid={Boolean(errors.docType)} onChange={(e) => onChange({ docType: e.target.value, valueField: null, groupBy: null, columns: [], orderBy: null, filters: [] })}>
              <option value="">— choose —</option>
              {doctypes.map((d) => <option key={d} value={d}>{d}</option>)}
            </Select>
            <Err k="docType" />
          </div>
          <div className="field">
            <label>Colour</label>
            <Select aria-label={`${label} colour`} value={item.color} onChange={(e) => onChange({ color: e.target.value as DashboardItem['color'] })}>
              {['blue', 'green', 'orange', 'red', 'purple', 'gray'].map((c) => <option key={c}>{c}</option>)}
            </Select>
          </div>

          {summary && (
            <>
              <div className="field">
                <label>Show</label>
                <Select aria-label={`${label} function`} value={item.function} onChange={(e) => onChange({ function: e.target.value as DashboardItem['function'] })}>
                  {FUNCTIONS.map((f) => <option key={f.value} value={f.value}>{f.label}</option>)}
                </Select>
                <Err k="function" />
              </div>
              {item.function !== 'count' && (
                <div className="field">
                  <label>Of the field</label>
                  <Select aria-label={`${label} value field`} value={item.valueField ?? ''} aria-invalid={Boolean(errors.valueField)} onChange={(e) => onChange({ valueField: e.target.value })}>
                    <option value="">— choose —</option>
                    {numericFields(fields).map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
                  </Select>
                  <Err k="valueField" />
                </div>
              )}
            </>
          )}

          {item.type === 'Chart' && (
            <>
              <div className="field">
                <label>Group by</label>
                <Select aria-label={`${label} group by`} value={item.groupBy ?? ''} aria-invalid={Boolean(errors.groupBy)} onChange={(e) => onChange({ groupBy: e.target.value, period: null })}>
                  <option value="">— choose —</option>
                  {groupableFields(fields).map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
                </Select>
                <Err k="groupBy" />
              </div>
              {dateFields(fields).some((f) => f.name === item.groupBy) && (
                <div className="field">
                  <label>Per</label>
                  <Select aria-label={`${label} period`} value={item.period ?? 'day'} onChange={(e) => onChange({ period: e.target.value as 'day' | 'month' | 'year' })}>
                    <option value="day">Day</option><option value="month">Month</option><option value="year">Year</option>
                  </Select>
                  <Err k="period" />
                </div>
              )}
              <div className="field">
                <label>Chart</label>
                <Select aria-label={`${label} chart type`} value={item.chartType} onChange={(e) => onChange({ chartType: e.target.value as DashboardItem['chartType'] })}>
                  <option value="bar">Bars</option><option value="line">Line</option><option value="pie">Pie</option>
                </Select>
                <Err k="chartType" />
              </div>
            </>
          )}

          {item.type === 'List' && (
            <>
              <fieldset className="field full check-group">
                <legend>Fields to show</legend>
                <div className="check-row">
                  {groupableFields(fields).map((f) => (
                    <label key={f.name} className="switch">
                      <input type="checkbox" checked={item.columns.includes(f.name)} onChange={(e) => onChange({ columns: e.target.checked ? [...item.columns, f.name] : item.columns.filter((c) => c !== f.name) })} />
                      <span>{labelOf(f)}</span>
                    </label>
                  ))}
                </div>
                <Err k="columns" />
              </fieldset>
              <div className="field">
                <label>Newest first by</label>
                <Select aria-label={`${label} order by`} value={item.orderBy ?? ''} onChange={(e) => onChange({ orderBy: e.target.value })}>
                  <option value="">Last changed</option>
                  {withStandard.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
                </Select>
              </div>
              <div className="field">
                <label htmlFor={`t${index}-limit`}>Rows</label>
                <input id={`t${index}-limit`} type="number" min={1} max={50} value={item.limit} onChange={(e) => onChange({ limit: Number(e.target.value) || 10 })} />
                <Err k="limit" />
              </div>
            </>
          )}

          <div className="field full">
            <label>Only documents where</label>
            {item.filters.map((f, fi) => (
              <div key={fi} className="filter-row">
                <Select aria-label={`${label} filter ${fi + 1} field`} value={f.field} onChange={(e) => onChange({ filters: item.filters.map((x, j) => (j === fi ? { ...x, field: e.target.value } : x)) })}>
                  <option value="">— field —</option>
                  {withStandard.map((fld) => <option key={fld.name} value={fld.name}>{labelOf(fld)}</option>)}
                </Select>
                <Select aria-label={`${label} filter ${fi + 1} operator`} value={f.op} onChange={(e) => onChange({ filters: item.filters.map((x, j) => (j === fi ? { ...x, op: e.target.value } : x)) })}>
                  {OPERATORS.map((o) => <option key={o}>{o}</option>)}
                </Select>
                {f.op === 'is'
                  ? (
                    <Select aria-label={`${label} filter ${fi + 1} value`} value={String(f.value || 'set')} onChange={(e) => onChange({ filters: item.filters.map((x, j) => (j === fi ? { ...x, value: e.target.value } : x)) })}>
                      <option value="set">is set</option><option value="not set">is not set</option>
                    </Select>
                  )
                  : <input aria-label={`${label} filter ${fi + 1} value`} value={String(f.value ?? '')} onChange={(e) => onChange({ filters: item.filters.map((x, j) => (j === fi ? { ...x, value: e.target.value } : x)) })} />}
                <IconButton icon="x" label="Remove filter" onClick={() => onChange({ filters: item.filters.filter((_, j) => j !== fi) })} />
                {errors[`filters[${fi}].field`] && <small className="error-text" role="alert">{errors[`filters[${fi}].field`]}</small>}
              </div>
            ))}
            <button type="button" className="btn small" onClick={() => onChange({ filters: [...item.filters, { field: '', op: '=', value: '' }] })}><Icon name="plus" size={14} /> Add a condition</button>
          </div>

          <div className="field full">
            <button type="button" className="btn small" disabled={run.isPending || !item.docType} onClick={() => run.mutate()}><Icon name="play" size={14} /> {run.isPending ? 'Working…' : 'Preview'}</button>
            {previewError && <small className="error-text" role="alert">{previewError}</small>}
            {preview && <div className="preview-box"><Tile item={item} result={preview} /></div>}
          </div>
        </div>
      )}
    </li>
  )
}
