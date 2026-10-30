import { useMemo, useRef } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import { Icon } from '../controls/Icon.tsx'
import { dayNumber, layout } from '../lib/gantt.ts'
import { labelOf } from '../lib/fields.ts'
import { mapUrl } from '../lib/fieldtypes.ts'
import { layoutMap } from '../lib/map.ts'
import { Select } from '../controls/Select.tsx'

const LIMIT = 500

/** What every view needs: the DocType, its permissions and the documents with a few chosen fields. */
function useDocs(fields: (meta: DocField[]) => string[]) {
  const { doctype = '' } = useParams()
  const metaQuery = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  const meta = metaQuery.data?.data
  const wanted = useMemo(() => (meta ? fields(meta.fields) : []), [meta, fields])
  const titleField = meta?.titleField && meta.fields.some((f) => f.name === meta.titleField) ? meta.titleField : null
  const columns = useMemo(() => ['name', ...(titleField ? [titleField] : []), ...wanted], [titleField, wanted])
  const list = useQuery({
    queryKey: ['view-docs', doctype, columns],
    queryFn: () => api.list(doctype, { fields: columns, filters: [], orderBy: 'modified', descending: true, limit: LIMIT, offset: 0 }),
    enabled: Boolean(meta) && wanted.length > 0,
    retry: false,
  })
  const titleOf = (row: Record<string, unknown>) => String((titleField && row[titleField]) || row.name)
  return { doctype, meta, perms: metaQuery.data?.permissions, metaQuery, list, titleOf, base: `/app/${encodeURIComponent(doctype)}` }
}

function Head({ doctype, base, what, children }: { doctype: string; base: string; what: string; children?: React.ReactNode }) {
  return (
    <div className="page-head">
      <div><Link to={base} className="muted back">← {doctype} list</Link><h1>{doctype} {what}</h1></div>
      <span className="spacer" />
      {children}
    </div>
  )
}

const Denied = ({ doctype }: { doctype: string }) => <div className="banner error" role="alert">You do not have access to "{doctype}", or it does not exist.</div>

const pictureFields = (fields: DocField[]) => fields.filter((f) => f.type === 'AttachImage' && !f.hidden)
const placeFields = (fields: DocField[]) => fields.filter((f) => f.type === 'Geolocation' && !f.hidden)
const dateFields = (fields: DocField[]) => fields.filter((f) => (f.type === 'Date' || f.type === 'Datetime') && !f.hidden)

/** A wall of pictures: one tile for each document, from one of its Image fields. */
export function GalleryView() {
  const [params, setParams] = useSearchParams()
  const preferred = useRef<string | undefined>(undefined)
  const picker = useMemo(() => (fields: DocField[]) => {
    const by = pictureFields(fields)
    const chosen = by.find((f) => f.name === params.get('by')) ?? by.find((f) => f.name === preferred.current) ?? by[0]
    return chosen ? [chosen.name] : []
  }, [params])
  const { doctype, meta, perms, metaQuery, list, titleOf, base } = useDocs(picker)
  const fields = pictureFields(meta?.fields ?? [])
  preferred.current = meta?.imageField ?? undefined
  const by = fields.find((f) => f.name === params.get('by')) ?? fields.find((f) => f.name === meta?.imageField) ?? fields[0]
  const rows = (list.data?.data ?? []) as Record<string, unknown>[]
  const withPictures = rows.filter((r) => typeof r[by?.name ?? ''] === 'string' && r[by!.name] !== '')

  if (metaQuery.isError) return <Denied doctype={doctype} />
  if (!meta) return <p className="muted" role="status">Loading…</p>
  if (!by) return <div className="banner info" role="status">{doctype} has no Image field. Add one in the DocType Designer. <Link to={base}>Back to the list</Link></div>

  return (
    <>
      <Head doctype={doctype} base={base} what="gallery">
        {fields.length > 1 && <label className="inline-field">Picture from <Select value={by.name} onChange={(e) => setParams({ by: e.target.value })}>{fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}</Select></label>}
        {perms?.create && <Link className="btn primary" to={`${base}/new`}><Icon name="plus" /> New {doctype}</Link>}
      </Head>
      {list.isPending && <p className="muted" role="status">Loading…</p>}
      {list.data && withPictures.length === 0 && <p className="muted">No document has a picture in "{labelOf(by)}" yet.</p>}
      {list.data && rows.length >= LIMIT && <div className="banner info" role="status">Showing the {LIMIT} most recently changed documents.</div>}
      <ul className="gallery">
        {withPictures.map((r) => (
          <li key={String(r.name)}>
            <Link to={`${base}/${encodeURIComponent(String(r.name))}`} title={titleOf(r)}>
              <img src={api.files.url(String(r[by.name]))} alt={titleOf(r)} loading="lazy" />
              <span>{titleOf(r)}</span>
            </Link>
          </li>
        ))}
      </ul>
    </>
  )
}

