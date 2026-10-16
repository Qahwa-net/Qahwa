export type FilterOp = '=' | '!=' | '>' | '<' | '>=' | '<=' | 'like' | 'not like' | 'is' | 'between' | 'timespan'

/** The named spans the server understands (weeks start on Monday). */
export const TIMESPANS = [
  'today', 'yesterday', 'tomorrow', 'this week', 'last week', 'next week', 'this month', 'last month', 'next month',
  'this quarter', 'last quarter', 'this year', 'last year', 'next year', 'last 7 days', 'last 30 days', 'last 90 days', 'next 7 days', 'next 30 days',
] as const

export interface Filter {
  field: string
  op: FilterOp
  /** For 'is' this is 'set' or 'not set'; for 'between' the two ends; for 'timespan' one of {@link TIMESPANS}. */
  value: string | number | boolean | [string | number, string | number]
}

const OPS: readonly string[] = ['=', '!=', '>', '<', '>=', '<=', 'like', 'not like', 'is', 'between', 'timespan']

/** "acme" becomes a contains-search; anything the user wrote with an explicit % is kept as their own pattern. */
export function likePattern(input: string): string {
  if (input.includes('%')) return input
  return `%${input.replace(/[\\_]/g, (c) => `\\${c}`)}%`
}

export function toApiFilters(filters: Filter[]): [string, string, unknown][] {
  return filters.map((f) => [f.field, f.op, f.op === 'like' || f.op === 'not like' ? likePattern(String(f.value)) : f.value])
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
      if (op === 'between') {
        const ends = Array.isArray(value) && value.length === 2 && value.every((v) => typeof v === 'string' || typeof v === 'number')
        if (ends) result.push({ field, op, value: [value[0], value[1]] as [string | number, string | number] })
        continue
      }
      if (op === 'timespan' && !(TIMESPANS as readonly unknown[]).includes(value)) continue
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
    case 'not like':
      return `${fieldLabel} does not contain ${filter.value}`
    case 'between':
      return Array.isArray(filter.value) ? `${fieldLabel} between ${filter.value[0]} and ${filter.value[1]}` : `${fieldLabel} between`
    case 'timespan':
      return `${fieldLabel} is ${filter.value}`
    default:
      return `${fieldLabel} ${filter.op} ${String(filter.value)}`
  }
}
