import type { PreparedReportInfo, PreparedReportResult } from '../api/types.ts'
import { csvCell } from './tableCsv.ts'

/** True while the server is still working on it, so the list should be looked at again soon. */
export const isPending = (r: Pick<PreparedReportInfo, 'status'>) => r.status === 'Queued' || r.status === 'Running'

export const statusText = (r: PreparedReportInfo): string =>
  r.status === 'Queued' ? 'Waiting' : r.status === 'Running' ? 'Building…' : r.status === 'Failed' ? 'Failed' : r.truncated ? `${r.rows} rows (cut short)` : `${r.rows} rows`

/** The result as CSV text (a header of column names, then the rows), safe to open in a spreadsheet. */
export function resultToCsv(result: Pick<PreparedReportResult, 'columns' | 'rows'>): string {
  const lines = [result.columns.map((c) => csvCell(c)).join(',')]
  for (const row of result.rows)
    lines.push(result.columns.map((c) => csvCell(row[c] === null || row[c] === undefined ? '' : String(row[c]))).join(','))
  return lines.join('\r\n') + '\r\n'
}