/** Places on a plain map (a grid of latitude and longitude, no outside map service), with a link to a full map for each. */
export function MapView() {
  const [params, setParams] = useSearchParams()
  const picker = useMemo(() => (fields: DocField[]) => {
    const by = placeFields(fields)
    const chosen = by.find((f) => f.name === params.get('by')) ?? by[0]
    return chosen ? [chosen.name] : []
  }, [params])
  const { doctype, meta, perms, metaQuery, list, titleOf, base } = useDocs(picker)
  const fields = placeFields(meta?.fields ?? [])
  const by = fields.find((f) => f.name === params.get('by')) ?? fields[0]
  const rows = (list.data?.data ?? []) as Record<string, unknown>[]
  const shown = useMemo(() => layoutMap(rows.map((r) => ({ name: String(r.name), title: titleOf(r), place: r[by?.name ?? ''] }))), [rows, by])

  if (metaQuery.isError) return <Denied doctype={doctype} />
  if (!meta) return <p className="muted" role="status">Loading…</p>
  if (!by) return <div className="banner info" role="status">{doctype} has no Geolocation field. Add one in the DocType Designer. <Link to={base}>Back to the list</Link></div>

  return (
    <>
      <Head doctype={doctype} base={base} what="map">
        {fields.length > 1 && <label className="inline-field">Place from <Select value={by.name} onChange={(e) => setParams({ by: e.target.value })}>{fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}</Select></label>}
        {perms?.create && <Link className="btn primary" to={`${base}/new`}><Icon name="plus" /> New {doctype}</Link>}
      </Head>
      {list.isPending && <p className="muted" role="status">Loading…</p>}
      {list.data && shown.points.length === 0 && <p className="muted">No document has a place in "{labelOf(by)}" yet.</p>}
      {shown.skipped > 0 && shown.points.length > 0 && <p className="muted small-text">{shown.skipped} document{shown.skipped === 1 ? ' has' : 's have'} no place and {shown.skipped === 1 ? 'is' : 'are'} not shown.</p>}
      {shown.points.length > 0 && (
        <>
          <svg className="plot" viewBox="0 0 100 100" preserveAspectRatio="xMidYMid meet" role="img" aria-label={`${shown.points.length} places`}>
            {shown.grid.map((g) => g.vertical
              ? <g key={`v${g.label}`}><line x1={g.at} x2={g.at} y1={0} y2={100} className="grid-line" /><text x={g.at} y={99} className="grid-text">{g.label}</text></g>
              : <g key={`h${g.label}`}><line y1={g.at} y2={g.at} x1={0} x2={100} className="grid-line" /><text x={0.5} y={g.at - 0.5} className="grid-text">{g.label}</text></g>)}
            {shown.points.map((p) => (
              <Link key={p.name} to={`${base}/${encodeURIComponent(p.name)}`}>
                <circle cx={p.x} cy={p.y} r={1.4} className="dot"><title>{p.title}</title></circle>
              </Link>
            ))}
          </svg>
          <table className="grid">
            <thead><tr><th>{doctype}</th><th>Latitude</th><th>Longitude</th><th /></tr></thead>
            <tbody>
              {shown.points.map((p) => (
                <tr key={p.name}><td><Link to={`${base}/${encodeURIComponent(p.name)}`}>{p.title}</Link></td><td>{p.lat}</td><td>{p.lng}</td>
                  <td><a href={mapUrl(p)} target="_blank" rel="noopener noreferrer">View on a map</a></td></tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </>
  )
}

/** Documents as bars from a start date to an end date. */
export function GanttView() {
  const [params, setParams] = useSearchParams()
  const pairs = (fields: DocField[]) => {
    const dates = dateFields(fields)
    const start = dates.find((f) => f.name === params.get('start')) ?? dates.find((f) => /start|from|begin/i.test(f.name)) ?? dates[0]
    const end = dates.find((f) => f.name === params.get('end')) ?? dates.find((f) => f.name !== start?.name && /end|to|due|finish/i.test(f.name)) ?? dates.find((f) => f.name !== start?.name)
    return { dates, start, end }
  }
  const picker = useMemo(() => (fields: DocField[]) => { const { start, end } = pairs(fields); return start && end ? [start.name, end.name] : [] }, [params])
  const { doctype, meta, perms, metaQuery, list, titleOf, base } = useDocs(picker)
  const { dates, start, end } = pairs(meta?.fields ?? [])
  const rows = (list.data?.data ?? []) as Record<string, unknown>[]
  const chart = useMemo(() => layout(rows.map((r) => ({ name: String(r.name), title: titleOf(r), start: r[start?.name ?? ''], end: r[end?.name ?? ''] })), dayNumber(new Date().toISOString().slice(0, 10)) ?? 0), [rows, start, end])

  if (metaQuery.isError) return <Denied doctype={doctype} />
  if (!meta) return <p className="muted" role="status">Loading…</p>
  if (!start || !end) return <div className="banner info" role="status">{doctype} needs two date fields (a start and an end) for a timeline. <Link to={base}>Back to the list</Link></div>

  const choose = (key: 'start' | 'end', value: string) => { const next = new URLSearchParams(params); next.set(key, value); setParams(next) }
  return (
    <>
      <Head doctype={doctype} base={base} what="timeline">
        <label className="inline-field">Start <Select value={start.name} onChange={(e) => choose('start', e.target.value)}>{dates.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}</Select></label>
        <label className="inline-field">End <Select value={end.name} onChange={(e) => choose('end', e.target.value)}>{dates.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}</Select></label>
        {perms?.create && <Link className="btn primary" to={`${base}/new`}><Icon name="plus" /> New {doctype}</Link>}
      </Head>
      {list.isPending && <p className="muted" role="status">Loading…</p>}
      {chart.skipped > 0 && <p className="muted small-text">{chart.skipped} document{chart.skipped === 1 ? ' is' : 's are'} not shown: no start or end date, or the end is before the start.</p>}
      {list.data && chart.bars.length === 0 && <p className="muted">Nothing to show on a timeline yet.</p>}
      {chart.bars.length > 0 && (
        <div className="gantt" role="table" aria-label={`${doctype} timeline`}>
          <div className="gantt-axis" role="row">
            <span className="gantt-name" role="columnheader" />
            <div className="gantt-track" role="columnheader">{chart.ticks.map((t) => <span key={t.label} className="gantt-tick" style={{ insetInlineStart: `${t.left}%` }}>{t.label}</span>)}</div>
          </div>
          {chart.bars.map((b) => (
            <div className="gantt-row" role="row" key={b.name}>
              <Link className="gantt-name" role="rowheader" to={`${base}/${encodeURIComponent(b.name)}`} title={b.title}>{b.title}</Link>
              <div className="gantt-track" role="cell">
                {chart.today !== null && <i className="gantt-today" style={{ insetInlineStart: `${chart.today}%` }} />}
                <span className="gantt-bar" style={{ insetInlineStart: `${b.left}%`, inlineSize: `${b.width}%` }} title={`${b.title}: ${b.days} day${b.days === 1 ? '' : 's'}`} />
              </div>
            </div>
          ))}
        </div>
      )}
    </>
  )
}
