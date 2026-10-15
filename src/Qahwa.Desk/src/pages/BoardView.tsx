import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import { Icon } from '../controls/Icon.tsx'
import { NO_VALUE, boardFields, columnLabel, columnsOf, groupCards, valueOf, withMoved, type CardDoc } from '../lib/board.ts'
import { STANDARD_FIELDS, formatCell, labelOf } from '../lib/fields.ts'

const LIMIT = 500

/** A board: one column for each choice of a Select field, one card for each document. Drag a card to another column to change its value. */
export function BoardView() {
  const { doctype = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const queryClient = useQueryClient()
  const metaQuery = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  const meta = metaQuery.data?.data
  const perms = metaQuery.data?.permissions
  const fields = useMemo(() => boardFields(meta?.fields ?? []), [meta])
  const by = fields.find((f) => f.name === params.get('by')) ?? fields.find((f) => f.name === meta?.statusField) ?? fields[0]
  const [error, setError] = useState<string | null>(null)
  const [dragging, setDragging] = useState<string | null>(null)
  const [over, setOver] = useState<string | null>(null)

  const titleField = meta?.titleField && meta.fields.some((f) => f.name === meta.titleField) ? meta.titleField : null
  const shown = useMemo(() => (meta?.fields ?? []).filter((f) => f.inListView && f.name !== by?.name && f.name !== titleField && f.type !== 'Table').slice(0, 2), [meta, by, titleField])
  const wanted = useMemo(() => ['name', 'modified', ...(titleField ? [titleField] : []), ...(by ? [by.name] : []), ...shown.map((f) => f.name)], [titleField, by, shown])
  const key = ['board', doctype, by?.name]
  const list = useQuery({
    queryKey: key,
    queryFn: () => api.list(doctype, { fields: wanted, filters: [], orderBy: 'modified', descending: true, limit: LIMIT, offset: 0 }),
    enabled: Boolean(by),
    retry: false,
  })
  const docs = (list.data?.data ?? []) as CardDoc[]
  const columns = useMemo(() => (by ? columnsOf(by, docs) : []), [by, docs])
  const groups = useMemo(() => (by ? groupCards(docs, by.name, columns) : {}), [by, docs, columns])
  const canMove = Boolean(perms?.write)

  const move = useMutation({
    mutationFn: ({ name, to }: { name: string; to: string }) => api.update(doctype, name, { [by!.name]: to === NO_VALUE ? null : to }),
    onMutate: async ({ name, to }) => {
      setError(null)
      await queryClient.cancelQueries({ queryKey: key })
      const before = queryClient.getQueryData<typeof list.data>(key)
      if (before) queryClient.setQueryData(key, { ...before, data: withMoved(before.data as CardDoc[], name, by!.name, to) })
      return { before }
    },
    onError: (e, _vars, context) => {
      if (context?.before) queryClient.setQueryData(key, context.before)
      setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not move the card.')
    },
    onSettled: () => void queryClient.invalidateQueries({ queryKey: ['list', doctype] }),
  })

  const base = `/app/${encodeURIComponent(doctype)}`
  if (metaQuery.isError) return <div className="banner error" role="alert">You do not have access to "{doctype}", or it does not exist.</div>
  if (!meta) return <p className="muted" role="status">Loading…</p>
  if (!by) return <div className="banner info" role="status">{doctype} has no choice field to build a board on. Add a Select field in the DocType Designer. <Link to={base}>Back to the list</Link></div>

  const drop = (to: string) => {
    setOver(null)
    const name = dragging
    setDragging(null)
    if (name && canMove) {
      const doc = docs.find((d) => d.name === name)
      if (doc && valueOf(doc, by.name) !== to) move.mutate({ name, to })
    }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <Link to={base} className="muted back">← {doctype} list</Link>
          <h1>{doctype} board</h1>
        </div>
        <span className="spacer" />
        {fields.length > 1 && (
          <label className="inline-field">Columns from
            <select value={by.name} onChange={(e) => setParams({ by: e.target.value })}>
              {fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
            </select>
          </label>
        )}
        {perms?.create && <Link className="btn primary" to={`${base}/new`}><Icon name="plus" /> New {doctype}</Link>}
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}
      {list.isPending && <p className="muted" role="status">Loading…</p>}
      {list.data && list.data.total > LIMIT && <div className="banner info" role="status">Showing the {LIMIT} most recently changed of {list.data.total}. Use filters in the list to narrow down.</div>}
      {!canMove && <p className="muted small-text">You can look at the board but not move cards.</p>}

      <div className="board" role="list">
        {columns.map((column) => {
          const cards = groups[column] ?? []
          return (
            <section key={column || 'none'} role="listitem" aria-label={`${columnLabel(column)}, ${cards.length} cards`} className={`board-col${over === column ? ' over' : ''}`}
              onDragOver={(e) => { if (canMove) { e.preventDefault(); setOver(column) } }} onDragLeave={() => setOver((o) => (o === column ? null : o))} onDrop={(e) => { e.preventDefault(); drop(column) }}>
              <header><h2>{columnLabel(column)}</h2><span className="badge">{cards.length}</span></header>
              <ul className="board-cards">
                {cards.map((card) => (
                  <li key={card.name} className={`board-card${dragging === card.name ? ' dragging' : ''}`} draggable={canMove}
                    onDragStart={(e) => { setDragging(card.name); e.dataTransfer.setData('text/plain', card.name); e.dataTransfer.effectAllowed = 'move' }} onDragEnd={() => { setDragging(null); setOver(null) }}>
                    <Link to={`${base}/${encodeURIComponent(card.name)}`}>{String((titleField && card[titleField]) || card.name)}</Link>
                    {shown.map((f) => {
                      const text = formatCell(f, card[f.name])
                      return text ? <small key={f.name} className="muted">{labelOf(f)}: {text}</small> : null
                    })}
                    {canMove && (
                      <select aria-label={`Move ${card.name} to`} value={column} onChange={(e) => move.mutate({ name: card.name, to: e.target.value })}>
                        {columns.map((c) => <option key={c || 'none'} value={c}>{columnLabel(c)}</option>)}
                      </select>
                    )}
                  </li>
                ))}
                {cards.length === 0 && <li className="board-empty">No cards</li>}
              </ul>
            </section>
          )
        })}
      </div>
    </>
  )
}

// Keeps the standard-field helper imported for formatting columns such as "modified".
void STANDARD_FIELDS
