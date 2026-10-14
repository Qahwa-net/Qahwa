import { describe, expect, it } from 'vitest'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import {
  emptyRow, gridColumns, moveRow, rowErrorList, rowErrors, rowsChanged, rowsFromDoc, rowsToPayload, tableFieldsOf,
} from './tables.ts'

const field = (name: string, type: DocField['type'], extra: Partial<DocField> = {}): DocField => ({
  name, type, required: false, unique: false, inListView: false, lowercase: false, ...extra,
})

const child: DocTypeMeta = {
  name: 'Order Line', isChildTable: true, isSubmittable: false, trackChanges: false,
  fields: [
    field('item', 'Data', { required: true, inListView: true }),
    field('qty', 'Int', { inListView: true }),
    field('done', 'Check', { default: '1' }),
    field('secret', 'Data', { hidden: true, inListView: true }),
    field('notes', 'Text'),
  ],
}

describe('grid columns', () => {
  it('uses the fields shown in the list and never the hidden ones', () =>
    expect(gridColumns(child).map((f) => f.name)).toEqual(['item', 'qty']))

  it('falls back to the first four editable fields', () => {
    const plain = { ...child, fields: child.fields.map((f) => ({ ...f, inListView: false })) }
    expect(gridColumns(plain).map((f) => f.name)).toEqual(['item', 'qty', 'done', 'notes'].slice(0, 4))
  })
})

describe('rows', () => {
  it('starts a new row with the field defaults', () => {
    const row = emptyRow(child)
    expect(row.name).toBeUndefined()
    expect(row.values).toMatchObject({ item: '', qty: '', done: true, notes: '' })
  })

  it('loads saved rows with their ids and converts values for inputs', () => {
    const rows = rowsFromDoc(child, [{ name: 'abc', idx: 1, item: 'Pen', qty: 3, done: false, notes: null }])
    expect(rows[0]).toMatchObject({ name: 'abc', values: { item: 'Pen', qty: '3', done: false, notes: '' } })
    expect(rowsFromDoc(child, undefined)).toEqual([])
  })

  it('builds the payload in order and identifies saved rows by name', () => {
    const rows = rowsFromDoc(child, [{ name: 'abc', item: 'Pen', qty: 3, done: true }])
    rows.push(emptyRow(child))
    const payload = rowsToPayload(child, rows)

    expect(payload[0]).toMatchObject({ name: 'abc', item: 'Pen', qty: '3', done: true })
    expect(payload[1]).not.toHaveProperty('name')
    expect(Object.keys(payload[1]!)).toEqual(['item', 'qty', 'done', 'secret', 'notes'])
  })

  it('detects added, removed, reordered and edited rows', () => {
    const saved = rowsFromDoc(child, [{ name: 'a', item: 'A', qty: 1, done: true }, { name: 'b', item: 'B', qty: 2, done: true }])
    expect(rowsChanged(saved, rowsFromDoc(child, [{ name: 'a', item: 'A', qty: 1, done: true }, { name: 'b', item: 'B', qty: 2, done: true }]))).toBe(false)
    expect(rowsChanged(saved, saved.slice(0, 1))).toBe(true)
    expect(rowsChanged(saved, moveRow(saved, 0, 1))).toBe(true)
    expect(rowsChanged(saved, [saved[0]!, { ...saved[1]!, values: { ...saved[1]!.values, qty: '9' } }])).toBe(true)
    expect(rowsChanged(saved, [...saved, emptyRow(child)])).toBe(true)
  })

  it('moves rows without mutating and ignores impossible moves', () => {
    const rows = [emptyRow(child), emptyRow(child), emptyRow(child)]
    expect(moveRow(rows, 0, 2).map((r) => r.key)).toEqual([rows[1]!.key, rows[2]!.key, rows[0]!.key])
    expect(moveRow(rows, 0, -1)).toBe(rows)
    expect(moveRow(rows, 2, 3)).toBe(rows)
  })
})

describe('server errors', () => {
  const errors = {
    title: 'This field is required.',
    items: 'Add at least one row.',
    'items[1].qty': 'Value must be a number.',
    'items[1].item': 'This field is required.',
    'items[3].name': 'This row does not belong to the document.',
    'notes[0].note': 'Other table',
  }

  it('groups errors by row and field for one table', () => {
    const { table, rows } = rowErrors(errors, 'items')
    expect(table).toBe('Add at least one row.')
    expect(rows.get(1)).toEqual({ qty: 'Value must be a number.', item: 'This field is required.' })
    expect(rows.get(3)).toEqual({ name: 'This row does not belong to the document.' })
    expect(rows.has(0)).toBe(false)
  })

  it('writes readable messages that point at the row', () => {
    const list = rowErrorList(errors, 'items', child.fields)
    expect(list).toContain('Row 2, qty: Value must be a number.')
    expect(list.every((m) => !m.includes('Other table'))).toBe(true)
  })

  it('ignores errors that clearly belong elsewhere', () => {
    expect(rowErrors({ 'itemsX[0].a': 'x', items_extra: 'y' }, 'items').rows.size).toBe(0)
  })
})

describe('table fields', () => {
  it('pairs each Table field with the child definition the server sent', () => {
    const parent: DocTypeMeta = {
      name: 'Order', isChildTable: false, isSubmittable: false, trackChanges: false,
      fields: [field('title', 'Data'), field('items', 'Table', { options: 'Order Line' }), field('gone', 'Table', { options: 'Missing' })],
    }
    const result = tableFieldsOf(parent, { 'Order Line': child })
    expect(result.map((r) => r.field.name)).toEqual(['items'])
    expect(result[0]?.child.name).toBe('Order Line')
  })
})
