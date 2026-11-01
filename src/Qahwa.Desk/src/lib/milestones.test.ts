import { describe, expect, it } from 'vitest'
import { durationText } from './milestones.ts'

describe('durationText', () => {
  it('uses the largest whole unit', () => {
    expect(durationText(30)).toBe('under a minute')
    expect(durationText(60)).toBe('1 minute')
    expect(durationText(12 * 60 + 59)).toBe('12 minutes')
    expect(durationText(3600)).toBe('1 hour')
    expect(durationText(5 * 3600 + 1800)).toBe('5 hours')
    expect(durationText(86400)).toBe('1 day')
    expect(durationText(3 * 86400 + 5000)).toBe('3 days')
  })
})
