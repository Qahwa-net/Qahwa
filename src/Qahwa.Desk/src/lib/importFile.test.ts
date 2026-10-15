import { describe, expect, it } from 'vitest'
import { MAX_IMPORT_BYTES, checkImportFile, summarise } from './importFile.ts'

describe('import file helpers', () => {
  it('accepts CSV and text files within the limit', () => {
    expect(checkImportFile({ name: 'people.csv', size: 100 })).toBeNull()
    expect(checkImportFile({ name: 'PEOPLE.CSV', size: 100 })).toBeNull()
    expect(checkImportFile({ name: 'export', size: 100, type: 'text/csv' })).toBeNull()
  })

  it('refuses other files and big ones', () => {
    expect(checkImportFile({ name: 'x.xlsx', size: 100, type: 'application/vnd.ms-excel' })).toMatch(/CSV/)
    expect(checkImportFile({ name: 'x.csv', size: MAX_IMPORT_BYTES + 1 })).toMatch(/larger/)
  })

  it('summarises what happened', () => {
    expect(summarise({ created: 3, updated: 0, failed: 1 })).toBe('3 created, 1 failed')
    expect(summarise({ created: 0, updated: 2, failed: 0 })).toBe('2 updated')
    expect(summarise({ created: 0, updated: 0, failed: 0 })).toBe('Nothing was imported')
  })
})
