import type { IndicatorColor, WorkflowDefinition, WorkflowState, WorkflowTransition, WorkflowView } from '../api/types.ts'

export const STATE_COLORS: IndicatorColor[] = ['gray', 'blue', 'green', 'orange', 'red', 'purple']
export const DOCSTATUS_LABELS = ['Draft', 'Submitted', 'Cancelled']

/** "Sales User, Sales Manager" → ['Sales User', 'Sales Manager'], without blanks or repeats. */
export function parseRoles(text: string): string[] {
  const seen = new Set<string>()
  for (const part of text.split(',')) {
    const role = part.trim()
    if (role) seen.add(role)
  }
  return [...seen]
}

export const newState = (name = ''): WorkflowState => ({ name, color: 'gray', docStatus: 0, editRoles: [] })

export const newTransition = (from = '', to = ''): WorkflowTransition => ({ from, action: '', to, roles: [], condition: '', allowSelfApproval: true, requiredApprovals: 1 })

/** What the builder edits: the saved workflow, or a starting point with the usual Draft state. */
export function draftFrom(view: WorkflowView): WorkflowDefinition {
  const w = view.workflow
  if (view.exists || w.states.length > 0) return structuredClone(w)
  return { ...structuredClone(w), active: true, states: [newState('Draft')], transitions: [] }
}

/** The workflow to send: blank conditions removed and nothing else changed. */
export function toDefinition(draft: WorkflowDefinition): WorkflowDefinition {
  return {
    ...draft,
    transitions: draft.transitions.map((t) => ({ ...t, condition: t.condition?.trim() ? t.condition.trim() : null })),
  }
}

/** Server errors are keyed like `states[1].name` or `transitions[0].roles`; pick those of one row. */
export function rowErrors(errors: Record<string, string>, list: string, index: number): Record<string, string> {
  const prefix = `${list}[${index}]`
  return Object.fromEntries(Object.entries(errors)
    .filter(([path]) => path === prefix || path.startsWith(`${prefix}.`))
    .map(([path, message]) => [path.slice(prefix.length + 1) || '_', message]))
}

/** States no transition leads to or away from (apart from the first state, where documents start). */
export function unreachableStates(draft: WorkflowDefinition): string[] {
  const used = new Set(draft.transitions.flatMap((t) => [t.from, t.to]))
  return draft.states.slice(1).map((s) => s.name).filter((name) => name && !used.has(name))
}

/** Renaming a state must carry its transitions along. */
export function renameState(draft: WorkflowDefinition, index: number, name: string): WorkflowDefinition {
  const old = draft.states[index]?.name
  return {
    ...draft,
    states: draft.states.map((s, i) => (i === index ? { ...s, name } : s)),
    transitions: draft.transitions.map((t) => ({ ...t, from: t.from === old ? name : t.from, to: t.to === old ? name : t.to })),
  }
}

/** Removing a state removes the transitions that used it. */
export function removeState(draft: WorkflowDefinition, index: number): WorkflowDefinition {
  const old = draft.states[index]?.name
  return {
    ...draft,
    states: draft.states.filter((_, i) => i !== index),
    transitions: draft.transitions.filter((t) => t.from !== old && t.to !== old),
  }
}
