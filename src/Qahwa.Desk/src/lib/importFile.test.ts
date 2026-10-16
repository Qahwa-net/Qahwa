import { describe, expect, it } from 'vitest'
import { MAX_IMPORT_BYTES, MAX_XLSX_BYTES, checkImportFile, isSpreadsheet, summarise, toBase64 } from './importFile.ts'

describe('import file helpers', () => {
  it('accepts CSV and text files within the limit', () => {
    expect(checkImportFile({ name: 'people.csv', size: 100 })).toBeNull()
    expect(checkImportFile({ name: 'PEOPLE.CSV', size: 100 })).toBeNull()
    expect(checkImportFile({ name: 'export', size: 100, type: 'text/csv' })).toBeNull()
  })

  it('refuses other files and big ones', () => {
    expect(checkImportFile({ name: 'x.pdf', size: 100, type: 'application/pdf' })).toMatch(/CSV/)
    expect(checkImportFile({ name: 'x.xls', size: 100, type: 'application/vnd.ms-excel' })).toMatch(/.xlsx/)
    expect(checkImportFile({ name: 'x.csv', size: MAX_IMPORT_BYTES + 1 })).toMatch(/larger/)
  })

  it('accepts Excel files up to their own limit', () => {
    expect(checkImportFile({ name: 'people.xlsx', size: MAX_IMPORT_BYTES + 1 })).toBeNull()
    expect(checkImportFile({ name: 'PEOPLE.XLSX', size: 100 })).toBeNull()
    expect(checkImportFile({ name: 'people', size: 100, type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })).toBeNull()
    expect(checkImportFile({ name: 'big.xlsx', size: MAX_XLSX_BYTES + 1 })).toMatch(/larger/)
    expect(isSpreadsheet({ name: 'a.csv' })).toBe(false)
  })

  it('turns bytes into base64, also for large files', () => {
    expect(toBase64(new Uint8Array([72, 105]))).toBe('SGk=')
    expect(toBase64(new Uint8Array(200_000).fill(65)).length).toBe(Math.ceil(200_000 / 3) * 4)
  })

  it('summarises what happened', () => {
    expect(summarise({ created: 3, updated: 0, failed: 1 })).toBe('3 created, 1 failed')
    expect(summarise({ created: 0, updated: 2, failed: 0 })).toBe('2 updated')
    expect(summarise({ created: 0, updated: 0, failed: 0 })).toBe('Nothing was imported')
  })
})
