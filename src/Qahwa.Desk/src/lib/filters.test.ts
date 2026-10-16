import { describe, expect, it } from 'vitest'
import { describeFilter, decodeFilters, encodeFilters, toApiFilters, type Filter } from './filters.ts'
import { operatorsFor } from './fields.ts'

describe('the extra filter operators', () => {
  it('round-trip through the address and are described in words', () => {
    const filters: Filter[] = [
      { field: 'title', op: 'not like', value: 'draft' },
      { field: 'due', op: 'between', value: ['2026-01-01', '2026-01-31'] },
      { field: 'due', op: 'timespan', value: 'this month' },
    ]

    expect(decodeFilters(encodeFilters(filters))).toEqual(filters)
    expect(describeFilter(filters[0]!, 'Title')).toBe('Title does not contain draft')
    expect(describeFilter(filters[1]!, 'Due')).toBe('Due between 2026-01-01 and 2026-01-31')
    expect(describeFilter(filters[2]!, 'Due')).toBe('Due is this month')
  })

  it('wraps the text of "not like" like "like" and sends the two ends of "between" as they are', () => {
    expect(toApiFilters([{ field: 't', op: 'not like', value: 'a_b' }])).toEqual([['t', 'not like', String.raw`%a\_b%`]])
    expect(toApiFilters([{ field: 'n', op: 'between', value: [1, 5] }])).toEqual([['n', 'between', [1, 5]]])
  })

  it('drops malformed ones from the address instead of breaking the page', () => {
    const bad = JSON.stringify([
      ['due', 'between', 'x'], ['due', 'between', ['a']], ['due', 'between', [{}, 1]], ['due', 'timespan', 'fortnight'], ['due', 'timespan', 5],
      ['due', 'regex', '.*'], ['due', 'between', ['2026-01-01', '2026-01-31']],
    ])

    expect(decodeFilters(bad)).toEqual([{ field: 'due', op: 'between', value: ['2026-01-01', '2026-01-31'] }])
  })

  it('are offered for the right kinds of fields', () => {
    const ops = (type: string) => operatorsFor(type as never).map((o) => o.op)

    expect(ops('Data')).toContain('not like')
    expect(ops('Date')).toEqual(expect.arrayContaining(['between', 'timespan']))
    expect(ops('Datetime')).toEqual(expect.arrayContaining(['between', 'timespan']))
    expect(ops('Float')).toContain('between')
    expect(ops('Float')).not.toContain('timespan')
    expect(ops('Check')).toEqual(['='])
  })
})
