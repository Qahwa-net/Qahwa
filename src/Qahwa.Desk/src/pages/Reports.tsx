import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { DocField, ReportRun } from '../api/types.ts'
import { LinkSelect } from '../controls/LinkSelect.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { formatCell, labelOf } from '../lib/fields.ts'
import {
  FUNCTIONS, OPERATORS, draftProblem, fromDefinition, isNumeric, newDraft, reportFields, toDefinition, toggle, type ReportDraft,
} from '../lib/reports.ts'
import { Select } from '../controls/Select.tsx'

const messageOf = (e: unknown): string =>
  e instanceof ApiError ? (e.details.length > 0 ? e.details.map((d) => d.message).join(' ') : e.message) : 'Something went wrong.'

/** The table a report produces. Cells are shown as text, formatted by the field's type. */
function ReportTable({ run }: { run: ReportRun }) {
  if (run.rows.length === 0) return <p className="muted">No documents match.</p>
  return (
    <>
      {run.truncated && <div className="banner info" role="status">Only the first rows are shown. Add a filter to narrow it down.</div>}
      <div className="table-wrap">
        <table className="grid">
          <thead><tr>{run.columns.map((c) => <th key={c.key}>{c.label}</th>)}</tr></thead>
          <tbody>
            {run.rows.map((row, i) => (
              <tr key={i}>
                {run.columns.map((c) => (
                  <td key={c.key} className={['Currency', 'Float', 'Int', 'Decimal', 'Percent'].includes(c.type) ? 'num' : undefined}>
                    {formatCell({ name: c.key, type: c.type } as DocField, row[c.key])}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

/** The reports you can open: your own and the ones shared with everyone. */
export function ReportsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const list = useQuery({ queryKey: ['reports'], queryFn: api.reports.list, retry: false })
  const [removing, setRemoving] = useState<{ id: string; title: string } | null>(null)
  const remove = useMutation({
    mutationFn: (id: string) => api.reports.remove(id),
    onSuccess: () => { setRemoving(null); toast('Report deleted.'); void queryClient.invalidateQueries({ queryKey: ['reports'] }) },
    onError: (e) => { setRemoving(null); toast(messageOf(e)) },
  })

  return (
    <>
      <div className="page-head">
        <div><h1>Reports</h1><p className="muted">Lists and summaries of your documents. A report always shows each person only what they are allowed to see.</p></div>
        <span className="spacer" />
        <Link className="btn primary" to="/reports/new">New report</Link>
      </div>
      {list.isError && <div className="banner error" role="alert">Could not load the reports.</div>}
      {list.data?.length === 0 && <p className="muted">There are no reports yet. Make one with <strong>New report</strong>.</p>}
      {list.data && list.data.length > 0 && (
        <table className="grid">
          <thead><tr><th>Report</th><th>DocType</th><th>By</th><th>Shared</th><th /></tr></thead>
          <tbody>
            {list.data.map((r) => (
              <tr key={r.id}>
                <td><Link to={`/reports/${r.id}`}>{r.title}</Link></td>
                <td>{r.docType}</td>
                <td>{r.mine ? 'You' : (r.ownerName ?? r.owner)}</td>
                <td>{r.isPublic ? 'Everyone' : 'Only you'}</td>
                <td className="actions">
                  {r.mine && <><Link className="btn small" to={`/reports/${r.id}/edit`}>Edit</Link>{' '}<button className="btn small" onClick={() => setRemoving({ id: r.id, title: r.title })}>Delete</button></>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {removing && <ConfirmDialog title="Delete this report?" message={`“${removing.title}” will be deleted for everyone. Your documents are not affected.`} confirmLabel="Delete" tone="danger" busy={remove.isPending} onClose={() => setRemoving(null)} onConfirm={() => remove.mutate(removing.id)} />}
    </>
  )
}

/** Opens a saved report: runs it for you and offers a download. */
export function ReportViewPage() {
  const { id = '' } = useParams()
  const detail = useQuery({ queryKey: ['report', id], queryFn: () => api.reports.get(id), retry: false })
  const run = useQuery({ queryKey: ['report', id, 'run'], queryFn: () => api.reports.run(id), retry: false, staleTime: 0 })

  if (detail.isError) return <div className="banner error" role="alert">This report was not found, or you may not open it.</div>
  return (
    <>
      <div className="page-head">
        <div><Link to="/reports" className="muted back">← Reports</Link><h1>{detail.data?.info.title ?? 'Report'}</h1>
          {detail.data && <p className="muted">{detail.data.info.docType} · by {detail.data.info.mine ? 'you' : (detail.data.info.ownerName ?? detail.data.info.owner)}</p>}</div>
        <span className="spacer" />
        <button className="btn" onClick={() => void run.refetch()} disabled={run.isFetching}>Refresh</button>
        <a className="btn" href={api.reports.csvUrl(id)} download>Download CSV</a>
        <a className="btn" href={api.reports.xlsxUrl(id)} download>Download Excel</a>
        {detail.data?.canEdit && <Link className="btn primary" to={`/reports/${id}/edit`}>Edit</Link>}
      </div>
      {run.isError && <div className="banner error" role="alert">{run.error instanceof ApiError && run.error.status === 403 ? 'You do not have access to the documents in this report.' : messageOf(run.error)}</div>}
      {run.isPending && <p className="muted" role="status">Loading…</p>}
      {run.data && <ReportTable run={run.data} />}
    </>
  )
}

/** Build or change a report, with a live preview that runs with your own permissions. */
export function ReportEditorPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const toast = useToast()
  const existing = useQuery({ queryKey: ['report', id], queryFn: () => api.reports.get(id!), enabled: Boolean(id), retry: false })
  const [draft, setDraft] = useState<ReportDraft>(newDraft())
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [preview, setPreview] = useState<ReportRun | null>(null)

  useEffect(() => {
    if (existing.data && !loaded) {
      setDraft(fromDefinition(existing.data.info.title, existing.data.info.isPublic, existing.data.definition))
      setLoaded(true)
    }
  }, [existing.data, loaded])

  const meta = useQuery({ queryKey: ['meta', draft.docType], queryFn: () => api.meta(draft.docType), enabled: Boolean(draft.docType), retry: false })
  const fields = useMemo(() => reportFields(meta.data?.data.fields ?? []), [meta.data])
  const numeric = useMemo(() => fields.filter(isNumeric), [fields])
  const set = (patch: Partial<ReportDraft>) => { setDraft({ ...draft, ...patch }); setPreview(null) }
  const problem = draftProblem(draft)

  const run = useMutation({ mutationFn: () => api.reports.preview(toDefinition(draft)), onSuccess: (r) => { setError(null); setPreview(r) }, onError: (e) => setError(messageOf(e)) })
  const save = useMutation({
    mutationFn: async () => {
      const body = { title: draft.title, isPublic: draft.isPublic, definition: toDefinition(draft) }
      return id ? api.reports.update(id, body, existing.data!.info.modified) : api.reports.create(body)
    },
    onSuccess: (saved) => {
      toast('Report saved.')
      void queryClient.invalidateQueries({ queryKey: ['reports'] })
      void queryClient.invalidateQueries({ queryKey: ['report'] })
      navigate(`/reports/${saved.id}`)
    },
    onError: (e) => setError(messageOf(e)),
  })

  if (id && existing.isError) return <div className="banner error" role="alert">This report was not found, or you may not change it.</div>

  const updateFilter = (i: number, patch: Partial<ReportDraft['filters'][number]>) => set({ filters: draft.filters.map((f, n) => (n === i ? { ...f, ...patch } : f)) })
  const updateAggregate = (i: number, patch: Partial<ReportDraft['aggregates'][number]>) => set({ aggregates: draft.aggregates.map((a, n) => (n === i ? { ...a, ...patch } : a)) })

  return (
    <>
      <div className="page-head"><div><Link to="/reports" className="muted back">← Reports</Link><h1>{id ? 'Change report' : 'New report'}</h1></div></div>
      {error && <div className="banner error" role="alert">{error}</div>}

      <section className="card pad">
        <label className="field">Title<input value={draft.title} maxLength={100} onChange={(e) => setDraft({ ...draft, title: e.target.value })} /></label>
        <label className="field check"><input type="checkbox" checked={draft.isPublic} onChange={(e) => setDraft({ ...draft, isPublic: e.target.checked })} /> Share with everyone (each person still only sees what they are allowed to see)</label>
        <div className="field"><span>DocType</span>
          <LinkSelect value={draft.docType} cacheKey={['link', 'DocType']} ariaLabel="DocType" search={(text) => api.linkSearch('DocType', text)}
            onChange={(next) => setDraft({ ...newDraft(next), title: draft.title, isPublic: draft.isPublic })} />
        </div>
      </section>

      {draft.docType && meta.data && (
        <>
          <section className="card pad">
            <div role="radiogroup" aria-label="Kind of report" className="segmented">
              <label><input type="radio" name="mode" checked={draft.mode === 'list'} onChange={() => set({ mode: 'list' })} /> A list of documents</label>
              <label><input type="radio" name="mode" checked={draft.mode === 'summary'} onChange={() => set({ mode: 'summary' })} /> A summary (numbers per group)</label>
            </div>

            {draft.mode === 'list' ? (
              <fieldset className="checks"><legend>Columns to show</legend>
                {[{ name: 'name', label: 'ID' } as { name: string; label: string }, ...fields.map((f) => ({ name: f.name, label: labelOf(f) }))].map((f) => (
                  <label key={f.name}><input type="checkbox" checked={draft.columns.includes(f.name)} onChange={() => set({ columns: toggle(draft.columns, f.name) })} /> {f.label}</label>
                ))}
              </fieldset>
            ) : (
              <>
                <label className="field">Group by
                  <Select value={draft.groupBy} onChange={(e) => set({ groupBy: e.target.value })}>
                    <option value="" />{fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
                  </Select></label>
                <h3>Numbers to show</h3>
                {draft.aggregates.map((a, i) => (
                  <div className="row" key={i}>
                    <Select aria-label="Function" value={a.function} onChange={(e) => updateAggregate(i, { function: e.target.value })}>
                      {FUNCTIONS.filter((f) => f.value === 'count' || numeric.length > 0).map((f) => <option key={f.value} value={f.value}>{f.label}</option>)}
                    </Select>
                    {a.function !== 'count' && (
                      <Select aria-label="Field" value={a.field} onChange={(e) => updateAggregate(i, { field: e.target.value })}>
                        <option value="" />{numeric.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
                      </Select>
                    )}
                    <button type="button" className="btn small" aria-label="Remove this number" onClick={() => set({ aggregates: draft.aggregates.filter((_, n) => n !== i) })}>✕</button>
                  </div>
                ))}
                {draft.aggregates.length < 5 && <button type="button" className="btn small" onClick={() => set({ aggregates: [...draft.aggregates, { function: 'sum', field: numeric[0]?.name ?? '' }] })}>Add a number</button>}
              </>
            )}
          </section>

          <section className="card pad">
            <h3>Only documents where</h3>
            {draft.filters.map((f, i) => (
              <div className="row" key={i}>
                <Select aria-label="Field" value={f.field} onChange={(e) => updateFilter(i, { field: e.target.value })}>
                  <option value="" />{fields.map((x) => <option key={x.name} value={x.name}>{labelOf(x)}</option>)}
                </Select>
                <Select aria-label="Operator" value={f.op} onChange={(e) => updateFilter(i, { op: e.target.value, value: e.target.value === 'is' ? 'set' : f.value })}>
                  {OPERATORS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </Select>
                {f.op === 'is'
                  ? <Select aria-label="Value" value={f.value} onChange={(e) => updateFilter(i, { value: e.target.value })}><option value="set">set</option><option value="not set">not set</option></Select>
                  : <input aria-label="Value" value={f.value} maxLength={500} onChange={(e) => updateFilter(i, { value: e.target.value })} />}
                <button type="button" className="btn small" aria-label="Remove this filter" onClick={() => set({ filters: draft.filters.filter((_, n) => n !== i) })}>✕</button>
              </div>
            ))}
            {draft.filters.length < 10 && <button type="button" className="btn small" onClick={() => set({ filters: [...draft.filters, { field: fields[0]?.name ?? '', op: '=', value: '' }] })}>Add a filter</button>}
            {draft.mode === 'list' && (
              <div className="row">
                <label>Order by <Select value={draft.orderBy} onChange={(e) => set({ orderBy: e.target.value })}><option value="">Newest changes</option>{fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}</Select></label>
                <label><input type="checkbox" checked={draft.descending} onChange={(e) => set({ descending: e.target.checked })} /> highest first</label>
                <label>Rows <input type="number" min={1} max={1000} value={draft.limit} onChange={(e) => set({ limit: Math.max(1, Math.min(1000, Number(e.target.value) || 1)) })} /></label>
              </div>
            )}
          </section>
        </>
      )}

      <div className="row">
        <button className="btn" onClick={() => run.mutate()} disabled={Boolean(problem) || run.isPending} title={problem ?? undefined}>Preview</button>
        <button className="btn primary" onClick={() => save.mutate()} disabled={Boolean(problem) || draft.title.trim() === '' || save.isPending} title={problem ?? (draft.title.trim() === '' ? 'Give the report a title.' : undefined)}>Save</button>
      </div>
      {preview && <section aria-label="Preview"><h2>Preview</h2><ReportTable run={preview} /></section>}
    </>
  )
}
