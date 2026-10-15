import { describe, expect, it } from 'vitest'
import type { DocField } from '../api/types.ts'
import { NO_VALUE, boardFields, choicesOf, columnsOf, groupCards, withMoved } from './board.ts'
import { addMonths, dayOf, eventsByDay, formatMonth, iso, monthGrid, monthRange, parseMonth } from './calendar.ts'

const select = (name: string, options: string): DocField => ({ name, type: 'Select', options }) as DocField

describe('board helpers', () => {
  const status = select('status', 'Open\nWorking\n\nDone')

  it('reads the choices in order', () => {
    expect(choicesOf(status)).toEqual(['Open', 'Working', 'Done'])
    expect(choicesOf(select('x', ''))).toEqual([])
  })

  it('makes a column for each choice, an old value found in the data, and a last column for no value', () => {
    const docs = [{ name: 'a', status: 'Open' }, { name: 'b', status: 'Legacy' }, { name: 'c', status: null }]
    expect(columnsOf(status, docs)).toEqual(['Open', 'Working', 'Done', 'Legacy', NO_VALUE])
    expect(columnsOf(status, [])).toEqual(['Open', 'Working', 'Done', NO_VALUE])
  })

  it('groups cards by value and keeps empty columns', () => {
    const docs = [{ name: 'a', status: 'Open' }, { name: 'b', status: 'Done' }, { name: 'c', status: 'Open' }, { name: 'd' }]
    const groups = groupCards(docs, 'status', columnsOf(status, docs))
    expect(groups.Open!.map((d) => d.name)).toEqual(['a', 'c'])
    expect(groups.Working).toEqual([])
    expect(groups[NO_VALUE]!.map((d) => d.name)).toEqual(['d'])
  })

  it('moves one card without touching the others', () => {
    const docs = [{ name: 'a', status: 'Open' }, { name: 'b', status: 'Open' }]
    const moved = withMoved(docs, 'a', 'status', 'Done')
    expect(moved.map((d) => d.status)).toEqual(['Done', 'Open'])
    expect(withMoved(docs, 'a', 'status', NO_VALUE)[0]!.status).toBeNull()
    expect(docs[0]!.status).toBe('Open')
  })

  it('offers only Select fields that have choices', () => {
    const fields = [status, select('empty', ''), { name: 'n', type: 'Data' } as DocField, { ...select('hid', 'A'), hidden: true }]
    expect(boardFields(fields).map((f) => f.name)).toEqual(['status'])
  })
})

describe('calendar helpers', () => {
  it('builds six weeks of seven days that start on the chosen weekday', () => {
    const grid = monthGrid(2026, 3, 0) // March 2026 starts on a Sunday
    expect(grid).toHaveLength(6)
    expect(grid.every((w) => w.length === 7)).toBe(true)
    expect(grid[0]![0]).toEqual({ date: '2026-03-01', inMonth: true })
    expect(grid[5]![6]!.date).toBe('2026-04-11')
    expect(monthGrid(2026, 3, 1)[0]![0]!.date).toBe('2026-02-23') // weeks starting Monday
    expect(monthGrid(2026, 3, 1)[0]![0]!.inMonth).toBe(false)
  })

  it('handles year ends and leap years', () => {
    expect(monthGrid(2026, 1, 0)[0]![0]!.date).toBe('2025-12-28')
    expect(monthRange(2026, 12)).toEqual(['2026-12-01', '2027-01-01'])
    expect(monthRange(2028, 2)).toEqual(['2028-02-01', '2028-03-01'])
    expect(iso(2026, 3, 0)).toBe('2026-02-28')
    expect(addMonths(2026, 1, -1)).toEqual({ year: 2025, month: 12 })
    expect(addMonths(2026, 12, 1)).toEqual({ year: 2027, month: 1 })
  })

  it('reads and writes months', () => {
    expect(parseMonth('2026-07', { year: 2000, month: 1 })).toEqual({ year: 2026, month: 7 })
    expect(parseMonth('2026-13', { year: 2000, month: 1 })).toEqual({ year: 2000, month: 1 })
    expect(parseMonth(null, { year: 2000, month: 1 })).toEqual({ year: 2000, month: 1 })
    expect(formatMonth(2026, 3)).toBe('2026-03')
  })

  it('finds the day of dates and datetimes and groups documents by it', () => {
    expect(dayOf('2026-03-04')).toBe('2026-03-04')
    expect(dayOf('2026-03-04T23:59:00Z')).toBe('2026-03-04')
    expect(dayOf(null)).toBeNull()
    expect(dayOf('soon')).toBeNull()
    const byDay = eventsByDay([{ name: 'a', due: '2026-03-04' }, { name: 'b', due: '2026-03-04T10:00:00Z' }, { name: 'c', due: null }], 'due')
    expect(Object.keys(byDay)).toEqual(['2026-03-04'])
    expect(byDay['2026-03-04']!.map((d) => d.name)).toEqual(['a', 'b'])
  })
})
