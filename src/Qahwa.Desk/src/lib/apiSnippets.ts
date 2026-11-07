import type { DocTypeDefinition, FieldType } from '../api/types.ts'

/** Field types that only lay out the form: they hold no value, so they are left out of examples. */
const LAYOUT: ReadonlySet<FieldType> = new Set<FieldType>(['SectionBreak', 'ColumnBreak', 'TabBreak', 'Heading', 'Html', 'Table'])

/** A believable sample value for a field type, so an example body can be pasted and run. */
export function sampleValue(type: FieldType, options?: string[] | null): unknown {
  switch (type) {
    case 'Int': case 'Rating': case 'Duration': return 1
    case 'Float': case 'Decimal': case 'Currency': case 'Percent': return 10.5
    case 'Check': return true
    case 'Date': return '2026-01-31'
    case 'Datetime': return '2026-01-31T09:30:00Z'
    case 'Time': return '09:30:00'
    case 'Email': return 'name@example.com'
    case 'Phone': return '+966500000000'
    case 'Url': return 'https://example.com'
    case 'Json': return {}
    case 'Select': return options?.[0] ?? ''
    case 'Color': return '#2f6f4e'
    default: return 'Example'
  }
}

/** The body of a create request: the required fields, or the first few when none are required. */
export function exampleBody(def: Pick<DocTypeDefinition, 'fields'>): Record<string, unknown> {
  const usable = def.fields.filter((f) => !LAYOUT.has(f.type) && f.type !== 'Password')
  const required = usable.filter((f) => f.required)
  const chosen = required.length > 0 ? required : usable.slice(0, 3)
  const body: Record<string, unknown> = {}
  for (const f of chosen) body[f.name] = sampleValue(f.type, f.options as string[] | null | undefined)
  return body
}

export interface ApiExample { title: string; curl: string }

/** Ready-to-run examples for one DocType (ADR-162): list, read, create, update, delete. */
export function apiExamples(def: Pick<DocTypeDefinition, 'name' | 'fields' | 'isSingle' | 'isSubmittable'>, origin: string): ApiExample[] {
  const url = `${origin}/api/resource/${encodeURIComponent(def.name)}`
  const auth = `-H "Authorization: Bearer $QAHWA_KEY"`
  const json = `-H "Content-Type: application/json"`
  const body = JSON.stringify(exampleBody(def), null, 2)
  const out: ApiExample[] = [
    { title: 'List', curl: `curl ${auth} "${url}?limit_page_length=20&order_by=modified%20desc"` },
    { title: 'Read one', curl: `curl ${auth} "${url}/<name>"` },
    { title: 'Create', curl: `curl -X POST ${auth} ${json} \\\n  -d '${body}' \\\n  "${url}"` },
    { title: 'Update', curl: `curl -X PUT ${auth} ${json} \\\n  -d '${JSON.stringify(Object.fromEntries(Object.entries(exampleBody(def)).slice(0, 1)))}' \\\n  "${url}/<name>"` },
    { title: 'Delete', curl: `curl -X DELETE ${auth} "${url}/<name>"` },
  ]
  if (def.isSubmittable) out.push({ title: 'Submit', curl: `curl -X POST ${auth} "${url}/<name>/submit"` })
  return def.isSingle ? out.filter((e) => e.title === 'Read one' || e.title === 'Update') : out
}
