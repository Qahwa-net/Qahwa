import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { useAuth } from '../auth.tsx'

export interface DeskLink { id: string; label: string; path: string; group: string; defaultVisible: boolean; icon?: string | null; parent?: string | null }
export interface DeskWorkspace { id: string; title: string }
export interface DeskLayout { pinned: string[]; hidden: string[]; order: string[]; groupOrder: string[]; collapsedGroups: string[]; homeShortcuts: string[]; defaultWorkspace: string | null }
export interface DeskState { layout: DeskLayout; personal: boolean; defaultRole: string | null; modified: string | null; links: DeskLink[]; workspaces: DeskWorkspace[] }
export interface DeskRoleLayout { role: string; layout: DeskLayout; modified: string | null }
export const emptyDesk = (): DeskLayout => ({ pinned: [], hidden: [], order: [], groupOrder: [], collapsedGroups: [], homeShortcuts: [], defaultWorkspace: null })
export function ordered<T>(items: T[], ids: string[], id: (item: T) => string): T[] {
  const rank = new Map(ids.map((value, index) => [value, index]))
  return [...items].sort((a, b) => (rank.get(id(a)) ?? Number.MAX_SAFE_INTEGER) - (rank.get(id(b)) ?? Number.MAX_SAFE_INTEGER))
}
export function moveId(ids: string[], id: string, by: -1 | 1): string[] {
  const index = ids.indexOf(id), next = index + by
  if (index < 0 || next < 0 || next >= ids.length) return ids
  const result = [...ids]; [result[index], result[next]] = [result[next]!, result[index]!]; return result
}
export function useDeskSettings() {
  const { user } = useAuth()
  return useQuery({ queryKey: ['desk-settings', user?.user], queryFn: api.desk.get, enabled: Boolean(user), retry: false, staleTime: 30_000 })
}
export function sidebarGroups(links: DeskLink[], layout: DeskLayout): [string, DeskLink[]][] {
  const visible = ordered(links.filter(l => !layout.hidden.includes(l.id)), layout.order, l => l.id)
  const pinned = ordered(visible.filter(l => layout.pinned.includes(l.id)), layout.pinned, l => l.id)
  const groups = new Map<string, DeskLink[]>()
  for (const link of visible.filter(l => l.defaultVisible && !layout.pinned.includes(l.id))) groups.set(link.group, [...(groups.get(link.group) ?? []), link])
  const names = [...groups.keys()].sort((a, b) => {
    return groupRank(a) - groupRank(b) || a.localeCompare(b)
  })
  if (pinned.length) { names.unshift('Favorites'); groups.set('Favorites', pinned) }
  return ordered(names, layout.groupOrder, n => n).map(n => [n, groups.get(n)!])
}

/** Where a group sits when the person has not arranged the sidebar: their own work, then workspaces, then the business modules, then the tools, and the system's own DocTypes last. */
export const groupRank = (group: string): number =>
  group === 'My work' ? 0 : group === 'Workspaces' ? 1 : group === 'Tools' ? 98 : group === 'Core' ? 99 : 50

/** Groups that are not business modules: they have no module page of their own. */
export const NOT_A_MODULE = new Set(['Favorites', 'My work', 'Workspaces', 'Tools', 'Dashboards', 'Reports', 'Saved views'])

/** Long groups start folded so the sidebar is short; the group holding the page being viewed always opens. */
export const FOLDED_BY_DEFAULT_OVER = 12

/**
 * Whether a group is shown folded. A choice the person made (they folded it, saved with their desk) always wins; otherwise a group starts
 * folded when it is Tools, the system's Core, or long, unless they unfolded it on this device or the page they are on is inside it.
 */
export function groupFolded(group: string, count: number, layout: DeskLayout, unfolded: ReadonlySet<string>, holdsCurrentPage: boolean): boolean {
  if (layout.collapsedGroups.includes(group)) return true
  const startsFolded = group === 'Tools' || group === 'Core' || count > FOLDED_BY_DEFAULT_OVER
  return startsFolded && !unfolded.has(group) && !holdsCurrentPage
}

/** The unfolded groups remembered on this device (the folded ones are saved with the desk). */
export function readUnfolded(): Set<string> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem('qahwa.sidebar.unfolded') ?? '[]')
    return new Set(Array.isArray(parsed) ? parsed.filter((g): g is string => typeof g === 'string') : [])
  } catch { return new Set() }
}

export function saveUnfolded(groups: ReadonlySet<string>): void {
  try { localStorage.setItem('qahwa.sidebar.unfolded', JSON.stringify([...groups])) } catch { /* private mode: the choice lasts until reload */ }
}

/** The list with <paramref name="id"/> taken out and put just before <paramref name="target"/> (or at the end when the target is null). */
export function moveBefore(ids: string[], id: string, target: string | null): string[] {
  if (id === target || !ids.includes(id)) return ids
  const rest = ids.filter(x => x !== id)
  const at = target === null ? rest.length : rest.indexOf(target)
  if (at < 0) return ids
  return [...rest.slice(0, at), id, ...rest.slice(at)]
}

/** The layout after a group was dragged before another: every visible group is written down, so the new order is complete. */
export function withGroupMoved(layout: DeskLayout, visible: string[], group: string, before: string | null): DeskLayout {
  const groupOrder = moveBefore(visible, group, before)
  return groupOrder === visible ? layout : { ...layout, groupOrder }
}

/**
 * The layout after a link was dragged before another in the same group. Favorites are ordered by the pins; other links by the saved order,
 * where only the order inside the group matters, so the group's links go first and everything saved for other groups stays as it was.
 */
export function withLinkMoved(layout: DeskLayout, group: string, groupLinks: string[], link: string, before: string | null): DeskLayout {
  const moved = moveBefore(groupLinks, link, before)
  if (moved === groupLinks) return layout
  if (group === 'Favorites') return { ...layout, pinned: moved }
  return { ...layout, order: [...moved, ...layout.order.filter(id => !moved.includes(id))] }
}

export interface LinkNode { link: DeskLink; children: DeskLink[] }

/**
 * The links of one group as a tree one level deep: a link that names a parent in the same group is listed beneath it; one whose parent is
 * not there (hidden, or not for this person) stands on its own. The order of the links is kept.
 */
export function nestLinks(links: DeskLink[]): LinkNode[] {
  const ids = new Set(links.map(l => l.id))
  const nested = (l: DeskLink) => Boolean(l.parent && ids.has(l.parent) && l.parent !== l.id)
  const roots = links.filter(l => !nested(l))
  return roots.map(link => ({ link, children: links.filter(l => nested(l) && l.parent === link.id) }))
}
