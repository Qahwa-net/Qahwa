import { describe, expect, it } from 'vitest'
import type { DesignerDetail, FieldTypeInfo } from '../api/types.ts'
import {
  buildAutoname, emptyDraft, errorMap, fromDetail, moveItem, newField, parseAutoname, slugify, toDefinition, uniqueName,
} from './designer.ts'

const info = (type: FieldTypeInfo['type'], label = type as string): FieldTypeInfo => ({
  type, label, group: 'Text', hint: '', options: 'None', supported: true,
})

describe('slugify', () => {
  it('turns labels into valid field names', () => {
    expect(slugify('Credit Limit (USD)')).toBe('credit_limit_usd')
    expect(slugify('  Customer   Name ')).toBe('customer_name')
    expect(slugify('2nd address')).toBe('f_2nd_address')
    expect(slugify('')).toBe('')
    expect(slugify('اسم')).toBe('')
    expect(slugify('Café')).toBe('cafe')
    expect(slugify('x'.repeat(100))).toHaveLength(60)
  })

  it('always produces something the server accepts or nothing', () => {
    for (const label of ['A', 'a b', 'Total $', '__x__', 'ÄÖÜ', '1', '9 lives']) {
      const slug = slugify(label)
      expect(slug === '' || /^[a-z][a-z0-9_]{0,62}$/.test(slug)).toBe(true)
    }
  })
})

describe('field helpers', () => {
  it('finds a free name', () => {
    expect(uniqueName('rating', ['a', 'b'])).toBe('rating')
    expect(uniqueName('rating', ['rating'])).toBe('rating_2')
    expect(uniqueName('rating', ['rating', 'rating_2'])).toBe('rating_3')
  })

  it('creates fields with unique names and layout defaults', () => {
    const first = newField(info('Data', 'Data'), [])
    const second = newField(info('Data', 'Data'), [first.name])
    expect(first.name).toBe('data')
    expect(second.name).toBe('data_2')
    expect(first.locked).toBe(false)

    const section = newField(info('SectionBreak', 'Section'), [])
    expect(section.label).toBe('Section')
    expect(section.name).toMatch(/^[a-z][a-z0-9_]*$/)
  })

  it('moves items without mutating and ignores impossible moves', () => {
    const items = ['a', 'b', 'c']
    expect(moveItem(items, 0, 2)).toEqual(['b', 'c', 'a'])
    expect(moveItem(items, 2, 1)).toEqual(['a', 'c', 'b'])
    expect(moveItem(items, 0, -1)).toBe(items)
    expect(moveItem(items, 1, 3)).toBe(items)
    expect(items).toEqual(['a', 'b', 'c'])
  })
})

describe('naming rules', () => {
  it('round-trips every kind', () => {
    for (const rule of ['prompt', 'field:supplier_name', 'SUP-.#####', 'INV/.##']) {
      const parsed = parseAutoname(rule)
      expect(buildAutoname(parsed)).toBe(rule)
    }
    expect(parseAutoname(undefined).autonameKind).toBe('hash')
    expect(buildAutoname(parseAutoname('hash'))).toBeUndefined()
  })

  it('keeps series digits within the server limits', () => {
    expect(buildAutoname({ autonameKind: 'series', autonameField: '', seriesPrefix: 'X-', seriesDigits: 99 })).toBe('X-.##########')
    expect(buildAutoname({ autonameKind: 'series', autonameField: '', seriesPrefix: 'X-', seriesDigits: 0 })).toBe('X-.#')
  })
})

