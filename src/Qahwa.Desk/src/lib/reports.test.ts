import { describe, expect, it } from 'vitest'
import type { DocField } from '../api/types.ts'
import { draftProblem, filterValue, fromDefinition, isNumeric, newDraft, reportFields, toDefinition, toggle } from './reports.ts'

const field = (name: string, type: string): DocField => ({ name, type }) as DocField

describe('report builder helpers', () => {
  it('offers only real, non-password columns and knows the numeric ones', () => {
    const fields = [field('a', 'Data'), field('pw', 'Password'), field('n', 'Currency'), field('s', 'SectionBreak'), field('t', 'Table')]
    expect(reportFields(fields).map((f) => f.name)).toEqual(['a', 'n'])
    expect(isNumeric(field('n', 'Currency'))).toBe(true)
    expect(isNumeric(field('a', 'Data'))).toBe(false)
  })

  it('builds a list definition and drops half-filled filters', () => {
    const draft = { ...newDraft('Sale'), columns: ['customer', 'amount'], orderBy: 'amount', filters: [
      { field: 'region', op: '=', value: 'North' }, { field: 'region', op: '=', value: '  ' }, { field: '', op: '=', value: 'x' }, { field: 'status', op: 'is', value: 'set' },
    ] }
    const definition = toDefinition(draft) as { columns: string[]; filters: unknown[]; groupBy: unknown; aggregates: unknown[] }
    expect(definition.columns).toEqual(['customer', 'amount'])
    expect(definition.filters).toHaveLength(2)
    expect(definition.groupBy).toBeNull()
    expect(definition.aggregates).toEqual([])
  })

  it('builds a summary definition where count has no field', () => {
    const draft = { ...newDraft('Sale'), mode: 'summary' as const, groupBy: 'region', aggregates: [{ function: 'count', field: 'ignored' }, { function: 'sum', field: 'amount' }] }
    const definition = toDefinition(draft) as { aggregates: { function: string; field: string | null }[]; columns: string[] }
    expect(definition.aggregates).toEqual([{ function: 'count', field: null }, { function: 'sum', field: 'amount' }])
    expect(definition.columns).toEqual([])
  })

  it('reads lists for "is one of" and nothing odd otherwise', () => {
    expect(filterValue({ field: 'a', op: 'in', value: 'x, y ,, z' })).toEqual(['x', 'y', 'z'])
    expect(filterValue({ field: 'a', op: '=', value: "x'; drop" })).toBe("x'; drop")
  })

  it('round trips a saved definition', () => {
    const draft = { ...newDraft('Sale'), mode: 'summary' as const, groupBy: 'region', aggregates: [{ function: 'sum', field: 'amount' }], filters: [{ field: 'region', op: 'in', value: 'N, S' }], limit: 50 }
    const back = fromDefinition('T', true, JSON.parse(JSON.stringify(toDefinition(draft))))
    expect(back).toMatchObject({ title: 'T', isPublic: true, mode: 'summary', groupBy: 'region', limit: 50, docType: 'Sale' })
    expect(back.filters[0]).toEqual({ field: 'region', op: 'in', value: 'N, S' })
  })

  it('says what is missing', () => {
    expect(draftProblem(newDraft())).toBe('Choose a DocType.')
    expect(draftProblem(newDraft('Sale'))).toBe('Choose at least one column to show.')
    expect(draftProblem({ ...newDraft('Sale'), mode: 'summary' })).toBe('Choose a field to group by.')
    expect(draftProblem({ ...newDraft('Sale'), mode: 'summary', groupBy: 'r', aggregates: [{ function: 'sum', field: '' }] })).toBe('Choose a numeric field for each number.')
    expect(draftProblem({ ...newDraft('Sale'), columns: ['a'] })).toBeNull()
  })

  it('toggles items in a list without changing the original', () => {
    const list = ['a']
    expect(toggle(list, 'b')).toEqual(['a', 'b'])
    expect(toggle(list, 'a')).toEqual([])
    expect(list).toEqual(['a'])
  })
})
