import { describe, expect, it } from 'vitest'
import type { DocField } from '../api/types.ts'
import { formatCell, fromInput, humanize, kindOf, operatorsFor, selectOptions, toInput } from './fields.ts'
import { decodeFilters, encodeFilters, likePattern, toApiFilters } from './filters.ts'

const field = (type: DocField['type'], extra: Partial<DocField> = {}): DocField => ({
  name: 'f', type, required: false, unique: false, inListView: false, lowercase: false, ...extra,
})

describe('filters', () => {
  it('wraps plain text in a contains pattern and escapes LIKE wildcards', () => {
    expect(likePattern('acme')).toBe('%acme%')
    expect(likePattern('a_b')).toBe('%a\\_b%')
    expect(likePattern('50%')).toBe('50%') // an explicit % means the user wrote their own pattern
  })

  it('converts to the API shape', () => {
    expect(toApiFilters([
      { field: 'customer_name', op: 'like', value: 'acme' },
      { field: 'credit_limit', op: '>', value: 5 },
      { field: 'territory', op: 'is', value: 'set' },
    ])).toEqual([
      ['customer_name', 'like', '%acme%'],
      ['credit_limit', '>', 5],
      ['territory', 'is', 'set'],
    ])
  })

  it('round-trips through the URL encoding', () => {
    const filters = [{ field: 'a', op: '=' as const, value: 'x' }, { field: 'b', op: '>=' as const, value: 3 }]
    expect(decodeFilters(encodeFilters(filters))).toEqual(filters)
    expect(encodeFilters([])).toBe('')
  })

  it('drops malformed or hostile filter input instead of throwing', () => {
    expect(decodeFilters(null)).toEqual([])
    expect(decodeFilters('not json')).toEqual([])
    expect(decodeFilters('{"a":1}')).toEqual([])
    expect(decodeFilters('[["a","DROP",1],["b","=",{"x":1}],["c","="],["d","=","ok"]]'))
      .toEqual([{ field: 'd', op: '=', value: 'ok' }])
  })
})

describe('fields', () => {
  it('humanizes names', () => expect(humanize('customer_name')).toBe('Customer name'))

  it('splits select options', () =>
    expect(selectOptions(field('Select', { options: 'Company\n Individual \n\n' }))).toEqual(['Company', 'Individual']))

  it('offers operators that suit the type', () => {
    expect(operatorsFor('Data')[0]?.op).toBe('like')
    expect(operatorsFor('Currency').map((o) => o.op)).toContain('>=')
    expect(operatorsFor('Check')).toHaveLength(1)
    expect(kindOf('Email')).toBe('text')
  })

  it('converts values for inputs and back', () => {
    expect(toInput(field('Check'), true)).toBe(true)
    expect(toInput(field('Check'), null)).toBe(false)
    expect(toInput(field('Currency'), 12.5)).toBe('12.5')
    expect(toInput(field('Data'), null)).toBe('')
    expect(toInput(field('Time'), '10:30:00.123')).toBe('10:30:00')

    const local = toInput(field('Datetime'), '2026-03-09T10:15:00Z') as string
    expect(local).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/)
    expect(fromInput(field('Datetime'), local)).toMatch(/Z$/)
    expect(fromInput(field('Datetime'), '')).toBe('')
    expect(fromInput(field('Int'), '7')).toBe('7')
  })

  it('formats cells', () => {
    expect(formatCell(field('Check'), true)).toBe('✓')
    expect(formatCell(field('Data'), null)).toBe('')
    expect(formatCell(field('Currency'), 1234.5)).toContain('1')
  })
})
