/** Layout for a Gantt chart: each document is a bar from its start date to its end date. Pure functions, no drawing. */

export interface GanttInput {
  name: string
  title: string
  start: unknown
  end: unknown
}

export interface GanttBar {
  name: string
  title: string
  startDay: number
  endDay: number
  /** Percentages of the chart's width. */
  left: number
  width: number
  days: number
}

export interface GanttTick {
  label: string
  left: number
}

export interface GanttLayout {
  bars: GanttBar[]
  /** Documents left out: no start or end date, or an end before the start. */
  skipped: number
  from: number
  to: number
  ticks: GanttTick[]
  /** Where "today" falls (percent), or null when it is outside the chart. */
  today: number | null
}

const DAY = 86_400_000

/** A date (or the date part of a date and time) as whole days since 1970-01-01, or null when it is not one. */
export function dayNumber(value: unknown): number | null {
  const match = typeof value === 'string' ? /^(\d{4})-(\d{2})-(\d{2})/.exec(value) : null
  if (!match) return null
  const time = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return Number.isNaN(time) ? null : Math.round(time / DAY)
}

export const isoOf = (day: number): string => new Date(day * DAY).toISOString().slice(0, 10)

/** Steps between ticks that keep the axis readable: a day, a week, two weeks, a month, a quarter or a year. */
function tickStep(span: number): number {
  for (const step of [1, 7, 14, 30, 91, 365]) if (span / step <= 12) return step
  return 365
}

export function layout(items: GanttInput[], todayDay: number, maxBars = 500): GanttLayout {
  const valid = items.flatMap((item) => {
    const startDay = dayNumber(item.start)
    const endDay = dayNumber(item.end)
    return startDay !== null && endDay !== null && endDay >= startDay ? [{ item, startDay, endDay }] : []
  }).slice(0, maxBars)
  const skipped = items.length - valid.length

  if (valid.length === 0) return { bars: [], skipped, from: todayDay, to: todayDay + 1, ticks: [], today: null }

  const from = Math.min(...valid.map((v) => v.startDay))
  const to = Math.max(...valid.map((v) => v.endDay)) + 1 // the last day is part of the bar
  const span = Math.max(to - from, 1)
  const pct = (day: number) => ((day - from) / span) * 100

  const bars = valid
    .sort((a, b) => a.startDay - b.startDay || a.endDay - b.endDay || a.item.name.localeCompare(b.item.name))
    .map(({ item, startDay, endDay }) => ({
      name: item.name, title: item.title || item.name, startDay, endDay, left: pct(startDay), width: Math.max(pct(endDay + 1) - pct(startDay), 0.5), days: endDay - startDay + 1,
    }))

  const step = tickStep(span)
  const ticks: GanttTick[] = []
  for (let day = from; day < to; day += step) ticks.push({ label: isoOf(day), left: pct(day) })

  return { bars, skipped, from, to, ticks, today: todayDay >= from && todayDay <= to ? pct(todayDay) : null }
}
