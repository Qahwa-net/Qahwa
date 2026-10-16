import { describe, expect, it } from 'vitest'
import { DEFAULT_DISPLAY, formatDate, formatDateTime, weekStartIndex } from './display.ts'

const settings = (over: Partial<typeof DEFAULT_DISPLAY>) => ({ ...DEFAULT_DISPLAY, ...over })

describe('display settings', () => {
  it('formats dates in the chosen order and leaves other text alone', () => {
    expect(formatDate('2026-03-04')).toBe('2026-03-04')
    expect(formatDate('2026-03-04', settings({ dateFormat: 'dd/mm/yyyy' }))).toBe('04/03/2026')
    expect(formatDate('2026-03-04T10:00:00Z', settings({ dateFormat: 'mm/dd/yyyy' }))).toBe('03/04/2026')
    expect(formatDate('soon')).toBe('soon')
    expect(formatDate(null)).toBe('')
  })

  it('shows a moment in the chosen time zone', () => {
    expect(formatDateTime('2026-03-04T22:30:00Z', settings({ timeZone: 'Asia/Riyadh', dateFormat: 'dd/mm/yyyy' }))).toBe('05/03/2026 01:30')
    expect(formatDateTime('2026-03-04T22:30:00Z', settings({ timeZone: 'UTC' }))).toBe('2026-03-04 22:30')
    expect(formatDateTime('nonsense')).toBe('nonsense')
    expect(formatDateTime('2026-03-04T22:30:00Z', settings({ timeZone: 'Not/AZone' }))).not.toBe('')
  })

  it('knows the first weekday', () => {
    expect(weekStartIndex(settings({ firstDayOfWeek: 'Monday' }))).toBe(1)
    expect(weekStartIndex(settings({ firstDayOfWeek: 'Sunday' }))).toBe(0)
    expect(weekStartIndex(settings({ firstDayOfWeek: 'Saturday' }))).toBe(6)
    expect(weekStartIndex(settings({ firstDayOfWeek: 'Funday' }))).toBe(1)
  })
})
