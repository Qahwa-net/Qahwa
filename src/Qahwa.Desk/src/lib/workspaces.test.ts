import { describe, expect, it } from 'vitest'
import { PAGES, moveBlock, newBlock, toRequest } from './workspaces.ts'

describe('workspace helpers', () => {
  it('starts each block type with what it needs', () => {
    expect(newBlock('Heading')).toMatchObject({ type: 'Heading', text: '' })
    expect(newBlock('Dashboard')).toMatchObject({ type: 'Dashboard', dashboardId: '' })
    expect(newBlock('Shortcuts').items).toHaveLength(1)
  })

  it('moves blocks within bounds', () => {
    expect(moveBlock(['a', 'b', 'c'], 1, -1)).toEqual(['b', 'a', 'c'])
    expect(moveBlock(['a', 'b'], 1, 1)).toEqual(['a', 'b'])
  })

  it('sends only what each block uses', () => {
    const request = toRequest({
      title: ' Home ', sequence: 3.6, roles: ['Clerk'],
      blocks: [
        { type: 'Heading', text: ' Hi ', items: [{ label: 'x', kind: 'Page', target: 'dashboards' }] },
        { type: 'Dashboard', dashboardId: '', items: [] },
        { type: 'Shortcuts', items: [{ label: ' ', kind: 'DocType', target: 'Customer' }, { label: 'None', kind: 'Page', target: '' }] },
      ],
    })
    expect(request.title).toBe('Home')
    expect(request.sequence).toBe(4)
    expect(request.blocks[0]).toEqual({ type: 'Heading', text: 'Hi', items: [] })
    expect(request.blocks[1]).toEqual({ type: 'Dashboard', dashboardId: null, items: [] })
    expect(request.blocks[2]!.items).toEqual([{ label: 'Customer', kind: 'DocType', target: 'Customer' }])
  })

  it('lists pages with unique keys', () => {
    expect(new Set(PAGES.map((p) => p.value)).size).toBe(PAGES.length)
  })
})
