import { describe, expect, it } from 'vitest'
import { emptyDesk, moveId, ordered, sidebarGroups, type DeskLink } from './desk.ts'
const links: DeskLink[] = [
  { id: 'home', label: 'Home', path: '/', group: 'My work', defaultVisible: true },
  { id: 'a', label: 'A', path: '/app/A', group: 'Sales', defaultVisible: true },
  { id: 'b', label: 'B', path: '/app/B', group: 'Sales', defaultVisible: true },
  { id: 'report', label: 'Report', path: '/reports/1', group: 'Reports', defaultVisible: false },
]
describe('Desk navigation', () => {
  it('keeps standard navigation, excludes opt-in links and adds newly available items', () => {
    expect(sidebarGroups(links, emptyDesk()).map(([g, items]) => [g, items.map(i => i.id)])).toEqual([['My work', ['home']], ['Sales', ['a', 'b']]])
    expect(ordered(links, ['b', 'a'], l => l.id).map(l => l.id)).toEqual(['b', 'a', 'home', 'report'])
  })
  it('moves pins into ordered favorites without duplicating them and respects hidden items', () => {
    const layout = { ...emptyDesk(), pinned: ['report', 'a', 'b'], hidden: ['b'] }
    expect(sidebarGroups(links, layout).map(([g, items]) => [g, items.map(i => i.id)])).toEqual([['Favorites', ['report', 'a']], ['My work', ['home']]])
  })
  it('applies group and link ordering without mutating input', () => {
    expect(sidebarGroups(links, { ...emptyDesk(), groupOrder: ['Sales'], order: ['b', 'a'] })[0]).toEqual(['Sales', [links[2], links[1]]])
    const ids = ['a', 'b', 'c']; expect(moveId(ids, 'b', -1)).toEqual(['b', 'a', 'c']); expect(ids).toEqual(['a', 'b', 'c'])
    expect(moveId(ids, 'a', -1)).toEqual(ids); expect(moveId(ids, 'missing', 1)).toEqual(ids)
  })
})
