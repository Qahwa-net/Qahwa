import { describe, expect, it } from 'vitest'
import { ago, describeAgent } from './sessions.ts'

describe('session helpers', () => {
  it('describes browsers in words and shows nothing risky', () => {
    expect(describeAgent('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36')).toBe('Chrome on Windows')
    expect(describeAgent('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Gecko/20100101 Firefox/121.0')).toBe('Firefox on macOS')
    expect(describeAgent('Mozilla/5.0 (Windows NT 10.0) Chrome/120.0 Safari/537.36 Edg/120.0')).toBe('Edge on Windows')
    expect(describeAgent(null)).toBe('A browser')
    expect(describeAgent('curl/8.0')).toBe('curl/8.0')
    expect(describeAgent('x'.repeat(200)).length).toBe(40)
  })

  it('says how long ago', () => {
    const now = new Date('2026-03-01T12:00:00Z')
    expect(ago('2026-03-01T11:59:40Z', now)).toBe('just now')
    expect(ago('2026-03-01T11:55:00Z', now)).toBe('5 minutes ago')
    expect(ago('2026-03-01T11:00:00Z', now)).toBe('1 hour ago')
    expect(ago('2026-02-27T12:00:00Z', now)).toBe('2 days ago')
    expect(ago('2026-03-02T12:00:00Z', now)).toBe('just now') // clocks that disagree never give a negative time
  })
})
