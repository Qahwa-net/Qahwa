import { describe, expect, it } from 'vitest'
import { agoText, paceText } from './scheduler.ts'

describe('paceText', () => {
  it('names the interval in the largest whole unit', () => {
    expect([10, 60, 300, 3600, 7200, 90].map(paceText)).toEqual(['10 seconds', 'minute', '5 minutes', 'hour', '2 hours', '90 seconds'])
  })
})

describe('agoText', () => {
  const now = Date.parse('2026-11-04T12:00:00Z')
  it('says how long ago in the largest unit', () => {
    const at = (secondsAgo: number) => new Date(now - secondsAgo * 1000).toISOString()
    expect([5, 60, 300, 7200, 172800].map(s => agoText(at(s), now))).toEqual(['just now', '1 minute ago', '5 minutes ago', '2 hours ago', '2 days ago'])
  })
})
