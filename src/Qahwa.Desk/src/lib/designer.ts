import type {
  DesignerDetail, DocField, DocPerm, DocTypeDefinition, FieldError, FieldType, FieldTypeInfo, ValidationRule,
} from '../api/types.ts'

/** A field being edited. `key` is a stable id for React; `locked` marks fields that already exist in the database. */
export interface DraftField extends DocField {
  key: string
  locked: boolean
  /** True while the name still follows the label (until the user edits the name by hand). */
  nameFollowsLabel: boolean
}

export type AutonameKind = 'hash' | 'prompt' | 'field' | 'series'

export interface Draft {
  /** A child table: its rows live inside other documents and it has no list or form of its own. */
  isChildTable: boolean
  isNew: boolean
  editable: boolean
  name: string
  module: string
  titleField: string
  autonameKind: AutonameKind
  autonameField: string
  seriesPrefix: string
  seriesDigits: number
  fields: DraftField[]
  permissions: DocPerm[]
  validations: ValidationRule[]
  modified: string | null
  documentCount: number | null
}

let counter = 0
const nextKey = () => `f${++counter}`

/** "Credit Limit (USD)" becomes "credit_limit_usd", a valid field name. */
export function slugify(label: string): string {
  const slug = label
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '') // accents: "é" is "e" plus a combining mark
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 60)
  return /^[a-z]/.test(slug) ? slug : slug ? `f_${slug}` : ''
}

/** Returns `base`, or `base_2`, `base_3`, ... so it does not collide with an existing name. */
export function uniqueName(base: string, taken: Iterable<string>): string {
  const used = new Set(taken)
  if (!used.has(base)) return base
  let n = 2
  while (used.has(`${base}_${n}`)) n++
  return `${base}_${n}`
}

export function newField(info: FieldTypeInfo, taken: string[]): DraftField {
  const layout = info.type === 'SectionBreak' || info.type === 'TabBreak'
  const label = layout ? info.label : ''
  const base = layout ? slugify(`${info.label} break`) : slugify(info.label)
  return {
    key: nextKey(),
    locked: false,
    nameFollowsLabel: true,
    name: uniqueName(base || 'field', taken),
    type: info.type,
    label,
    required: false,
    unique: false,
    inListView: false,
    lowercase: false,
    options: null,
    default: null,
    description: null,
    hidden: false,
    readOnly: false,
    dependsOn: null,
    mandatoryDependsOn: null,
    readOnlyDependsOn: null,
    formula: null,
    fetchFrom: null,
    fetchIfEmpty: false,
    permLevel: 0,
  }
}

export function moveItem<T>(items: T[], from: number, to: number): T[] {
  if (to < 0 || to >= items.length || from === to) return items
  const copy = [...items]
  const [item] = copy.splice(from, 1)
  copy.splice(to, 0, item!)
  return copy
}

export function emptyDraft(): Draft {
  return {
    isChildTable: false,
    isNew: true,
    editable: true,
    name: '',
    module: 'Custom',
    titleField: '',
    autonameKind: 'hash',
    autonameField: '',
    seriesPrefix: '',
    seriesDigits: 5,
    fields: [],
    permissions: [{ role: 'System Manager', read: true, write: true, create: true, delete: true, ifOwner: false, permLevel: 0 }],
    validations: [],
    modified: null,
    documentCount: null,
  }
}

export function parseAutoname(autoname: string | null | undefined): Pick<Draft, 'autonameKind' | 'autonameField' | 'seriesPrefix' | 'seriesDigits'> {
  const base = { autonameKind: 'hash' as AutonameKind, autonameField: '', seriesPrefix: '', seriesDigits: 5 }
  if (!autoname || autoname === 'hash') return base
  if (autoname === 'prompt') return { ...base, autonameKind: 'prompt' }
  if (autoname.startsWith('field:')) return { ...base, autonameKind: 'field', autonameField: autoname.slice(6) }
  const index = autoname.lastIndexOf('.#')
  if (index >= 0) {
    return { ...base, autonameKind: 'series', seriesPrefix: autoname.slice(0, index), seriesDigits: autoname.length - index - 1 }
  }
  return base
}

export function buildAutoname(draft: Pick<Draft, 'autonameKind' | 'autonameField' | 'seriesPrefix' | 'seriesDigits'>): string | undefined {
  switch (draft.autonameKind) {
    case 'prompt': return 'prompt'
    case 'field': return `field:${draft.autonameField}`
    case 'series': return `${draft.seriesPrefix}.${'#'.repeat(Math.min(10, Math.max(1, draft.seriesDigits)))}`
    default: return undefined // hash is the default
  }
}

