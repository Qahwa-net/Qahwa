import { useMemo, useState } from 'react'
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { ActivityItem, DocTypeMeta } from '../api/types.ts'
import { describeVersion, splitMentions, timeAgo, versionHeadline } from '../lib/activity.ts'

const MAX = 5000

/** A document's updates log and comments in one timeline, with a box to add a comment. */
export function ActivityPanel({ meta, childMetas, name }: { meta: DocTypeMeta; childMetas: Record<string, DocTypeMeta>; name: string }) {
  const queryClient = useQueryClient()
  const key = ['activity', meta.name, name]
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)

  const labels = useMemo(() => {
    const result: Record<string, string> = { docstatus: 'Status' }
    for (const f of [...meta.fields, ...Object.values(childMetas).flatMap((c) => c.fields)]) result[f.name] = f.label || f.name
    return result
  }, [meta, childMetas])

  const timeline = useInfiniteQuery({
    queryKey: key,
    queryFn: ({ pageParam }) => api.activity.timeline(meta.name, name, pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => (last.length >= 30 ? last[last.length - 1]?.at : undefined),
    retry: false,
  })
  const items = timeline.data?.pages.flat() ?? []
  const refresh = () => queryClient.invalidateQueries({ queryKey: key })
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const add = useMutation({
    mutationFn: () => api.activity.addComment(meta.name, name, text.trim()),
    onSuccess: async () => { setText(''); setError(null); await refresh() },
    onError: fail,
  })

  return (
    <section className="card activity" aria-label="Activity">
      <h3>Activity</h3>

      <form className="comment-box" onSubmit={(e) => { e.preventDefault(); if (text.trim()) add.mutate() }}>
        <textarea rows={3} maxLength={MAX} placeholder="Write a comment… use @name to mention someone" aria-label="Write a comment"
          value={text} onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && text.trim()) { e.preventDefault(); add.mutate() } }} />
        <div className="comment-actions">
          <small className="muted">{text.length}/{MAX} · Ctrl+Enter to post</small>
          <button className="btn primary" disabled={!text.trim() || add.isPending}>{add.isPending ? 'Posting…' : 'Comment'}</button>
        </div>
      </form>
      {error && <div className="banner error" role="alert">{error}</div>}

      {timeline.isPending && <p className="muted" role="status">Loading…</p>}
      {timeline.isError && <p className="muted">The history could not be loaded.</p>}
      <ol className="timeline">
        {items.map((item) => <Entry key={`${item.kind}-${item.id}`} item={item} labels={labels} doctype={meta.name} name={name} onChanged={refresh} onError={fail} />)}
      </ol>
      {timeline.hasNextPage && (
        <button className="btn small" disabled={timeline.isFetchingNextPage} onClick={() => void timeline.fetchNextPage()}>Load older</button>
      )}
      {timeline.data && items.length === 0 && <p className="muted">Nothing yet.</p>}
    </section>
  )
}

function Who({ item }: { item: ActivityItem }) {
  return <strong title={item.user}>{item.userName || item.user}</strong>
}

function When({ at }: { at: string }) {
  return <time dateTime={at} title={new Date(at).toLocaleString()} className="muted">{timeAgo(at)}</time>
}

function Entry({ item, labels, doctype, name, onChanged, onError }: {
  item: ActivityItem
  labels: Record<string, string>
  doctype: string
  name: string
  onChanged: () => Promise<unknown>
  onError: (error: unknown) => void
}) {
  if (item.kind === 'version') {
    const lines = describeVersion(item, labels)
    return (
      <li className="entry version">
        <div><Who item={item} /> {versionHeadline(item)} · <When at={item.at} /></div>
        {lines.length > 0 && ['Update', 'Rename', 'Merge'].includes(item.event) && <ul className="changes">{lines.map((line, i) => <li key={i}>{line}</li>)}</ul>}
      </li>
    )
  }
  if (item.event === 'Info') {
    return <li className="entry note"><span>{item.content}</span> · <When at={item.at} /></li>
  }
  return <CommentEntry item={item} doctype={doctype} name={name} onChanged={onChanged} onError={onError} />
}

function CommentEntry({ item, doctype, name, onChanged, onError }: {
  item: ActivityItem
  doctype: string
  name: string
  onChanged: () => Promise<unknown>
  onError: (error: unknown) => void
}) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(item.content ?? '')

  const save = useMutation({
    mutationFn: () => api.activity.updateComment(doctype, name, item.id, draft.trim()),
    onSuccess: async () => { setEditing(false); await onChanged() },
    onError,
  })
  const remove = useMutation({
    mutationFn: () => api.activity.deleteComment(doctype, name, item.id),
    onSuccess: async () => { await onChanged() },
    onError,
  })

  return (
    <li className="entry comment">
      <div><Who item={item} /> commented · <When at={item.at} />{item.edited && <span className="muted"> (edited)</span>}</div>
      {editing ? (
        <div className="comment-box">
          <textarea rows={3} maxLength={MAX} aria-label="Edit comment" value={draft} onChange={(e) => setDraft(e.target.value)} />
          <div className="comment-actions">
            <button className="btn small" onClick={() => { setEditing(false); setDraft(item.content ?? '') }}>Cancel</button>
            <button className="btn small primary" disabled={!draft.trim() || save.isPending} onClick={() => save.mutate()}>Save</button>
          </div>
        </div>
      ) : (
        // Text only: pieces are rendered as React text, never as markup.
        <p className="comment-text">{splitMentions(item.content ?? '').map((p, i) => (p.mention ? <strong key={i} className="mention">{p.text}</strong> : <span key={i}>{p.text}</span>))}</p>
      )}
      {item.canEdit && !editing && (
        <div className="comment-actions">
          <button className="link-button" onClick={() => setEditing(true)}>Edit</button>
          <button className="link-button" disabled={remove.isPending}
            onClick={() => { if (window.confirm('Delete this comment?')) remove.mutate() }}>Delete</button>
        </div>
      )}
    </li>
  )
}