describe('draft conversion', () => {
  const detail: DesignerDetail = {
    source: 'Custom', editable: true, modified: '2026-01-01T00:00:00Z', documentCount: 2,
    definition: {
      name: 'Supplier', module: 'Buying', titleField: 'supplier_name', autoname: 'SUP-.#####',
      isChildTable: false, isSubmittable: false, trackChanges: false,
      fields: [{ name: 'supplier_name', type: 'Data', label: 'Name', required: true, unique: true, inListView: true, lowercase: false }],
      permissions: [{ role: 'System Manager', read: true, write: true, create: true, delete: true, ifOwner: false }],
    },
  }

  it('loads existing fields as locked', () => {
    const draft = fromDetail(detail)
    expect(draft.fields[0]?.locked).toBe(true)
    expect(draft.seriesPrefix).toBe('SUP-')
    expect(draft.documentCount).toBe(2)
  })

  it('writes a compact definition without empty values or editor-only properties', () => {
    const draft = fromDetail(detail)
    draft.fields[0]!.description = '   '
    const definition = toDefinition(draft)

    expect(definition.autoname).toBe('SUP-.#####')
    expect(definition.fields[0]).toEqual({
      name: 'supplier_name', type: 'Data', label: 'Name', required: true, unique: true, inListView: true,
      options: undefined, default: undefined, lowercase: undefined, description: undefined, hidden: undefined, readOnly: undefined,
    })
    expect(JSON.stringify(definition)).not.toContain('key')
    expect(JSON.stringify(definition)).not.toContain('locked')
  })

  it('drops permission rows without a role', () => {
    const draft = emptyDraft()
    draft.permissions.push({ role: '  ', read: true, write: false, create: false, delete: false, ifOwner: false })
    expect(toDefinition(draft).permissions).toHaveLength(1)
  })

  it('leaves out naming, title and permissions for child tables, which have none of their own', () => {
    const draft = fromDetail(detail)
    draft.isChildTable = true
    const definition = toDefinition(draft)

    expect(definition.isChildTable).toBe(true)
    expect(definition.autoname).toBeUndefined()
    expect(definition.titleField).toBeUndefined()
    expect(definition.permissions).toEqual([])
    expect(toDefinition(fromDetail(detail)).isChildTable).toBeUndefined()
  })

  it('keeps permission levels, leaving level 0 out and dropping levels in child tables', () => {
    const draft = fromDetail(detail)
    draft.fields[0]!.permLevel = 2
    draft.permissions.push({ role: 'HR', read: true, write: false, create: false, delete: false, ifOwner: false, permLevel: 2 })

    const definition = toDefinition(draft)
    expect(definition.fields[0]!.permLevel).toBe(2)
    expect(definition.permissions.at(-1)!.permLevel).toBe(2)
    expect(fromDetail({ ...detail, definition: { ...detail.definition, fields: [{ ...detail.definition.fields[0]!, permLevel: 3 }] } }).fields[0]!.permLevel).toBe(3)

    draft.isChildTable = true
    expect(toDefinition(draft).fields[0]!.permLevel).toBeUndefined()
  })

  it('writes the logic properties and rules, and leaves them out when empty', () => {
    const draft = fromDetail(detail)
    draft.fields[0]!.dependsOn = "status == 'Open'"
    draft.fields[0]!.formula = '  '
    draft.fields[0]!.fetchFrom = 'customer.name'
    draft.fields[0]!.fetchIfEmpty = true
    draft.validations = [
      { expression: 'qty > 0', message: 'Positive please', field: 'qty' },
      { expression: '', message: '', field: null },
    ]

    const definition = toDefinition(draft)

    expect(definition.fields[0]).toMatchObject({ dependsOn: "status == 'Open'", fetchFrom: 'customer.name', fetchIfEmpty: true })
    expect(definition.fields[0]?.formula).toBeUndefined()
    expect(definition.validations).toEqual([{ expression: 'qty > 0', message: 'Positive please', field: 'qty' }])

    draft.fields[0]!.fetchFrom = null
    expect(toDefinition(draft).fields[0]?.fetchIfEmpty).toBeUndefined()
    draft.validations = []
    expect(toDefinition(draft).validations).toBeUndefined()
  })

  it('loads logic properties from the server definition', () => {
    const withLogic: DesignerDetail = {
      ...detail,
      definition: {
        ...detail.definition,
        fields: [{ ...detail.definition.fields[0]!, formula: 'qty * rate', dependsOn: 'qty > 0' }],
        validations: [{ expression: 'qty > 0', message: 'm', field: 'qty' }],
      },
    }
    const draft = fromDetail(withLogic)

    expect(draft.fields[0]).toMatchObject({ formula: 'qty * rate', dependsOn: 'qty > 0', fetchIfEmpty: false })
    expect(draft.validations).toEqual([{ expression: 'qty > 0', message: 'm', field: 'qty' }])
  })

  it('starts new DocTypes with full access for System Manager', () => {
    expect(emptyDraft().permissions[0]).toMatchObject({ role: 'System Manager', read: true, delete: true })
  })
})

describe('errorMap', () => {
  it('keeps the first message per path', () => {
    expect(errorMap([{ field: 'a', message: '1' }, { field: 'a', message: '2' }, { field: 'b', message: '3' }]))
      .toEqual({ a: '1', b: '3' })
  })
})
