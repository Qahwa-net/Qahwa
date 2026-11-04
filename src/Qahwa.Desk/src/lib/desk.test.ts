import { describe, expect, it } from 'vitest'
import { emptyDesk, groupFolded, groupRank, moveBefore, moveId, nestLinks, ordered, sidebarGroups, withGroupMoved, withLinkMoved, type DeskLink } from './desk.ts'
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

describe('Default sidebar arrangement', () => {
  const more: DeskLink[] = [
    ...links,
    { id: 't', label: 'Designer', path: '/designer', group: 'Tools', defaultVisible: true },
    { id: 'c', label: 'User', path: '/app/User', group: 'Core', defaultVisible: true },
    { id: 'w', label: 'Ops', path: '/workspace/ops', group: 'Workspaces', defaultVisible: true },
    { id: 'z', label: 'Z', path: '/app/Z', group: 'Accounting', defaultVisible: true },
  ]
  it('puts my work first, then workspaces, then business modules, then tools, and the system last', () => {
    expect(sidebarGroups(more, emptyDesk()).map(([g]) => g)).toEqual(['My work', 'Workspaces', 'Accounting', 'Sales', 'Tools', 'Core'])
    expect(['My work', 'Workspaces', 'Anything', 'Tools', 'Core'].map(groupRank)).toEqual([0, 1, 50, 98, 99])
  })
  it('starts Tools, Core and long groups folded, unless unfolded here or holding the current page', () => {
    const layout = emptyDesk()
    expect(groupFolded('Tools', 3, layout, new Set(), false)).toBe(true)
    expect(groupFolded('Core', 3, layout, new Set(), false)).toBe(true)
    expect(groupFolded('Sales', 13, layout, new Set(), false)).toBe(true)
    expect(groupFolded('Sales', 12, layout, new Set(), false)).toBe(false)
    expect(groupFolded('Tools', 3, layout, new Set(['Tools']), false)).toBe(false)
    expect(groupFolded('Core', 70, layout, new Set(), true)).toBe(false)
  })
  it('lets a saved choice win over every default', () => {
    const layout = { ...emptyDesk(), collapsedGroups: ['Sales', 'Core'] }
    expect(groupFolded('Sales', 2, layout, new Set(['Sales']), true)).toBe(true)
    expect(groupFolded('Core', 70, layout, new Set(['Core']), true)).toBe(true)
  })
})

describe('Dragging in the sidebar', () => {
  it('moves an item before another, to the end, and ignores nonsense', () => {
    expect(moveBefore(['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'a', 'b'])
    expect(moveBefore(['a', 'b', 'c'], 'a', null)).toEqual(['b', 'c', 'a'])
    const same = ['a', 'b']
    expect(moveBefore(same, 'a', 'a')).toBe(same)
    expect(moveBefore(same, 'x', 'a')).toBe(same)
    expect(moveBefore(same, 'a', 'x')).toBe(same)
  })
  it('writes the whole group order when a group is dragged', () => {
    const layout = emptyDesk()
    expect(withGroupMoved(layout, ['My work', 'Sales', 'Tools'], 'Tools', 'Sales').groupOrder).toEqual(['My work', 'Tools', 'Sales'])
    expect(withGroupMoved(layout, ['My work', 'Sales'], 'Sales', 'Sales')).toBe(layout)
  })
  it('keeps what is saved for other groups when a link is dragged, and orders favorites by their pins', () => {
    const layout = { ...emptyDesk(), order: ['z', 'y'], pinned: ['p1', 'p2'] }
    expect(withLinkMoved(layout, 'Sales', ['a', 'b', 'c'], 'c', 'a').order).toEqual(['c', 'a', 'b', 'z', 'y'])
    expect(withLinkMoved(layout, 'Favorites', ['p1', 'p2'], 'p2', 'p1').pinned).toEqual(['p2', 'p1'])
    expect(withLinkMoved(layout, 'Sales', ['a', 'b'], 'a', 'a')).toBe(layout)
  })
})

describe('Workspaces under workspaces', () => {
  const l = (id: string, parent?: string): DeskLink => ({ id, label: id, path: `/w/${id}`, group: 'Workspaces', defaultVisible: true, parent })
  it('lists a child beneath its parent and keeps the order', () => {
    const tree = nestLinks([l('a'), l('b'), l('a1', 'a'), l('a2', 'a'), l('c')])
    expect(tree.map(n => [n.link.id, n.children.map(c => c.id)])).toEqual([['a', ['a1', 'a2']], ['b', []], ['c', []]])
  })
  it('stands a child on its own when its parent is not there, and ignores a link that names itself', () => {
    expect(nestLinks([l('x', 'missing'), l('y', 'y')]).map(n => n.link.id)).toEqual(['x', 'y'])
  })
})

