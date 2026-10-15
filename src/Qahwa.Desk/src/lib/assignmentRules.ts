import type { AssignmentRule, AssignmentRuleSet, AssignmentStrategy } from '../api/types.ts'

export const STRATEGIES: { value: AssignmentStrategy; label: string; help: string }[] = [
  { value: 'RoundRobin', label: 'Round robin', help: 'People take turns: the one who was given something longest ago is next.' },
  { value: 'LoadBalancing', label: 'Load balancing', help: 'The person with the fewest open assignments gets it.' },
  { value: 'Field', label: 'From a field', help: 'The person named in one of the document’s fields gets it.' },
]

export const newRule = (existing: AssignmentRule[]): AssignmentRule => {
  let n = existing.length + 1
  while (existing.some((r) => r.name === `Rule ${n}`)) n++
  return { name: `Rule ${n}`, active: true, strategy: 'RoundRobin', users: [], userField: null, condition: '', closeCondition: '', description: '', dueInDays: null, priority: 'Medium' }
}

/** What to send: blank texts become null and each strategy keeps only what it uses. */
export function toSet(draft: AssignmentRuleSet): AssignmentRuleSet {
  const blank = (text?: string | null) => (text && text.trim() ? text.trim() : null)
  return {
    ...draft,
    rules: draft.rules.map((r) => ({
      ...r,
      users: r.strategy === 'Field' ? [] : r.users,
      userField: r.strategy === 'Field' ? blank(r.userField) : null,
      condition: blank(r.condition),
      closeCondition: blank(r.closeCondition),
      description: blank(r.description),
    })),
  }
}

/** Rules are tried in order, so moving one up or down changes which one wins. */
export function moveRule<T>(list: T[], index: number, by: -1 | 1): T[] {
  const target = index + by
  if (target < 0 || target >= list.length) return list
  const next = [...list]
  ;[next[index], next[target]] = [next[target]!, next[index]!]
  return next
}

/** Adds a person to a rule's list unless already there; returns the same array when nothing changes. */
export function addUser(users: string[], user: string): string[] {
  const value = user.trim()
  return !value || users.includes(value) ? users : [...users, value]
}
