import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'

/** The tags of a document: short labels anyone who may change it can add or remove. */
export function TagsPanel({ doctype, name, canEdit }: { doctype: string; name: string; canEdit: boolean }) {
  const queryClient = useQueryClient()
  const key = ['tags', doctype, name]
  const tags = useQuery({ queryKey: key, queryFn: () => api.tags.get(doctype, name), retry: false })
  const popular = useQuery({ queryKey: ['tags', doctype, '*popular'], queryFn: () => api.tags.popular(doctype), retry: false, enabled: canEdit })
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)

  const done = (next: string[]) => {
    queryClient.setQueryData(key, next)
    void queryClient.invalidateQueries({ queryKey: ['tags', doctype, '*popular'] })
    setError(null)
  }
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')
  const add = useMutation({ mutationFn: (tag: string) => api.tags.add(doctype, name, tag), onSuccess: (next) => { done(next); setText('') }, onError: fail })
  const remove = useMutation({ mutationFn: (tag: string) => api.tags.remove(doctype, name, tag), onSuccess: done, onError: fail })

  if (tags.isError || (!canEdit && (tags.data ?? []).length === 0)) return null
  const mine = tags.data ?? []
  const listId = `tag-suggestions-${doctype}`

  return (
    <section className="rail-card" aria-label="Tags">
      <header><h3>Tags</h3></header>
      {mine.length === 0 ? <p className="muted small-text">No tags.</p> : (
        <ul className="chips">
          {mine.map((tag) => (
            <li key={tag} className="chip">{tag}
              {canEdit && <button type="button" aria-label={`Remove tag ${tag}`} disabled={remove.isPending} onClick={() => remove.mutate(tag)}>×</button>}
            </li>
          ))}
        </ul>
      )}
      {canEdit && (
        <form onSubmit={(e) => { e.preventDefault(); if (text.trim()) add.mutate(text) }}>
          <input aria-label="Add a tag" placeholder="Add a tag…" list={listId} maxLength={40} value={text} onChange={(e) => setText(e.target.value)} />
          <datalist id={listId}>{(popular.data ?? []).filter((p) => !mine.includes(p.tag)).map((p) => <option key={p.tag} value={p.tag} />)}</datalist>
        </form>
      )}
      {error && <p role="alert" className="muted small-text">{error}</p>}
    </section>
  )
}
