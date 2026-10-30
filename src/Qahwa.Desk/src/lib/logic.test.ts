import { describe, expect, it } from 'vitest'
import type { DocField, DocTypeMeta } from '../api/types.ts'
import {
  applyLogic, documentContext, expressionDefaults, fetchTargets, fieldState, formulaOrder, fromValue, linkTargets, lookupLinks, rowContext, toValue,
  type Session,
} from './logic.ts'
import type { TableRow } from './tables.ts'

const field = (name: string, type: DocField['type'], extra: Partial<DocField> = {}): DocField => ({
  name, type, required: false, unique: false, inListView: false, lowercase: false, ...extra,
})

const session: Session = { user: 'alice@example.com', roles: ['Sales User'], isNew: true }

const line: DocTypeMeta = {
  name: 'Doc Line', isChildTable: true, isSubmittable: false, trackChanges: false,
  fields: [
    field('item', 'Data'),
    field('qty', 'Int'),
    field('rate', 'Currency'),
    field('amount', 'Currency', { formula: 'qty * rate' }),
    field('doc_discount', 'Percent', { formula: 'parent.discount ?? 0' }),
  ],
}

const doc: DocTypeMeta = {
  name: 'Sales Doc', isChildTable: false, isSubmittable: false, trackChanges: false,
  fields: [
    field('title', 'Data', { required: true }),
    field('status', 'Select', { options: 'Draft\nClosed', default: 'Draft' }),
    field('closed_reason', 'Data', { dependsOn: "status == 'Closed'", mandatoryDependsOn: "status == 'Closed'" }),
    field('approver', 'Data', { required: true, dependsOn: "status == 'Open'" }),
    field('notes', 'Text', { readOnlyDependsOn: "status == 'Closed'" }),
    field('discount', 'Percent'),
    field('lines', 'Table', { options: 'Doc Line' }),
    field('net', 'Currency', { formula: 'round(total * (1 - discount / 100), 2)' }),
    field('total', 'Currency', { formula: "sum(lines, 'amount')" }),
    field('order_date', 'Date', { default: '=today()' }),
    field('created_by', 'Data', { default: '=user()' }),
    field('customer', 'Link', { options: 'Customer' }),
    field('customer_name', 'Data', { fetchFrom: 'customer.customer_name' }),
  ],
}

const children = { 'Doc Line': line }
const row = (item: string, qty: string, rate: string): TableRow => ({ key: item, values: { item, qty, rate, amount: '', doc_discount: '' } })

describe('value conversion', () => {
  it('turns inputs into expression values', () => {
    expect(toValue(field('n', 'Int'), '12')).toBe(12)
    expect(toValue(field('n', 'Currency'), '')).toBeNull()
    expect(toValue(field('n', 'Float'), 'abc')).toBeNull()
    expect(toValue(field('c', 'Check'), true)).toBe(true)
    expect(toValue(field('c', 'Check'), undefined)).toBeNull()
    expect(toValue(field('t', 'Data'), '')).toBeNull()
    expect(toValue(field('t', 'Data'), 'x')).toBe('x')
  })

  it('turns results back into inputs without floating point noise', () => {
    expect(fromValue(field('n', 'Currency'), 0.1 + 0.2)).toBe('0.3')
    expect(fromValue(field('n', 'Currency'), 53)).toBe('53')
    expect(fromValue(field('n', 'Int'), 2.5)).toBe('')
    expect(fromValue(field('n', 'Currency'), null)).toBe('')
    expect(fromValue(field('c', 'Check'), true)).toBe(true)
    expect(fromValue(field('d', 'Date'), '2026-03-09')).toBe('2026-03-09')
  })
})

describe('field state', () => {
  const ctx = (status: string) => documentContext(doc, children, { status }, {}, session)

  it('hides, requires and locks fields by their conditions', () => {
    expect(fieldState(doc.fields[2]!, ctx('Draft'))).toEqual({ active: false, required: false, readOnly: false })
    expect(fieldState(doc.fields[2]!, ctx('Closed'))).toEqual({ active: true, required: true, readOnly: false })
    expect(fieldState(doc.fields[4]!, ctx('Closed')).readOnly).toBe(true)
  })

  it('does not require a field that is hidden by its condition, even if it is always required', () => {
    expect(fieldState(doc.fields[3]!, ctx('Draft')).required).toBe(false)
    expect(fieldState(doc.fields[3]!, ctx('Open')).required).toBe(true)
  })

  it('treats calculated fields as read-only and never required', () => {
    const total = fieldState(field('t', 'Currency', { formula: '1', required: true }), ctx('Draft'))
    expect(total).toEqual({ active: true, required: false, readOnly: true })
  })

  it('carries on when an expression is broken', () => {
    expect(fieldState(field('x', 'Data', { dependsOn: 'a ==' }), ctx('Draft')).active).toBe(true)
  })
})

