import { describe, expect, it } from 'vitest'
import { pageItems, paletteItems, score, type SearchResponse } from './search.ts'

const metas = [{ name: 'Customer', module: 'Selling' }, { name: 'Sales Order', module: 'Selling' }, { name: 'Role', module: 'Core' }]
const pages = pageItems(false, false)

describe('palette matching', () => {
  it('scores a start above a word above a part, ignoring case and accents', () => {
    expect(score('Customer', 'cus')).toBe(0)
    expect(score('Sales Order', 'ord')).toBe(1)
    expect(score('Sales Order', 'rde')).toBe(2)
    expect(score('Café', 'cafe')).toBe(0)
    expect(score('Customer', 'xyz')).toBeNull()
    expect(score('Customer', '')).toBe(2)
  })

  it('shows pages and DocTypes at once, best matches first, without system DocTypes', () => {
    const items = paletteItems('o', metas, pages, null)

    expect(items.map((i) => i.label)).not.toContain('Role')
    expect(items[0]!.kind).toBe('page')
    expect(items.find((i) => i.label === 'Sales Order')?.to).toBe('/app/Sales%20Order')
  })

  it('adds the creatable DocTypes and the documents the server found', () => {
    const found: SearchResponse = {
      doctypes: [{ name: 'Customer', module: 'Selling', can_create: true }, { name: 'Sales Order', module: 'Selling', can_create: false }],
      documents: [{ doctype: 'Customer', name: 'CUST-0001', title: 'Acme Coffee' }, { doctype: 'Sales Order', name: 'SO-0001', title: null }],
    }
    const items = paletteItems('c', metas, pages, found)

    expect(items.find((i) => i.kind === 'new')).toMatchObject({ label: 'New Customer', to: '/app/Customer/new' })
    expect(items.filter((i) => i.kind === 'new')).toHaveLength(1) // no create right: no "New Sales Order"
    expect(items.find((i) => i.kind === 'document' && i.hint === 'Customer')).toMatchObject({ label: 'Acme Coffee (CUST-0001)', to: '/app/Customer/CUST-0001' })
    expect(items.find((i) => i.label === 'SO-0001')?.to).toBe('/app/Sales%20Order/SO-0001')
  })

  it('understands "new customer" and "open customer"', () => {
    const found: SearchResponse = { doctypes: [{ name: 'Customer', can_create: true }], documents: [{ doctype: 'Customer', name: 'CUST-0001' }] }

    expect(paletteItems('new cus', metas, pages, found).map((i) => i.label)).toEqual(['New Customer'])
    expect(paletteItems('open cus', metas, pages, found).some((i) => i.id === 'doctype:Customer')).toBe(true)
  })

  it('never builds an address from unchecked text and shows no duplicates', () => {
    const found: SearchResponse = { doctypes: [], documents: [{ doctype: 'Customer', name: '../../x?y#z' }, { doctype: 'Customer', name: '../../x?y#z' }] }
    const docs = paletteItems('x', metas, pages, found).filter((i) => i.kind === 'document')

    expect(docs).toHaveLength(1)
    expect(docs[0]!.to).toBe('/app/Customer/..%2F..%2Fx%3Fy%23z')
  })

  it('offers tools only to people who may design', () => {
    expect(pageItems(false, false).some((p) => p.label === 'Brand')).toBe(false)
    expect(pageItems(true, false).some((p) => p.label === 'Brand')).toBe(true)
    expect(pageItems(true, false).some((p) => p.label === 'Backup & Restore')).toBe(false)
    expect(pageItems(true, true).some((p) => p.label === 'Backup & Restore')).toBe(true)
  })
})
