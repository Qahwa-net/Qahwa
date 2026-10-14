export type FilterOp = '=' | '!=' | '>' | '<' | '>=' | '<=' | 'like' | 'is'

export interface Filter {
  field: string
  op: FilterOp
  /** For 'is' this is 'set' or 'not set'. */
  value: string | number | boolean
}

const OPS: readonly string[] = ['=', '!=', '>', '<', '>=', '<=', 'like', 'is']

/** "acme" becomes a contains-search; anything the user wrote with an explicit % is kept as their own pattern. */
export function likePattern(input: string): string {
  if (input.includes('%')) return input
  return `%${input.replace(/[\\_]/g, (c) => `\\${c}`)}%`
}

export function toApiFilters(filters: Filter[]): [string, string, unknown][] {
  return filters.map((f) => [f.field, f.op, f.op === 'like' ? likePattern(String(f.value)) : f.value])
}

export function encodeFilters(filters: Filter[]): string {
  return filters.length === 0 ? '' : JSON.stringify(filters.map((f) => [f.field, f.op, f.value]))
}

/** Reads filters from the URL. Anything malformed is dropped rather than breaking the page. */
export function decodeFilters(encoded: string | null): Filter[] {
  if (!encoded) return []
  try {
    const parsed: unknown = JSON.parse(encoded)
    if (!Array.isArray(parsed)) return []

    const result: Filter[] = []
    for (const item of parsed) {
      if (!Array.isArray(item) || item.length !== 3) continue
      const [field, op, value] = item as [unknown, unknown, unknown]
      if (typeof field !== 'string' || typeof op !== 'string' || !OPS.includes(op)) continue
      if (typeof value !== 'string' && typeof value !== 'number' && typeof value !== 'boolean') continue
      result.push({ field, op: op as FilterOp, value })
    }
    return result
  } catch {
    return []
  }
}

export function describeFilter(filter: Filter, fieldLabel: string): string {
  switch (filter.op) {
    case 'is':
      return `${fieldLabel} is ${filter.value}`
    case 'like':
      return `${fieldLabel} contains ${filter.value}`
    default:
      return `${fieldLabel} ${filter.op} ${String(filter.value)}`
  }
}