describe('applyLogic', () => {
  it('calculates rows, totals and the figures built on them', () => {
    const result = applyLogic(doc, children, { discount: '10' }, { lines: [row('Pen', '2', '1.5'), row('Book', '5', '10')] }, session)

    expect(result.tables.lines!.map((r) => r.values.amount)).toEqual(['3', '50'])
    expect(result.values.total).toBe('53')
    expect(result.values.net).toBe('47.7')
  })

  it('lets a row formula read the document', () => {
    const result = applyLogic(doc, children, { discount: '20' }, { lines: [row('Pen', '1', '1')] }, session)
    expect(result.tables.lines![0]!.values.doc_discount).toBe('20')
  })

  it('is idempotent and returns the same objects when nothing changes', () => {
    const first = applyLogic(doc, children, { discount: '10' }, { lines: [row('Pen', '2', '1.5')] }, session)
    const second = applyLogic(doc, children, first.values, first.tables, session)

    expect(second.values).toBe(first.values)
    expect(second.tables).toBe(first.tables)
  })

  it('leaves values alone for a DocType without formulas', () => {
    const plain: DocTypeMeta = { ...doc, fields: [field('a', 'Data')] }
    const values = { a: 'x' }
    expect(applyLogic(plain, {}, values, {}, session).values).toBe(values)
  })

  it('orders formulas by what they read, not by where they are declared', () => {
    const fields = [field('c', 'Int', { formula: 'b + 1' }), field('b', 'Int', { formula: 'a + 1' }), field('a', 'Int')]
    expect(formulaOrder(fields).map((f) => f.name)).toEqual(['b', 'c'])

    const meta: DocTypeMeta = { ...doc, fields }
    expect(applyLogic(meta, {}, { a: '1' }, {}, session).values).toMatchObject({ b: '2', c: '3' })
  })

  it('survives a circular definition instead of looping', () => {
    const meta: DocTypeMeta = { ...doc, fields: [field('a', 'Int', { formula: 'b + 1' }), field('b', 'Int', { formula: 'a + 1' })] }
    expect(() => applyLogic(meta, {}, {}, {}, session)).not.toThrow()
  })

  it('gives the same answers as the server for the shared example', () => {
    // The numbers match FieldLogicTests.Insert_calculates_row_amounts_totals_and_dates on the server.
    const result = applyLogic(doc, children, { discount: '10' }, { lines: [row('Pen', '2', '1.5'), row('Book', '5', '10')] }, session)
    expect([result.values.total, result.values.net]).toEqual(['53', '47.7'])
  })
})

describe('defaults and fetch', () => {
  it('fills expression defaults for empty fields only', () => {
    const filled = expressionDefaults(doc, children, { created_by: '' }, session)
    expect(filled.created_by).toBe('alice@example.com')
    expect(filled.order_date).toMatch(/^\d{4}-\d{2}-\d{2}$/)
    expect(expressionDefaults(doc, children, { created_by: 'bob' }, session).created_by).toBe('bob')
  })

  it('finds the fields that copy from a link', () => {
    expect(fetchTargets(doc, 'customer').map((t) => [t.field.name, t.source])).toEqual([['customer_name', 'customer_name']])
    expect(fetchTargets(doc, 'status')).toEqual([])
  })

  it('exposes the document to a row as parent', () => {
    const ctx = rowContext(line, { qty: '3' }, { discount: '5' }, doc, session)
    expect(ctx.fields.qty).toBe(3)
    expect(ctx.parent).toMatchObject({ discount: 5 })
  })
})

describe('lookup() in the form', () => {
  const purchase: DocTypeMeta = {
    name: 'Purchase', isChildTable: false, isSubmittable: false, trackChanges: false,
    fields: [
      field('buyer', 'Link', { options: 'Buyer' }),
      field('total', 'Currency'),
      field('buyer_limit', 'Currency', { formula: "lookup(buyer, 'credit_limit')" }),
      field('room', 'Currency', { formula: "lookup(buyer, 'credit_limit') - total" }),
    ],
  }

  it('finds the Link fields a DocType reads and the documents to fetch for a record', () => {
    expect(lookupLinks(purchase)).toEqual(['buyer'])
    expect(linkTargets(purchase, { buyer: 'B-1' })).toEqual([['Buyer', 'B-1']])
    expect(linkTargets(purchase, { buyer: '' })).toEqual([])
    expect(lookupLinks(line)).toEqual([])
  })

  it('works formulas out from the linked document, and leaves them empty until it arrives', () => {
    const base = { buyer: 'B-1', total: '120' }
    const waiting = applyLogic(purchase, {}, base, {}, session)
    expect(waiting.values.buyer_limit).toBe('')

    const withLinked: Session = { ...session, linked: () => ({ buyer: { credit_limit: 500 } }) }
    const done = applyLogic(purchase, {}, base, {}, withLinked)
    expect(done.values.buyer_limit).toBe('500')
    expect(done.values.room).toBe('380')
    expect(documentContext(purchase, {}, done.values, {}, withLinked).linked).toEqual({ buyer: { credit_limit: 500 } })
  })
})
