import { describe, expect, it } from 'vitest'
import { describeFilter, toApiFilters } from './filters.ts'

describe('tag and like filters', () => {
  it('read as words and are sent as they are', () => {
    expect(describeFilter({ field: '_tag', op: '=', value: 'urgent' }, '_tag')).toBe('Tagged urgent')
    expect(describeFilter({ field: '_liked_by', op: '=', value: '__user' }, '_liked_by')).toBe('Liked by me')
    expect(describeFilter({ field: '_liked_by', op: '=', value: 'a@b.c' }, '_liked_by')).toBe('Liked by a@b.c')
    expect(toApiFilters([{ field: '_tag', op: '=', value: 'urgent' }])).toEqual([['_tag', '=', 'urgent']])
  })
})
