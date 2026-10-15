import { describe, expect, it } from 'vitest'
import { barHeights, formatNumber, niceMax, pieSlices, slicePath } from './charts.ts'

describe('chart maths', () => {
  it('rounds an axis top up to a round number', () => {
    expect(niceMax(7)).toBe(8)
    expect(niceMax(130)).toBe(150)
    expect(niceMax(1000)).toBe(1000)
    expect(niceMax(0)).toBe(1)
    expect(niceMax(-5)).toBe(1)
    expect(niceMax(0.07)).toBeCloseTo(0.08)
  })

  it('scales bars against the axis and never draws negative heights', () => {
    expect(barHeights([50, 100, 0, -20], 100)).toEqual([50, 100, 0, 0])
    expect(barHeights([75], 100)).toEqual([75 / 80 * 100])
    expect(barHeights([], 100)).toEqual([])
  })

  it('divides a pie by share', () => {
    const slices = pieSlices([1, 1, 2, 0, -3])
    expect(slices.map((s) => s.share)).toEqual([0.25, 0.25, 0.5, 0, 0])
    expect(slices[2]!.end).toBeCloseTo(Math.PI * 2)
    expect(pieSlices([0, 0]).every((s) => s.share === 0)).toBe(true)
  })

  it('draws a slice as a path, and a whole pie as a circle', () => {
    expect(slicePath(50, 50, 40, 0, Math.PI / 2)).toBe('M 50 50 L 50.00 10.00 A 40 40 0 0 1 90.00 50.00 Z')
    expect(slicePath(50, 50, 40, 0, Math.PI * 2)).toContain('A 40 40 0 1 1')
    expect(slicePath(50, 50, 40, 0, Math.PI * 1.5)).toContain(' 0 1 1 ')
  })

  it('formats numbers', () => {
    expect(formatNumber(1234567)).toBe('1,234,567')
    expect(formatNumber(1234.567)).toBe('1,234.57')
    expect(formatNumber(12345, true)).toBe('12.3K')
    expect(formatNumber(2500000, true)).toBe('2.5M')
    expect(formatNumber(999, true)).toBe('999')
  })
})
