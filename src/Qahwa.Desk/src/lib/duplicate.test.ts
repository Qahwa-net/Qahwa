import { describe, expect, it } from 'vitest'
import type { Doc, DocField, DocTypeMeta } from '../api/types.ts'
import { copyOf } from './duplicate.ts'

const field = (name: string, type = 'Data', extra: Partial<DocField> = {}): DocField => ({ name, type, label: name, required: false, unique: false, ...extra } as unknown as DocField)
const line = { name: 'Line', fields: [field('item'), field('sku', 'Data', { unique: true }), field('qty', 'Int')] } as unknown as DocTypeMeta
const meta = {
  name: 'Order',
  fields: [field('customer'), field('code', 'Data', { unique: true }), field('pin', 'Password'), field('total', 'Float'), field('lines', 'Table', { options: 'Line' })],
} as unknown as DocTypeMeta

describe('duplicating a document', () => {
  const doc = {
    name: 'SO-0001', creation: 'x', modified: 'y', modified_by: 'a', owner: 'a', docstatus: 1, idx: 0,
    customer: 'Acme', code: 'UNIQUE-1', pin: 'secret', total: 12.5,
    lines: [{ name: 'row1', parent: 'SO-0001', parenttype: 'Order', parentfield: 'lines', idx: 1, item: 'Tea', sku: 'S1', qty: 3 }],
  } as unknown as Doc

  it('copies the fields, leaves out what must stay unique, passwords and system fields', () => {
    const copy = copyOf(meta, { Line: line }, doc)

    expect(copy.customer).toBe('Acme')
    expect(copy.total).toBe(12.5)
    expect(copy).not.toHaveProperty('code')
    expect(copy).not.toHaveProperty('pin')
    for (const key of ['creation', 'modified', 'modified_by', 'owner', 'docstatus', 'idx']) expect(copy).not.toHaveProperty(key)
    expect(copy.name).toBe('')
  })

  it('copies table rows as new rows without their ids, parents or unique columns', () => {
    const copy = copyOf(meta, { Line: line }, doc)

    expect(copy.lines).toEqual([{ item: 'Tea', qty: 3 }])
  })

  it('copes with a missing table or an unknown child', () => {
    const noRows = copyOf(meta, { Line: line }, { ...doc, lines: undefined } as unknown as Doc)
    const noChild = copyOf(meta, {}, doc)

    expect(noRows.lines).toEqual([])
    expect(noChild.lines).toEqual([{ item: 'Tea', sku: 'S1', qty: 3 }]) // without the child's definition only the system keys are known
  })
})
