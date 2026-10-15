import { describe, expect, it } from 'vitest'
import { describeRepeat, needsAttention, tomorrow } from './autoRepeat.ts'

describe('auto repeat helpers', () => {
  it('finds tomorrow in UTC, across month and year ends', () => {
    expect(tomorrow(1, new Date('2026-03-05T23:30:00Z'))).toBe('2026-03-06')
    expect(tomorrow(1, new Date('2026-12-31T00:00:00Z'))).toBe('2027-01-01')
    expect(tomorrow(0, new Date('2026-02-28T12:00:00Z'))).toBe('2026-02-28')
    expect(tomorrow(1, new Date('2028-02-28T12:00:00Z'))).toBe('2028-02-29')
  })

  it('describes a repeat in words', () => {
    expect(describeRepeat({ frequency: 'Monthly', startDate: '2026-02-01' })).toBe('Every month from 2026-02-01')
    expect(describeRepeat({ frequency: 'Quarterly', startDate: '2026-02-01', endDate: '2027-02-01' })).toBe('Every 3 months from 2026-02-01 until 2027-02-01')
  })

  it('flags repeats that need a person', () => {
    expect(needsAttention({ status: 'Active', lastError: null })).toBe(false)
    expect(needsAttention({ status: 'Paused', lastError: null })).toBe(true)
    expect(needsAttention({ status: 'Active', lastError: 'Boom' })).toBe(true)
  })
})
