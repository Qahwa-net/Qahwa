import { describe, expect, it } from 'vitest'
import { DEFAULT_DISPLAY, formatDate, formatDateTime, formatNumber, weekStartIndex } from './display.ts'

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

describe('formatNumber', () => {
  it('uses the chosen separators', () => {
    expect(formatNumber(1234567.891, settings({ numberFormat: '1,234.56' }))).toBe('1,234,567.891')
    expect(formatNumber(1234567.891, settings({ numberFormat: '1.234,56' }))).toBe('1.234.567,891')
    expect(formatNumber(-1234.5, settings({ numberFormat: '1 234,56' }))).toBe('-1 234,5')
    expect(formatNumber(12, settings({}))).toBe('12')
    expect(formatNumber(0.000001, settings({ numberFormat: '1.234,56' }))).toBe('0,000001')
  })
  it('leaves what is not a number alone', () => {
    expect(formatNumber('abc', settings({}))).toBe('abc')
    expect(formatNumber(Number.NaN, settings({}))).toBe('NaN')
  })
})

describe('formatNumber with a fixed number of decimals', () => {
  it('always shows that many', () => {
    expect(formatNumber(1234.5, settings({ numberFormat: '1,234.56' }), 2)).toBe('1,234.50')
    expect(formatNumber(2, settings({ numberFormat: '1.234,56' }), 3)).toBe('2,000')
    expect(formatNumber(1234.567, settings({ numberFormat: '1,234.56' }), 0)).toBe('1,235')
  })
})
