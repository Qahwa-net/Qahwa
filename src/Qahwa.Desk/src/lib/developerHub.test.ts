import { describe, expect, it } from 'vitest'
import { HUB, filterHub } from './developerHub.ts'

describe('developerHub', () => {
  it('returns everything for an empty query', () => expect(filterHub(HUB, '  ')).toBe(HUB))
  it('matches title, description and keywords, all words required', () => {
    expect(filterHub(HUB, 'cron').flatMap((g) => g.tools.map((t) => t.title))).toEqual(['Scheduled Jobs'])
    expect(filterHub(HUB, 'webhook signed').flatMap((g) => g.tools.map((t) => t.title))).toEqual(['Webhooks'])
    expect(filterHub(HUB, 'zzzz')).toEqual([])
  })
  it('has no duplicate addresses', () => {
    const all = HUB.flatMap((g) => g.tools.map((t) => t.to))
    expect(new Set(all).size).toBe(all.length)
  })
})
