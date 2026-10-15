import { describe, expect, it } from 'vitest'
import type { AssignmentRule } from '../api/types.ts'
import { addUser, moveRule, newRule, toSet } from './assignmentRules.ts'

const rule = (patch: Partial<AssignmentRule> = {}): AssignmentRule => ({ ...newRule([]), ...patch })

describe('assignment rule helpers', () => {
  it('names new rules without clashing', () => {
    expect(newRule([]).name).toBe('Rule 1')
    expect(newRule([rule({ name: 'Rule 2' })]).name).toBe('Rule 3')
  })

  it('sends only what a strategy uses and drops blank texts', () => {
    const out = toSet({ docType: 'T', rules: [
      rule({ strategy: 'Field', users: ['a@x.com'], userField: ' handler ', condition: '  ', description: ' hi ' }),
      rule({ strategy: 'RoundRobin', users: ['a@x.com'], userField: 'handler', closeCondition: 'x == 1' }),
    ] }).rules
    expect(out[0]).toMatchObject({ users: [], userField: 'handler', condition: null, description: 'hi' })
    expect(out[1]).toMatchObject({ users: ['a@x.com'], userField: null, closeCondition: 'x == 1' })
  })

  it('moves rules within bounds', () => {
    expect(moveRule(['a', 'b', 'c'], 1, -1)).toEqual(['b', 'a', 'c'])
    expect(moveRule(['a', 'b', 'c'], 2, 1)).toEqual(['a', 'b', 'c'])
    expect(moveRule(['a', 'b', 'c'], 0, -1)).toEqual(['a', 'b', 'c'])
  })

  it('adds people once', () => {
    expect(addUser(['a@x.com'], ' b@x.com ')).toEqual(['a@x.com', 'b@x.com'])
    const same = ['a@x.com']
    expect(addUser(same, 'a@x.com')).toBe(same)
    expect(addUser(same, '  ')).toBe(same)
  })
})
