import type { Customization, CustomizationView, DocField, DocTypeMeta, FieldOverride, FieldTypeInfo } from '../api/types.ts'
import { slugify, uniqueName } from './designer.ts'

/** One field in the Customize Form: a field of the definition (possibly changed) or one added here. */
export interface Row {
  /** Stable while editing; the field's name for fields that exist. */
  key: string
  custom: boolean
  /** Added in this session and not saved yet, so its name can still follow its label. */
  isNew: boolean
  field: DocField
}

export interface CustomizeState {
  rows: Row[]
  titleField: string
}

/** The text properties an override can change; an empty string clears the value. */
const TEXT = ['label', 'description', 'default', 'dependsOn', 'mandatoryDependsOn', 'readOnlyDependsOn'] as const
const FLAGS = ['hidden', 'readOnly', 'required', 'inListView'] as const

/** Field types that cannot be added to an existing DocType (they need a table of their own, or special storage). */
const NOT_ADDABLE = new Set(['Table', 'Password'])

export function addableTypes(types: FieldTypeInfo[]): FieldTypeInfo[] {
  return types.filter((t) => t.supported && !NOT_ADDABLE.has(t.type))
}

/** The state shown when the page opens: the DocType as it works now, with custom fields marked. */
export function stateFromView(view: CustomizationView): CustomizeState {
  const custom = new Set(view.customization.customFields.map((f) => f.name))
  return {
    rows: view.effective.fields.map((field) => ({ key: field.name, custom: custom.has(field.name), isNew: false, field: { ...field } })),
    titleField: view.effective.titleField ?? '',
  }
}

/** A new custom field of this type. The name follows the label until the field is saved. */
export function newCustomField(info: FieldTypeInfo, label: string, taken: string[]): Row {
  const name = customName(label || info.label, taken)
  return {
    key: name,
    custom: true,
    isNew: true,
    field: {
      name, type: info.type, label: label || info.label, options: null, required: false, unique: false, inListView: false,
      lowercase: false, default: null, description: null, hidden: false, readOnly: false, permLevel: 0,
    },
  }
}

export function customName(label: string, taken: string[]): string {
  const slug = slugify(label).replace(/^f_/, '') || 'field'
  return uniqueName(`custom_${slug}`, taken)
}

const text = (value: string | null | undefined) => value ?? ''

function overrideOf(original: DocField, current: DocField): FieldOverride {
  const change: Record<string, unknown> = {}
  for (const key of TEXT) {
    if (text(current[key]) !== text(original[key])) change[key] = text(current[key])
  }
  for (const key of FLAGS) {
    if (Boolean(current[key]) !== Boolean(original[key])) change[key] = Boolean(current[key])
  }
  if ((current.permLevel ?? 0) !== (original.permLevel ?? 0)) change.permLevel = current.permLevel ?? 0
  if (original.type === 'Select' && text(current.options) !== text(original.options)) change.options = text(current.options)
  return change as FieldOverride
}

/** What to send to the server: only what differs from the definition. */
export function toCustomization(state: CustomizeState, definition: DocTypeMeta): Customization {
  const original = new Map(definition.fields.map((f) => [f.name, f]))
  const overrides: Record<string, FieldOverride> = {}
  for (const row of state.rows) {
    const base = original.get(row.field.name)
    if (!row.custom && base) {
      const change = overrideOf(base, row.field)
      if (Object.keys(change).length > 0) overrides[row.field.name] = change
    }
  }

  const customFields = state.rows.filter((r) => r.custom).map((r) => cleanCustom(r.field))
  const names = state.rows.map((r) => r.field.name)
  const usual = [...definition.fields.map((f) => f.name), ...customFields.map((f) => f.name)]
  const order = names.length === usual.length && names.every((n, i) => n === usual[i]) ? [] : names

  const result: Customization = { customFields, overrides, order }
  if (state.titleField !== text(definition.titleField)) result.titleField = state.titleField
  return result
}

/** A custom field is sent whole; leave out the empty and default values so the stored JSON stays small. */
function cleanCustom(field: DocField): DocField {
  const copy: DocField = { ...field }
  for (const key of ['label', 'options', 'default', 'description', 'dependsOn', 'mandatoryDependsOn', 'readOnlyDependsOn', 'formula', 'fetchFrom'] as const) {
    if (text(copy[key]) === '') delete copy[key]
  }
  if (!copy.permLevel) delete copy.permLevel
  return copy
}

export function moveRow(rows: Row[], from: number, to: number): Row[] {
  if (to < 0 || to >= rows.length || from === to) return rows
  const copy = [...rows]
  const [row] = copy.splice(from, 1)
  copy.splice(to, 0, row!)
  return copy
}

/** Server errors are keyed `fields[name].property`; find the ones for one field. */
export function errorsFor(errors: Record<string, string>, name: string): Record<string, string> {
  const prefix = `fields[${name}]`
  return Object.fromEntries(Object.entries(errors)
    .filter(([path]) => path === prefix || path.startsWith(`${prefix}.`))
    .map(([path, message]) => [path.slice(prefix.length + 1), message]))
}
