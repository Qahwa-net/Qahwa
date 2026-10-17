import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { useAuth } from '../auth.tsx'

export interface DeskLink { id: string; label: string; path: string; group: string; defaultVisible: boolean }
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
    const rank = (g: string) => g === 'My work' ? 0 : g === 'Tools' ? 1 : 2
    return rank(a) - rank(b) || a.localeCompare(b)
  })
  if (pinned.length) { names.unshift('Favorites'); groups.set('Favorites', pinned) }
  return ordered(names, layout.groupOrder, n => n).map(n => [n, groups.get(n)!])
}
