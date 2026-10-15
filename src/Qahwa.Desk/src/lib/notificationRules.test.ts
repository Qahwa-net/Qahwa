import { describe, expect, it } from 'vitest'
import type { NotificationRule } from '../api/types.ts'
import { isTimeBased, newRule, toSet, unknownPlaceholders, withPlaceholder } from './notificationRules.ts'

const rule = (patch: Partial<NotificationRule> = {}): NotificationRule => ({ ...newRule([]), message: ' hi ', ...patch })

describe('notification rule helpers', () => {
  it('names new rules without clashing', () => {
    expect(newRule([]).name).toBe('Rule 1')
    expect(newRule([rule({ name: 'Rule 2' })]).name).toBe('Rule 3')
  })

  it('knows which events are driven by dates', () => {
    expect(isTimeBased('DaysBefore')).toBe(true)
    expect(isTimeBased('DaysAfter')).toBe(true)
    expect(isTimeBased('New')).toBe(false)
  })

  it('sends only the settings an event uses', () => {
    const out = toSet({ docType: 'T', rules: [
      rule({ event: 'ValueChange', field: ' status ', dateField: 'due', days: 3, condition: '  ' }),
      rule({ event: 'DaysBefore', field: 'status', dateField: 'due', days: 3, condition: ' x == 1 ' }),
      rule({ event: 'New', field: 'status', dateField: 'due', days: 3 }),
    ] }).rules
    expect(out[0]).toMatchObject({ field: 'status', dateField: null, days: null, condition: null, message: 'hi' })
    expect(out[1]).toMatchObject({ field: null, dateField: 'due', days: 3, condition: 'x == 1' })
    expect(out[2]).toMatchObject({ field: null, dateField: null, days: null })
  })

  it('adds placeholders with sensible spacing', () => {
    expect(withPlaceholder('', 'title')).toBe('{{title}}')
    expect(withPlaceholder('Hello', 'title')).toBe('Hello {{title}}')
    expect(withPlaceholder('Hello ', 'title')).toBe('Hello {{title}}')
  })

  it('finds placeholders that name no field', () => {
    expect(unknownPlaceholders('{{ title }} {{nope}} {{nope}} {{name}}', ['title', 'name'])).toEqual(['nope'])
    expect(unknownPlaceholders('no placeholders', [])).toEqual([])
  })
})
