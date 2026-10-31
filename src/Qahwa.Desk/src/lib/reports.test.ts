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

describe('calculated columns', () => {
  const list = { ...newDraft('Sale'), columns: ['amount'] }

  it('are sent with their label and formula, blank ones dropped, and only for lists', () => {
    const draft = { ...list, calculated: [{ label: ' Total ', expression: ' amount * qty ' }, { label: '', expression: '' }] }
    expect((toDefinition(draft) as { calculated: unknown }).calculated).toEqual([{ label: 'Total', expression: 'amount * qty' }])
    const summary = { ...draft, mode: 'summary' as const, groupBy: 'region', aggregates: [{ function: 'count', field: '' }] }
    expect(toDefinition(summary)).not.toHaveProperty('calculated')
  })

  it('round trip through a saved definition', () => {
    const back = fromDefinition('R', false, { docType: 'Sale', columns: ['amount'], calculated: [{ label: 'Double', expression: 'amount * 2' }] })
    expect(back.calculated).toEqual([{ label: 'Double', expression: 'amount * 2' }])
    expect(fromDefinition('R', false, { docType: 'Sale', columns: ['amount'] }).calculated).toEqual([])
  })

  it('are checked while typing: a name is needed and the formula must be valid', () => {
    expect(draftProblem({ ...list, calculated: [{ label: 'T', expression: 'amount * 2' }] })).toBeNull()
    expect(draftProblem({ ...list, calculated: [{ label: '', expression: 'amount' }] })).toBe('Name each calculated column.')
    expect(draftProblem({ ...list, calculated: [{ label: 'T', expression: 'amount *' }] })).toMatch(/Calculated column "T"/)
    expect(draftProblem({ ...list, calculated: [{ label: '', expression: '' }] })).toBeNull() // an empty row is ignored
  })
})

