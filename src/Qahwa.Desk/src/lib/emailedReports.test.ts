import { describe, expect, it } from 'vitest'
import { describeSchedule, parseRecipients, problemWith } from './emailedReports.ts'

describe('emailed reports', () => {
  it('reads addresses from lines, commas and semicolons without repeats', () => {
    expect(parseRecipients('a@x.com, b@x.com;\nA@X.com\r\n  c@x.com  ,,')).toEqual(['a@x.com', 'b@x.com', 'c@x.com'])
    expect(parseRecipients('  ')).toEqual([])
  })

  it('says what is wrong before anything is sent', () => {
    expect(problemWith('a@x.com', '', 'me@x.com', 50)).toMatch(/Choose a report/)
    expect(problemWith('', 'R1', 'me@x.com', 50)).toMatch(/at least one/)
    expect(problemWith('nope', 'R1', 'me@x.com', 50)).toMatch(/not an email/)
    expect(problemWith('a@x.com\nBcc: evil@x.com', 'R1', 'me@x.com', 50)).toMatch(/not an email/)
    expect(problemWith(Array.from({ length: 11 }, (_, i) => `u${i}@x.com`).join(','), 'R1', 'me@x.com', 50)).toMatch(/At most 10/)
    expect(problemWith('a@x.com', 'R1', ' ', 50)).toMatch(/permissions/)
    expect(problemWith('a@x.com', 'R1', 'me@x.com', 0)).toMatch(/1 and 200/)
    expect(problemWith('a@x.com, b@x.com', 'R1', 'me@x.com', 50)).toBeNull()
  })

  it('describes the schedule', () => {
    expect(describeSchedule('Daily', 8)).toBe('Every day at 08:00 UTC')
    expect(describeSchedule('Weekly', 13)).toBe('Every week at 13:00 UTC')
    expect(describeSchedule('Monthly', 0)).toBe('Every month at 00:00 UTC')
  })
})
