import { describe, expect, it } from 'vitest'
import { isPending, resultToCsv, statusText } from './preparedReports.ts'
import type { PreparedReportInfo } from '../api/types.ts'

const info = (patch: Partial<PreparedReportInfo>): PreparedReportInfo =>
  ({ id: 'a', report: 'r', status: 'Done', rows: 3, truncated: false, requested: '2026-01-01T00:00:00Z', ...patch })

describe('prepared reports', () => {
  it('is pending while waiting or building', () => {
    expect(['Queued', 'Running'].map((status) => isPending(info({ status: status as PreparedReportInfo['status'] })))).toEqual([true, true])
    expect(isPending(info({ status: 'Done' }))).toBe(false)
    expect(isPending(info({ status: 'Failed' }))).toBe(false)
  })

  it('says what state it is in', () => {
    expect(statusText(info({ status: 'Queued' }))).toBe('Waiting')
    expect(statusText(info({ status: 'Running' }))).toBe('Building…')
    expect(statusText(info({ status: 'Failed' }))).toBe('Failed')
    expect(statusText(info({}))).toBe('3 rows')
    expect(statusText(info({ truncated: true }))).toBe('3 rows (cut short)')
  })

  it('writes CSV with quoting and without letting a cell run as a formula', () => {
    const csv = resultToCsv({ columns: ['Name', 'Note'], rows: [{ Name: 'Acme, Inc', Note: '=1+1' }, { Name: 'Globex', Note: null }] })
    expect(csv).toBe('Name,Note\r\n"Acme, Inc",\'=1+1\r\nGlobex,\r\n')
  })
})
