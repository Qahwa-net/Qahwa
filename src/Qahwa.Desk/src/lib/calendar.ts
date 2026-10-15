export interface Day {
  /** yyyy-MM-dd */
  date: string
  inMonth: boolean
}

const pad = (n: number) => String(n).padStart(2, '0')

export const iso = (year: number, month: number, day: number): string => {
  const d = new Date(Date.UTC(year, month - 1, day))
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`
}

/** The weeks of a month for a grid: always six rows of seven days, starting on <paramref name="weekStart"/> (0 = Sunday, 1 = Monday, 6 = Saturday). */
export function monthGrid(year: number, month: number, weekStart = 0): Day[][] {
  const first = new Date(Date.UTC(year, month - 1, 1))
  const offset = (first.getUTCDay() - weekStart + 7) % 7
  const weeks: Day[][] = []
  for (let w = 0; w < 6; w++) {
    const week: Day[] = []
    for (let d = 0; d < 7; d++) {
      const date = iso(year, month, 1 - offset + w * 7 + d)
      week.push({ date, inMonth: date.slice(0, 7) === `${year}-${pad(month)}` })
    }
    weeks.push(week)
  }

  return weeks
}

/** The first day of the month and of the next one: a documents-in-this-month filter is "on or after the first, before the next". */
export function monthRange(year: number, month: number): [string, string] {
  return [iso(year, month, 1), iso(year, month + 1, 1)]
}

export function addMonths(year: number, month: number, delta: number): { year: number; month: number } {
  const d = new Date(Date.UTC(year, month - 1 + delta, 1))
  return { year: d.getUTCFullYear(), month: d.getUTCMonth() + 1 }
}

/** "2026-03" to a year and month, or the given fallback (this month) when it is not one. */
export function parseMonth(text: string | null, fallback: { year: number; month: number }): { year: number; month: number } {
  const match = /^(\d{4})-(0[1-9]|1[0-2])$/.exec(text ?? '')
  return match ? { year: Number(match[1]), month: Number(match[2]) } : fallback
}

export const formatMonth = (year: number, month: number): string => `${year}-${pad(month)}`

/** The day a Date or Datetime value falls on (UTC, like the server), or null when there is none. */
export function dayOf(value: unknown): string | null {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}/.test(value) ? value.slice(0, 10) : null
}

/** Documents grouped by the day of one of their fields. */
export function eventsByDay<T extends Record<string, unknown>>(docs: T[], field: string): Record<string, T[]> {
  const byDay: Record<string, T[]> = {}
  for (const doc of docs) {
    const day = dayOf(doc[field])
    if (day) (byDay[day] ??= []).push(doc)
  }

  return byDay
}

export const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

export const MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
