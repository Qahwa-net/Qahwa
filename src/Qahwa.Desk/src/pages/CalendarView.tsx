import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { api } from '../api/client.ts'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { MONTH_NAMES, WEEKDAYS, addMonths, eventsByDay, formatMonth, monthGrid, monthRange, parseMonth } from '../lib/calendar.ts'
import { weekStartIndex } from '../lib/display.ts'
import { labelOf } from '../lib/fields.ts'

const MAX_PER_DAY = 3
const LIMIT = 500

/** A month calendar of a DocType's documents by one of their date fields. */
export function CalendarView() {
  const { doctype = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const metaQuery = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  const meta = metaQuery.data?.data
  const dateFields = useMemo(() => (meta?.fields ?? []).filter((f) => (f.type === 'Date' || f.type === 'Datetime') && !f.hidden), [meta])
  const by = dateFields.find((f) => f.name === params.get('by')) ?? dateFields[0]
  const today = new Date()
  const { year, month } = parseMonth(params.get('month'), { year: today.getUTCFullYear(), month: today.getUTCMonth() + 1 })
  const [from, to] = monthRange(year, month)
  const titleField = meta?.titleField && meta.fields.some((f) => f.name === meta.titleField) ? meta.titleField : null

  const list = useQuery({
    queryKey: ['calendar', doctype, by?.name, from],
    queryFn: () => api.list(doctype, {
      fields: ['name', ...(titleField ? [titleField] : []), by!.name],
      filters: [{ field: by!.name, op: '>=', value: from }, { field: by!.name, op: '<', value: to }],
      orderBy: by!.name, descending: false, limit: LIMIT, offset: 0,
    }),
    enabled: Boolean(by),
    retry: false,
  })
  const byDay = useMemo(() => eventsByDay((list.data?.data ?? []) as Record<string, unknown>[], by?.name ?? ''), [list.data, by])
  const weekStart = weekStartIndex()
  const grid = monthGrid(year, month, weekStart)
  const base = `/app/${encodeURIComponent(doctype)}`
  const go = (delta: number) => { const next = addMonths(year, month, delta); const p = new URLSearchParams(params); p.set('month', formatMonth(next.year, next.month)); setParams(p) }
  const todayIso = `${today.getUTCFullYear()}-${String(today.getUTCMonth() + 1).padStart(2, '0')}-${String(today.getUTCDate()).padStart(2, '0')}`

  if (metaQuery.isError) return <div className="banner error" role="alert">You do not have access to "{doctype}", or it does not exist.</div>
  if (!meta) return <p className="muted" role="status">Loading…</p>
  if (!by) return <div className="banner info" role="status">{doctype} has no date field to build a calendar on. <Link to={base}>Back to the list</Link></div>

  return (
    <>
      <div className="page-head">
        <div>
          <Link to={base} className="muted back">← {doctype} list</Link>
          <h1>{doctype} calendar</h1>
        </div>
        <span className="spacer" />
        {dateFields.length > 1 && (
          <label className="inline-field">By
            <select value={by.name} onChange={(e) => { const p = new URLSearchParams(params); p.set('by', e.target.value); setParams(p) }}>
              {dateFields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
            </select>
          </label>
        )}
        {metaQuery.data?.permissions.create && <Link className="btn primary" to={`${base}/new`}><Icon name="plus" /> New {doctype}</Link>}
      </div>

      <div className="cal-nav">
        <IconButton icon="arrowLeft" label="Previous month" onClick={() => go(-1)} />
        <h2 aria-live="polite">{MONTH_NAMES[month - 1]} {year}</h2>
        <IconButton icon="arrowLeft" label="Next month" className="flip" onClick={() => go(1)} />
        <button className="btn small" onClick={() => { const p = new URLSearchParams(params); p.delete('month'); setParams(p) }}>Today</button>
      </div>
      {list.data && list.data.total > LIMIT && <div className="banner info" role="status">Showing the first {LIMIT} of {list.data.total} documents this month.</div>}
      {list.isPending && <p className="muted" role="status">Loading…</p>}

      <div className="calendar" role="grid" aria-label={`${MONTH_NAMES[month - 1]} ${year}`}>
        <div className="cal-head" role="row">{[0, 1, 2, 3, 4, 5, 6].map((i) => (weekStart + i) % 7).map((d) => <span key={d} role="columnheader">{WEEKDAYS[d]}</span>)}</div>
        {grid.map((week, w) => (
          <div key={w} className="cal-week" role="row">
            {week.map((day) => {
              const events = byDay[day.date] ?? []
              return (
                <div key={day.date} role="gridcell" aria-label={`${day.date}, ${events.length} documents`} className={`cal-day${day.inMonth ? '' : ' out'}${day.date === todayIso ? ' today' : ''}`}>
                  <span className="cal-num">{Number(day.date.slice(8))}</span>
                  <ul>
                    {events.slice(0, MAX_PER_DAY).map((e) => (
                      <li key={String(e.name)}><Link to={`${base}/${encodeURIComponent(String(e.name))}`} title={String(e.name)}>{String((titleField && e[titleField]) || e.name)}</Link></li>
                    ))}
                    {events.length > MAX_PER_DAY && <li className="muted">+{events.length - MAX_PER_DAY} more</li>}
                  </ul>
                </div>
              )
            })}
          </div>
        ))}
      </div>
    </>
  )
}
