import { describe, expect, it } from 'vitest'
import { dayNumber, isoOf, layout } from './gantt.ts'
import { gridStep, layoutMap } from './map.ts'

describe('gantt layout', () => {
  const today = dayNumber('2026-03-10')!

  it('reads dates and date-times', () => {
    expect(isoOf(dayNumber('2026-03-04')!)).toBe('2026-03-04')
    expect(dayNumber('2026-03-04T23:59:00Z')).toBe(dayNumber('2026-03-04'))
    expect(dayNumber('soon')).toBeNull()
    expect(dayNumber(null)).toBeNull()
    expect(dayNumber('2026-13-45')).toBe(dayNumber('2027-02-14')) // JS rolls over: the server never stores such dates
  })

  it('puts each bar between its dates as percentages of the chart, counting the last day', () => {
    const result = layout([
      { name: 'a', title: 'A', start: '2026-03-01', end: '2026-03-10' },
      { name: 'b', title: 'B', start: '2026-03-06', end: '2026-03-10' },
    ], today)

    expect(result.from).toBe(dayNumber('2026-03-01'))
    expect(result.to).toBe(dayNumber('2026-03-11'))
    expect(result.bars[0]).toMatchObject({ name: 'a', left: 0, width: 100, days: 10 })
    expect(result.bars[1]).toMatchObject({ name: 'b', left: 50, width: 50, days: 5 })
    expect(result.today).toBeCloseTo(90, 5)
  })

  it('leaves out documents without usable dates and says how many', () => {
    const result = layout([
      { name: 'a', title: 'A', start: '2026-03-01', end: '2026-03-02' },
      { name: 'no-end', title: '', start: '2026-03-01', end: null },
      { name: 'backwards', title: '', start: '2026-03-05', end: '2026-03-01' },
      { name: 'junk', title: '', start: 'x', end: 'y' },
    ], today)

    expect(result.bars.map((b) => b.name)).toEqual(['a'])
    expect(result.skipped).toBe(3)
    expect(layout([], today)).toMatchObject({ bars: [], skipped: 0, ticks: [], today: null })
  })

  it('orders bars by start, keeps tiny ones visible, shows no today marker outside the chart and limits the ticks', () => {
    const result = layout([
      { name: 'late', title: 'L', start: '2026-12-01', end: '2026-12-01' },
      { name: 'early', title: 'E', start: '2026-01-01', end: '2026-01-02' },
    ], dayNumber('2025-06-01')!)

    expect(result.bars.map((b) => b.name)).toEqual(['early', 'late'])
    expect(result.bars[1]!.width).toBeGreaterThanOrEqual(0.5)
    expect(result.today).toBeNull()
    expect(result.ticks.length).toBeLessThanOrEqual(13)
    expect(result.ticks[0]!.label).toBe('2026-01-01')
  })

  it('limits the number of bars', () => {
    const many = Array.from({ length: 700 }, (_, i) => ({ name: `n${i}`, title: '', start: '2026-03-01', end: '2026-03-02' }))
    expect(layout(many, today).bars).toHaveLength(500)
  })
})

describe('map layout', () => {
  it('fits places on one scale with a margin and flips north up', () => {
    const result = layoutMap([
      { name: 'north', title: 'N', place: '{"lat":25,"lng":46}' },
      { name: 'south', title: 'S', place: '{"lat":24,"lng":47}' },
      { name: 'nowhere', title: 'X', place: null },
      { name: 'bad', title: 'B', place: '{"lat":999,"lng":0}' },
    ])

    expect(result.skipped).toBe(2)
    const [north, south] = [result.points.find((p) => p.name === 'north')!, result.points.find((p) => p.name === 'south')!]
    expect(north.y).toBeLessThan(south.y) // further north is higher up
    expect(north.x).toBeLessThan(south.x) // further east is further right
    for (const p of result.points) { expect(p.x).toBeGreaterThan(0); expect(p.x).toBeLessThan(100); expect(p.y).toBeGreaterThan(0); expect(p.y).toBeLessThan(100) }
    expect(result.grid.length).toBeGreaterThan(2)
  })

  it('gives a single place a sensible view in the middle', () => {
    const result = layoutMap([{ name: 'only', title: '', place: '{"lat":24.7136,"lng":46.6753}' }])
    expect(result.points[0]!.title).toBe('only')
    expect(result.points[0]!.x).toBeCloseTo(50, 5)
    expect(result.points[0]!.y).toBeCloseTo(50, 5)
    expect(layoutMap([]).bounds).toBeNull()
  })

  it('chooses round grid steps', () => {
    expect(gridStep(40)).toBe(10)
    expect(gridStep(1)).toBeCloseTo(0.5, 5)
    expect(gridStep(0.04)).toBeCloseTo(0.01, 5)
  })
})
