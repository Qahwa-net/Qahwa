import { useMemo } from 'react'
import { useQueries } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { DocTypeMeta } from '../api/types.ts'
import { linkTargets, lookupLinks, type Session, type Tables, type Values } from './logic.ts'
import type { Value } from './expr/expr.ts'

/**
 * Fetches the documents that lookup() reads (only when a formula or rule uses it) and returns what to put on the session,
 * so formulas work out live as a Link field is chosen. The server reads each one with the person's own permissions.
 */
export function useLinkedDocs(
  meta: DocTypeMeta, children: Record<string, DocTypeMeta>, values: Values, tables: Tables,
): Session['linked'] | undefined {
  const targets = useMemo(() => {
    const seen = new Map<string, [string, string]>()
    const add = (m: DocTypeMeta, v: Values) => {
      for (const t of linkTargets(m, v)) seen.set(`${t[0]}\u0000${t[1]}`, t)
    }
    add(meta, values)
    for (const field of meta.fields.filter((f) => f.type === 'Table' && f.options && children[f.options])) {
      for (const row of tables[field.name] ?? []) add(children[field.options!]!, row.values)
    }
    return [...seen.entries()]
  }, [meta, children, values, tables])

  const results = useQueries({
    queries: targets.map(([key, [doctype, name]]) => ({
      queryKey: ['lookup', doctype, name], queryFn: () => api.lookup(doctype, name), retry: false, staleTime: 15_000, meta: { key },
    })),
  })

  const loaded = results.map((r) => r.data).join('|') // changes when a fetched document arrives
  return useMemo(() => {
    if (targets.length === 0) return undefined
    const byKey = new Map<string, Record<string, Value>>()
    targets.forEach(([key], i) => {
      const data = results[i]?.data
      if (data) byKey.set(key, Object.fromEntries(Object.entries(data).map(([k, v]) => [k, v === undefined || v === '' ? null : (v as Value)])))
    })
    return (m: DocTypeMeta, v: Values) => {
      const found: Record<string, Record<string, Value>> = {}
      for (const name of lookupLinks(m)) {
        const field = m.fields.find((f) => f.name === name && f.type === 'Link')
        const value = v[name]
        const data = field?.options && typeof value === 'string' && value !== '' ? byKey.get(`${field.options}\u0000${value}`) : undefined
        if (data) found[name] = data
      }
      return found
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [targets, loaded])
}
