import { describe, expect, it } from 'vitest'
import type { WorkflowDefinition } from '../api/types.ts'
import { parseRoles, removeState, renameState, rowErrors, toDefinition, unreachableStates } from './workflow.ts'

const flow = (): WorkflowDefinition => ({
  docType: 'Expense', stateField: 'workflow_state', active: true,
  states: [
    { name: 'Draft', color: 'gray', docStatus: 0, editRoles: [] },
    { name: 'Pending', color: 'orange', docStatus: 0, editRoles: [] },
    { name: 'Approved', color: 'green', docStatus: 1, editRoles: [] },
  ],
  transitions: [
    { from: 'Draft', action: 'Request', to: 'Pending', roles: ['Clerk'], condition: '  ', allowSelfApproval: true },
    { from: 'Pending', action: 'Approve', to: 'Approved', roles: ['Boss'], condition: ' amount < 500 ', allowSelfApproval: false },
  ],
})

describe('workflow helpers', () => {
  it('parses roles without blanks or repeats', () => {
    expect(parseRoles(' Clerk, Boss ,,Clerk')).toEqual(['Clerk', 'Boss'])
    expect(parseRoles('')).toEqual([])
  })

  it('trims conditions and drops blank ones', () => {
    const out = toDefinition(flow())
    expect(out.transitions.map((t) => t.condition)).toEqual([null, 'amount < 500'])
  })

  it('picks the errors of one row', () => {
    const errors = { 'states[1].name': 'bad', 'states[10].name': 'other', 'transitions[0]': 'row', 'transitions[0].roles': 'roles' }
    expect(rowErrors(errors, 'states', 1)).toEqual({ name: 'bad' })
    expect(rowErrors(errors, 'transitions', 0)).toEqual({ _: 'row', roles: 'roles' })
  })

  it('finds states nothing leads to', () => {
    const w = flow()
    expect(unreachableStates(w)).toEqual([])
    expect(unreachableStates({ ...w, transitions: [] })).toEqual(['Pending', 'Approved'])
  })

  it('carries transitions along when a state is renamed or removed', () => {
    const renamed = renameState(flow(), 1, 'Waiting')
    expect(renamed.transitions.map((t) => [t.from, t.to])).toEqual([['Draft', 'Waiting'], ['Waiting', 'Approved']])
    const removed = removeState(flow(), 1)
    expect(removed.states.map((s) => s.name)).toEqual(['Draft', 'Approved'])
    expect(removed.transitions).toEqual([])
  })
})
