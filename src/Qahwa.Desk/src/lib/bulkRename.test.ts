import { describe, expect, it } from 'vitest'
import { renamePairs } from './bulkRename.ts'

describe('bulk rename', () => {
  it('replaces the text in each name and leaves out names that do not contain it', () => {
    const { pairs, problem } = renamePairs(['OLD-1', 'OLD-2', 'KEEP-3'], 'OLD', 'NEW')

    expect(problem).toBeNull()
    expect(pairs).toEqual([{ from: 'OLD-1', to: 'NEW-1' }, { from: 'OLD-2', to: 'NEW-2' }])
  })

  it('takes the text literally: no patterns, no special replacement codes', () => {
    expect(renamePairs(['a.b', 'axb'], '.', '$&').pairs).toEqual([{ from: 'a.b', to: 'a$&b' }])
    expect(renamePairs(['x(1)'], '(1)', '[2]').pairs).toEqual([{ from: 'x(1)', to: 'x[2]' }])
  })

  it('can replace all matches or only the first', () => {
    expect(renamePairs(['a-a-a'], 'a', 'b').pairs[0]!.to).toBe('b-b-b')
    expect(renamePairs(['a-a-a'], 'a', 'b', false).pairs[0]!.to).toBe('b-a-a')
  })

  it('does nothing without text to find', () => {
    expect(renamePairs(['a'], '', 'b')).toEqual({ pairs: [], problem: null })
  })

  it('says what is wrong instead of sending it', () => {
    expect(renamePairs(['abc'], 'abc', '').problem).toMatch(/no name/)
    expect(renamePairs(['a'], 'a', 'z'.repeat(141)).problem).toMatch(/too long/)
  })

  it('reports two documents that would get the same name', () => {
    expect(renamePairs(['ax', 'aax'], 'a', '').problem).toMatch(/both be named/)
  })
})
