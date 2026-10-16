import { describe, expect, it } from 'vitest'
import { restoreSummary } from './backup.ts'

const table = (docType: string, inserted: number, existing: number, failed: number) => ({ docType, inserted, existing, failed, firstProblem: null })

describe('restoreSummary', () => {
  it('adds the tables up and says whether it is a preview', () => {
    expect(restoreSummary({ dryRun: true, definitions: [], seriesRaised: 0, tables: [table('A', 2, 1, 0), table('B', 1, 0, 0)] })).toBe('3 documents would be added, 1 already there')
    expect(restoreSummary({ dryRun: false, definitions: [], seriesRaised: 0, tables: [table('A', 1, 0, 2)] })).toBe('1 document added, 0 already there, 2 refused')
    expect(restoreSummary({ dryRun: false, definitions: [], seriesRaised: 0, tables: [] })).toBe('0 documents added, 0 already there')
  })
})
