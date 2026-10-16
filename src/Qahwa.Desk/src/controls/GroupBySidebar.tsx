import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { DocField } from '../api/types.ts'
import { labelOf } from '../lib/fields.ts'
import type { Filter } from '../lib/filters.ts'

const key = (doctype: string) => `qahwa.groupby.${doctype}`

function remembered(doctype: string): string {
  try {
    return window.localStorage.getItem(key(doctype)) ?? ''
  } catch {
    return '' // storage can be blocked; the panel just starts empty
  }
}

/** The filter that picks one value of a field (or the documents where it is empty). */
export function filterFor(field: DocField, value: string | null): Filter {
  if (value === null) return { field: field.name, op: 'is', value: 'not set' }
  if (field.type === 'Check') return { field: field.name, op: '=', value: value === '1' }
  return { field: field.name, op: '=', value }
}

const same = (a: Filter, b: Filter) => a.field === b.field && a.op === b.op && String(a.value) === String(b.value)

/** "Group by": choose a field and see how many documents have each value, under the current filters; click a value to filter by it. */
export function GroupBySidebar({ doctype, fields, filters, applied, onChange }: {
  doctype: string
  /** The fields that can be grouped (choices, links, checks, owner, status). */
  fields: DocField[]
  /** Everything that narrows the list now (filters and the search box), so the counts match the rows. */
  filters: Filter[]
  /** Only the filters in the URL: these are the ones a click adds to or removes. */
  applied: Filter[]
  onChange: (next: Filter[]) => void
}) {
  const [field, setField] = useState(() => (fields.some((f) => f.name === remembered(doctype)) ? remembered(doctype) : ''))
  const chosen = fields.find((f) => f.name === field)
  const popular = useQuery({ queryKey: ['tags', doctype, '*popular'], queryFn: () => api.tags.popular(doctype), retry: false })
  const tagFilter = (tag: string): Filter => ({ field: '_tag', op: '=', value: tag })
  const likedFilter: Filter = { field: '_liked_by', op: '=', value: '__user' }

  const counts = useQuery({
    queryKey: ['group-by', doctype, field, JSON.stringify(filters)],
    queryFn: () => api.groupBy(doctype, field, filters),
    enabled: Boolean(chosen),
    retry: false,
  })

  function choose(next: string) {
    setField(next)
    try {
      window.localStorage.setItem(key(doctype), next)
    } catch {
      // not remembered, still works
    }
  }

  return (
    <aside className="group-by" aria-label="Group by">
      <label>Group by
        <select value={field} onChange={(e) => choose(e.target.value)}>
          <option value="">Choose a field…</option>
          {fields.map((f) => <option key={f.name} value={f.name}>{labelOf(f)}</option>)}
        </select>
      </label>
      {!popular.isError && (
        <div className="tag-filter" aria-label="Filter by tag or like">
          <button className={applied.some((f) => same(f, likedFilter)) ? 'chip-button active' : 'chip-button'} aria-pressed={applied.some((f) => same(f, likedFilter))}
            onClick={() => onChange(applied.some((f) => same(f, likedFilter)) ? applied.filter((f) => !same(f, likedFilter)) : [...applied, likedFilter])}>Liked by me</button>
          {(popular.data ?? []).slice(0, 12).map((t) => {
            const active = applied.some((f) => same(f, tagFilter(t.tag)))
            return (
              <button key={t.tag} className={active ? 'chip-button active' : 'chip-button'} aria-pressed={active}
                onClick={() => onChange(active ? applied.filter((f) => !same(f, tagFilter(t.tag))) : [...applied.filter((f) => f.field !== '_tag'), tagFilter(t.tag)])}>
                {t.tag} <span className="muted">{t.count}</span>
              </button>
            )
          })}
        </div>
      )}
      {chosen && counts.isPending && <p className="muted" role="status">Counting…</p>}
      {chosen && counts.data?.length === 0 && <p className="muted">No documents.</p>}
      <ul>
        {chosen && counts.data?.map((group) => {
          const filter = filterFor(chosen, group.value)
          const active = applied.some((f) => same(f, filter))
          return (
            <li key={group.value ?? '(empty)'}>
              <button className={active ? 'active' : undefined} aria-pressed={active}
                onClick={() => onChange(active ? applied.filter((f) => !same(f, filter)) : [...applied.filter((f) => f.field !== chosen.name), filter])}>
                <span>{group.value === null ? '(empty)' : chosen.type === 'Check' ? (group.value === '1' ? 'Yes' : 'No') : group.value}</span>
                <span className="muted">{group.count}</span>
              </button>
            </li>
          )
        })}
      </ul>
    </aside>
  )
}
