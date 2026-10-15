import type { AutoRepeatInfo, RepeatFrequency } from '../api/types.ts'

export const FREQUENCIES: RepeatFrequency[] = ['Daily', 'Weekly', 'Monthly', 'Quarterly', 'Yearly']

const WORDS: Record<RepeatFrequency, string> = {
  Daily: 'every day',
  Weekly: 'every week',
  Monthly: 'every month',
  Quarterly: 'every 3 months',
  Yearly: 'every year',
}

/** "2026-03-05" for the day after <paramref name="from"/> (or <paramref name="days"/> days after it), in UTC like the server. */
export function tomorrow(days = 1, from: Date = new Date()): string {
  const d = new Date(Date.UTC(from.getUTCFullYear(), from.getUTCMonth(), from.getUTCDate() + days))
  return d.toISOString().slice(0, 10)
}

/** "Every month from 2026-02-01 until 2026-12-01". */
export function describeRepeat(r: Pick<AutoRepeatInfo, 'frequency' | 'startDate' | 'endDate'>): string {
  const base = `${WORDS[r.frequency] ?? r.frequency} from ${r.startDate}`
  const text = r.endDate ? `${base} until ${r.endDate}` : base
  return text.charAt(0).toUpperCase() + text.slice(1)
}

/** Which repeats need attention: a problem on the last run, or paused. */
export const needsAttention = (r: Pick<AutoRepeatInfo, 'lastError' | 'status'>) => Boolean(r.lastError) || r.status === 'Paused'