export function fromDetail(detail: DesignerDetail): Draft {
  const def = detail.definition
  return {
    isChildTable: def.isChildTable,
    isNew: false,
    editable: detail.editable,
    name: def.name,
    module: def.module ?? '',
    titleField: def.titleField ?? '',
    ...parseAutoname(def.autoname),
    fields: def.fields.map((f) => ({
      ...f,
      key: nextKey(),
      locked: true,
      nameFollowsLabel: false,
      options: f.options ?? null,
      default: f.default ?? null,
      description: f.description ?? null,
      hidden: f.hidden ?? false,
      readOnly: f.readOnly ?? false,
      dependsOn: f.dependsOn ?? null,
      mandatoryDependsOn: f.mandatoryDependsOn ?? null,
      readOnlyDependsOn: f.readOnlyDependsOn ?? null,
      formula: f.formula ?? null,
      fetchFrom: f.fetchFrom ?? null,
      fetchIfEmpty: f.fetchIfEmpty ?? false,
      permLevel: f.permLevel ?? 0,
    })),
    permissions: (def.permissions ?? []).map((p) => ({ ...p, ifOwner: p.ifOwner ?? false, permLevel: p.permLevel ?? 0 })),
    validations: (def.validations ?? []).map((r) => ({ expression: r.expression, message: r.message, field: r.field ?? null })),
    modified: detail.modified ?? null,
    documentCount: detail.documentCount ?? null,
  }
}

const blank = (value: string | null | undefined) => (value === null || value === undefined || value.trim() === '' ? undefined : value)

/** The JSON the API stores. Empty values are left out so the definition stays small and readable. */
export function toDefinition(draft: Draft): DocTypeDefinition {
  return {
    name: draft.name.trim(),
    module: blank(draft.module),
    titleField: draft.isChildTable ? undefined : blank(draft.titleField),
    // Rows of a child table are named automatically.
    autoname: draft.isChildTable ? undefined : buildAutoname(draft),
    isChildTable: draft.isChildTable || undefined,
    fields: draft.fields.map((f) => ({
      name: f.name,
      type: f.type,
      label: blank(f.label),
      options: blank(f.options),
      required: f.required || undefined,
      unique: f.unique || undefined,
      inListView: f.inListView || undefined,
      default: blank(f.default),
      lowercase: f.lowercase || undefined,
      description: blank(f.description),
      hidden: f.hidden || undefined,
      readOnly: f.readOnly || undefined,
      dependsOn: blank(f.dependsOn),
      mandatoryDependsOn: blank(f.mandatoryDependsOn),
      readOnlyDependsOn: blank(f.readOnlyDependsOn),
      formula: blank(f.formula),
      fetchFrom: blank(f.fetchFrom),
      fetchIfEmpty: (f.fetchFrom && f.fetchIfEmpty) || undefined,
      // Rows of a child table follow the access of the document, so they have no levels of their own.
      permLevel: (!draft.isChildTable && f.permLevel) || undefined,
    })) as DocTypeDefinition['fields'],
    // Child table rows follow the permissions of the document they belong to.
    permissions: draft.isChildTable ? [] : draft.permissions.filter((p) => p.role.trim() !== ''),
    // Rules left completely blank are dropped; a half-filled rule is kept so the server can say what is missing.
    validations: draft.validations.some((r) => r.expression.trim() || r.message.trim())
      ? draft.validations
          .filter((r) => r.expression.trim() || r.message.trim())
          .map((r) => ({ expression: r.expression, message: r.message, field: blank(r.field) }))
      : undefined,
  }
}

export function errorMap(errors: FieldError[]): Record<string, string> {
  const map: Record<string, string> = {}
  for (const e of errors) map[e.field] ??= e.message
  return map
}

/** Types whose value is text, where "convert to lower case" makes sense. */
export const isTextType = (type: FieldType) =>
  ['Data', 'Text', 'LongText', 'Email', 'Phone', 'Url', 'Code', 'Markdown'].includes(type)

/** Types that cannot sensibly be unique or hold a default (long text, layout). */
export const isLayoutType = (type: FieldType) => type === 'SectionBreak' || type === 'TabBreak' || type === 'ColumnBreak'
