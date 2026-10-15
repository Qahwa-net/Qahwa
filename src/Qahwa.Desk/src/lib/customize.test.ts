import { describe, expect, it } from 'vitest'
import type { CustomizationView, DocField, DocTypeMeta, FieldTypeInfo } from '../api/types.ts'
import { addableTypes, customName, errorsFor, moveRow, newCustomField, stateFromView, toCustomization } from './customize.ts'

const field = (name: string, extra: Partial<DocField> = {}): DocField => ({
  name, type: 'Data', required: false, unique: false, inListView: false, lowercase: false, ...extra,
})

const definition: DocTypeMeta = {
  name: 'Customer', isChildTable: false, isSubmittable: false, trackChanges: false, titleField: 'customer_name',
  fields: [
    field('customer_name', { label: 'Name', required: true }),
    field('kind', { type: 'Select', options: 'A\nB' }),
    field('limit', { type: 'Currency' }),
  ],
}

const view = (customFields: DocField[] = []): CustomizationView => ({
  name: 'Customer', source: 'App', customized: customFields.length > 0, definition,
  effective: { ...definition, fields: [...definition.fields, ...customFields] },
  customization: { customFields, overrides: {}, order: [] }, canEdit: true,
})

const info = (type: FieldTypeInfo['type'], supported = true): FieldTypeInfo => ({ type, label: type, group: 'Text', hint: '', options: 'None', supported })

describe('customize state', () => {
  it('starts from the DocType as it works now and marks the custom fields', () => {
    const state = stateFromView(view([field('custom_phone')]))

    expect(state.rows.map((r) => r.field.name)).toEqual(['customer_name', 'kind', 'limit', 'custom_phone'])
    expect(state.rows.map((r) => r.custom)).toEqual([false, false, false, true])
    expect(state.titleField).toBe('customer_name')
  })

  it('sends nothing when nothing changed', () => {
    const result = toCustomization(stateFromView(view()), definition)

    expect(result).toEqual({ customFields: [], overrides: {}, order: [] })
  })

  it('sends only the properties that differ, and an empty text to clear one', () => {
    const state = stateFromView(view())
    state.rows[0]!.field = { ...state.rows[0]!.field, label: 'Customer', required: false, description: 'Who' }
    state.rows[1]!.field = { ...state.rows[1]!.field, options: 'A\nB\nC' }
    state.rows[2]!.field = { ...state.rows[2]!.field, hidden: true, permLevel: 2 }

    const { overrides } = toCustomization(state, definition)

    expect(overrides.customer_name).toEqual({ label: 'Customer', required: false, description: 'Who' })
    expect(overrides.kind).toEqual({ options: 'A\nB\nC' })
    expect(overrides.limit).toEqual({ hidden: true, permLevel: 2 })

    const cleared = stateFromView(view())
    cleared.rows[0]!.field = { ...cleared.rows[0]!.field, label: '' }
    expect(toCustomization(cleared, definition).overrides.customer_name).toEqual({ label: '' })
  })

  it('sends custom fields whole but without empty values, and never as overrides', () => {
    const state = stateFromView(view())
    const row = newCustomField(info('Data'), 'Phone number', state.rows.map((r) => r.field.name))
    state.rows.push(row)

    const result = toCustomization(state, definition)

    expect(result.customFields).toHaveLength(1)
    expect(result.customFields[0]).toMatchObject({ name: 'custom_phone_number', type: 'Data', label: 'Phone number' })
    expect(result.customFields[0]).not.toHaveProperty('options')
    expect(result.overrides).toEqual({})
    expect(result.order).toEqual([]) // appended at the end is the usual order
  })

  it('sends the order only when it differs from the usual one', () => {
    const state = stateFromView(view())
    state.rows = moveRow(state.rows, 2, 0)

    expect(toCustomization(state, definition).order).toEqual(['limit', 'customer_name', 'kind'])
  })

  it('sends the title field only when it changed, empty meaning none', () => {
    const state = stateFromView(view())
    expect(toCustomization(state, definition).titleField).toBeUndefined()

    state.titleField = 'kind'
    expect(toCustomization(state, definition).titleField).toBe('kind')
    state.titleField = ''
    expect(toCustomization(state, definition).titleField).toBe('')
  })
})

describe('custom fields', () => {
  it('are named from their label, with a custom_ prefix, never colliding', () => {
    expect(customName('Phone Number', [])).toBe('custom_phone_number')
    expect(customName('Phone Number', ['custom_phone_number'])).toBe('custom_phone_number_2')
    expect(customName('', [])).toBe('custom_field')
    expect(customName('2nd line', [])).toBe('custom_2nd_line')
  })

  it('cannot be tables or passwords, or unsupported types', () => {
    const types = addableTypes([info('Data'), info('Table'), info('Password'), info('Json', false), info('Date')])

    expect(types.map((t) => t.type)).toEqual(['Data', 'Date'])
  })
})

describe('helpers', () => {
  it('moves rows and ignores impossible moves', () => {
    const rows = stateFromView(view()).rows
    expect(moveRow(rows, 0, 1).map((r) => r.key)).toEqual(['kind', 'customer_name', 'limit'])
    expect(moveRow(rows, 0, -1)).toBe(rows)
    expect(moveRow(rows, 2, 3)).toBe(rows)
  })

  it('finds the server errors of one field by its name', () => {
    const errors = { 'fields[kind].options': 'Bad choices', 'fields[kind]': 'Not a field', 'fields[limit].label': 'x', order: 'y' }

    expect(errorsFor(errors, 'kind')).toEqual({ options: 'Bad choices', '': 'Not a field' })
    expect(errorsFor(errors, 'nothing')).toEqual({})
  })
})
