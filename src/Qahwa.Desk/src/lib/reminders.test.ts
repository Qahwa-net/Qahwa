import { describe, expect, it } from 'vitest'
import { PRESETS, toLocalInput, toRemindAt } from './reminders.ts'

describe('reminder times', () => {
  it('turns what a person typed into the instant it means', () => {
    const local = '2026-12-01T09:00'
    expect(new Date(toRemindAt(local)!).getTime()).toBe(new Date(local).getTime())
    expect(toRemindAt('not a time')).toBeNull()
  })

  it('writes a moment the way a datetime-local box wants it, in local time', () => {
    const moment = new Date(2026, 2, 5, 7, 4)
    expect(toLocalInput(moment)).toBe('2026-03-05T07:04')
  })

  const at = (label: string, from: Date) => PRESETS.find((p) => p.label === label)!.at(from)

  it('offers an hour on, this evening (or an hour on when it is already past), tomorrow morning and next Monday', () => {
    const noon = new Date(2026, 2, 5, 12, 0) // a Thursday
    expect(at('In an hour', noon).getHours()).toBe(13)
    expect(at('This evening', noon).getHours()).toBe(18)
    expect(at('This evening', new Date(2026, 2, 5, 19, 30)).getTime()).toBe(new Date(2026, 2, 5, 20, 30).getTime())
    expect(toLocalInput(at('Tomorrow morning', noon))).toBe('2026-03-06T09:00')
    expect(toLocalInput(at('Next Monday', noon))).toBe('2026-03-09T09:00')
  })

  it('never makes next Monday today when today is Monday', () => {
    expect(toLocalInput(at('Next Monday', new Date(2026, 2, 9, 8, 0)))).toBe('2026-03-16T09:00')
  })
})
