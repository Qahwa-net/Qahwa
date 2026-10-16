import { describe, expect, it } from 'vitest'
import { KINDS, exportUrl, readBundle, summary } from './bundle.ts'

describe('bundle helpers', () => {
  it('builds the download address with a name and only the chosen kinds', () => {
    expect(exportUrl('', KINDS.map((k) => k.value))).toBe('/api/bundle/export')
    expect(exportUrl('Shop & co', ['Workflow', 'DocType'])).toBe('/api/bundle/export?name=Shop+%26+co&kinds=Workflow%2CDocType')
  })

  it('accepts a bundle and says why a file is not one', () => {
    expect(readBundle('{"format":"qahwa-bundle","version":1,"items":[]}')).toHaveProperty('bundle')
    expect(readBundle('nope')).toEqual({ error: 'This file is not valid JSON.' })
    expect(readBundle('null')).toEqual({ error: 'This is not a Qahwa bundle file.' })
    expect(readBundle('{"format":"x","items":[]}')).toEqual({ error: 'This is not a Qahwa bundle file.' })
    expect(readBundle('{"format":"qahwa-bundle"}')).toEqual({ error: 'This is not a Qahwa bundle file.' })
  })

  it('summarises a report in words', () => {
    expect(summary({ dryRun: true, applied: 1, failed: 0, items: [] })).toBe('1 item can be applied')
    expect(summary({ dryRun: false, applied: 3, failed: 2, items: [] })).toBe('3 items applied, 2 refused')
  })
})
