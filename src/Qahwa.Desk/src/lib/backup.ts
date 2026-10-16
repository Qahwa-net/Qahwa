import type { RestoreReport } from '../api/types.ts'

/** One line saying what a restore did (or would do). */
export function restoreSummary(report: RestoreReport): string {
  const added = report.tables.reduce((n, t) => n + t.inserted, 0)
  const there = report.tables.reduce((n, t) => n + t.existing, 0)
  const refused = report.tables.reduce((n, t) => n + t.failed, 0)
  const verb = report.dryRun ? 'would be added' : 'added'
  const parts = [`${added} document${added === 1 ? '' : 's'} ${verb}`, `${there} already there`]
  if (refused > 0) parts.push(`${refused} refused`)
  return parts.join(', ')
}
