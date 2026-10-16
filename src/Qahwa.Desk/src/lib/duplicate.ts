import type { Doc, DocTypeMeta } from '../api/types.ts'

const SYSTEM = new Set(['name', 'creation', 'modified', 'modified_by', 'owner', 'docstatus', 'idx'])
const NEVER = new Set(['Password', 'SectionBreak', 'ColumnBreak', 'TabBreak'])

/**
 * A new document made from an existing one: every field copied except those that must stay unique, passwords and the system
 * fields; table rows are copied as new rows (without the old ones' names). Nothing is saved: it is the starting point of a new form.
 */
export function copyOf(meta: DocTypeMeta, children: Record<string, DocTypeMeta>, doc: Doc): Doc {
  const copy: Doc = { name: '' } as Doc
  for (const field of meta.fields) {
    if (SYSTEM.has(field.name) || NEVER.has(field.type) || field.unique) continue
    const value = doc[field.name]
    if (field.type === 'Table') {
      const child = field.options ? children[field.options] : undefined
      const rows = Array.isArray(value) ? value : []
      copy[field.name] = rows.map((row) => {
        const fresh: Record<string, unknown> = {}
        for (const [key, v] of Object.entries(row as Record<string, unknown>)) {
          if (SYSTEM.has(key) || key.startsWith('parent')) continue
          const column = child?.fields.find((f) => f.name === key)
          if (column && (column.unique || NEVER.has(column.type))) continue
          fresh[key] = v
        }
        return fresh
      })
    } else if (value !== undefined) {
      copy[field.name] = value
    }
  }

  return copy
}
