import { describe, expect, it } from 'vitest'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import {
  emptyRow, gridColumns, moveRow, nestedErrors, nestedFieldsOf, rowErrorList, rowErrors, rowsChanged, rowsFromDoc, rowsToPayload, tableFieldsOf, withNested,
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

describe('tables inside rows', () => {
  const part: DocTypeMeta = {
    name: 'Line Part', isChildTable: true, isSubmittable: false, trackChanges: false,
    fields: [field('part_name', 'Data', { required: true, inListView: true }), field('qty', 'Int', { inListView: true })],
  }
  const line: DocTypeMeta = {
    name: 'Kit Line', isChildTable: true, isSubmittable: false, trackChanges: false,
    fields: [field('title', 'Data', { inListView: true }), field('parts', 'Table', { options: 'Line Part' })],
  }
  const children = { 'Kit Line': line, 'Line Part': part }
  const saved = [{ name: 'l1', idx: 1, title: 'Side', parts: [{ name: 'p1', idx: 1, part_name: 'bolt', qty: 4 }, { name: 'p2', idx: 2, part_name: 'nut', qty: 6 }] }]

  it('finds the tables a child holds, only when the server sent their DocTypes', () => {
    expect(nestedFieldsOf(line, children).map((n) => [n.field.name, n.child.name])).toEqual([['parts', 'Line Part']])
    expect(nestedFieldsOf(line)).toEqual([])
    expect(nestedFieldsOf(part, children)).toEqual([])
  })

  it('loads the inner rows with the row that holds them and does not show the table as a cell', () => {
    const rows = rowsFromDoc(line, saved, children)

    expect(rows[0]!.values).toEqual({ title: 'Side' })
    expect(rows[0]!.tables!.parts!.map((r) => [r.name, r.values.part_name, r.values.qty])).toEqual([['p1', 'bolt', '4'], ['p2', 'nut', '6']])
    expect(rowsFromDoc(line, saved)[0]!.tables).toBeUndefined(); // without the child DocTypes the row is as before
  })

  it('sends the inner rows inside their row, saved ones by name', () => {
    const rows = rowsFromDoc(line, saved, children)
    const edited = withNested(rows[0]!, 'parts', [...rows[0]!.tables!.parts!, { ...emptyRow(part), values: { part_name: 'washer', qty: '2' } }])

    expect(rowsToPayload(line, [edited], children)).toEqual([
      { name: 'l1', title: 'Side', parts: [
        { name: 'p1', part_name: 'bolt', qty: '4' }, // numbers go as text, like every cell: the server converts them
        { name: 'p2', part_name: 'nut', qty: '6' },
        { part_name: 'washer', qty: '2' },
      ] },
    ])
  })

  it('does not send an inner table that was never loaded or built (a new row without parts)', () => {
    expect(rowsToPayload(line, [{ ...emptyRow(line), values: { title: 'New' } }], children)).toEqual([{ title: 'New' }])
  })

  it('notices a change made only inside an inner table', () => {
    const before = rowsFromDoc(line, saved, children)
    expect(rowsChanged(before, rowsFromDoc(line, saved, children))).toBe(false)

    const edited = [withNested(before[0]!, 'parts', before[0]!.tables!.parts!.map((r, i) => (i === 0 ? { ...r, values: { ...r.values, qty: '9' } } : r)))]
    expect(rowsChanged(before, edited)).toBe(true)
    expect(rowsChanged(before, [withNested(before[0]!, 'parts', before[0]!.tables!.parts!.slice(1))])).toBe(true) // a row removed
  })

  it('gives an inner table the errors for its own rows, with the outer path taken off', () => {
    const errors = { 'lines[1].parts[0].qty': 'Too many', 'lines[1].parts': 'Add at least one row.', 'lines[0].title': 'Required', 'lines[10].parts[0].qty': 'other row' }

    expect(nestedErrors(errors, 'lines', 1)).toEqual({ 'parts[0].qty': 'Too many', parts: 'Add at least one row.' })
    expect(rowErrors(nestedErrors(errors, 'lines', 1), 'parts').rows.get(0)).toEqual({ qty: 'Too many' })
  })
})

