import { describe, expect, it } from 'vitest'
import type { DocField } from '../api/types.ts'
import { dateFields, filterValue, newItem, numericFields, tileTitle, toItem } from './dashboards.ts'

const field = (name: string, type: DocField['type']) => ({ name, type }) as DocField

describe('dashboard helpers', () => {
  it('sorts fields into numbers and dates', () => {
    const fields = [field('a', 'Data'), field('b', 'Currency'), field('c', 'Date'), field('d', 'Datetime'), field('e', 'Int')]
    expect(numericFields(fields).map((f) => f.name)).toEqual(['b', 'e'])
    expect(dateFields(fields).map((f) => f.name)).toEqual(['c', 'd'])
  })

  it('names a tile that has no title', () => {
    expect(tileTitle({ ...newItem('Number', 'Sale'), title: ' Mine ' })).toBe('Mine')
    expect(tileTitle(newItem('Number', 'Sale'))).toBe('Sale count')
    expect(tileTitle({ ...newItem('Chart', 'Sale'), function: 'sum', valueField: 'amount', groupBy: 'region' })).toBe('Sum of amount by region')
    expect(tileTitle(newItem('List', 'Sale'))).toBe('Sale list')
  })

  it('sends only what a type uses', () => {
    const chart = toItem({ ...newItem('Chart', 'Sale'), function: 'count', valueField: 'amount', groupBy: 'region', columns: ['x'], orderBy: 'x', period: 'month' })
    expect(chart).toMatchObject({ valueField: null, groupBy: 'region', period: 'month', columns: [], orderBy: null })
    const list = toItem({ ...newItem('List', 'Sale'), function: 'sum', groupBy: 'region', columns: ['a'], orderBy: 'a' })
    expect(list).toMatchObject({ function: 'count', groupBy: null, period: null, columns: ['a'], orderBy: 'a' })
    const number = toItem({ ...newItem('Number', 'Sale'), function: 'sum', valueField: 'amount', title: ' T ' })
    expect(number).toMatchObject({ valueField: 'amount', title: 'T' })
  })

  it('drops filters with no field and fills the value of "is"', () => {
    const out = toItem({ ...newItem('Number', 'Sale'), filters: [{ field: '', op: '=', value: 'x' }, { field: 'status', op: 'is', value: '' }, { field: 'n', op: '>', value: undefined }] })
    expect(out.filters).toEqual([{ field: 'status', op: 'is', value: 'set' }, { field: 'n', op: '>', value: '' }])
  })

  it('gives filter values their proper type', () => {
    expect(filterValue({ field: 'a', op: '>', value: '5' }, 'Currency')).toBe(5)
    expect(filterValue({ field: 'a', op: '=', value: 'abc' }, 'Currency')).toBe('abc')
    expect(filterValue({ field: 'a', op: '=', value: 'true' }, 'Check')).toBe(true)
    expect(filterValue({ field: 'a', op: '=', value: '5' }, 'Data')).toBe('5')
  })
})
