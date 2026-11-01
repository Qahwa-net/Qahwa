/** "2026-12-01T09:00" as typed in a datetime-local box (the person's own clock) to the instant it means, with its offset, as the server wants it. */
export function toRemindAt(local: string): string | null {
  const when = new Date(local)
  return Number.isNaN(when.getTime()) ? null : when.toISOString()
}

/** The value of a datetime-local box for a moment: "2026-12-01T09:00", in the person's own time. */
export function toLocalInput(moment: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${moment.getFullYear()}-${pad(moment.getMonth() + 1)}-${pad(moment.getDate())}T${pad(moment.getHours())}:${pad(moment.getMinutes())}`
}

export interface ReminderPreset {
  label: string
  at: (from: Date) => Date
}

/** Quick choices: in an hour, this evening (18:00, or an hour on when it is past), tomorrow at 09:00, next Monday at 09:00. */
export const PRESETS: ReminderPreset[] = [
  { label: 'In an hour', at: (from) => new Date(from.getTime() + 3_600_000) },
  {
    label: 'This evening',
    at: (from) => {
      const evening = new Date(from)
      evening.setHours(18, 0, 0, 0)
      return evening.getTime() > from.getTime() ? evening : new Date(from.getTime() + 3_600_000)
    },
  },
  {
    label: 'Tomorrow morning',
    at: (from) => {
      const next = new Date(from)
      next.setDate(next.getDate() + 1)
      next.setHours(9, 0, 0, 0)
      return next
    },
  },
  {
    label: 'Next Monday',
    at: (from) => {
      const next = new Date(from)
      next.setDate(next.getDate() + (((8 - next.getDay()) % 7) || 7))
      next.setHours(9, 0, 0, 0)
      return next
    },
  },
]
