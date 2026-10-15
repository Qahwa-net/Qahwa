import type { WorkspaceBlock, WorkspaceShortcut } from '../api/types.ts'

/** The Desk's own pages a shortcut may lead to (the server checks the same list). */
export const PAGES: { value: string; label: string }[] = [
  { value: 'dashboards', label: 'Dashboards' },
  { value: 'auto-repeat', label: 'Auto Repeat' },
  { value: 'account', label: 'My account' },
  { value: 'designer', label: 'DocType Designer' },
  { value: 'customize', label: 'Customize Form' },
  { value: 'permissions', label: 'Role Permissions' },
  { value: 'workflows', label: 'Workflow Builder' },
  { value: 'assignment-rules', label: 'Assignment Rules' },
  { value: 'notification-rules', label: 'Notification Rules' },
  { value: 'print-formats', label: 'Print Formats' },
  { value: 'naming', label: 'Naming Series' },
]

export const newShortcut = (): WorkspaceShortcut => ({ label: '', kind: 'DocType', target: '' })

export const newBlock = (type: WorkspaceBlock['type']): WorkspaceBlock =>
  type === 'Heading' ? { type, text: '', items: [] } : type === 'Dashboard' ? { type, items: [], dashboardId: '' } : { type, items: [newShortcut()] }

export function moveBlock<T>(list: T[], index: number, by: -1 | 1): T[] {
  const target = index + by
  if (target < 0 || target >= list.length) return list
  const next = [...list]
  ;[next[index], next[target]] = [next[target]!, next[index]!]
  return next
}

/** What to send: each block keeps only what its type uses, and shortcuts without a target are dropped. */
export function toRequest(draft: { title: string; sequence: number; roles: string[]; blocks: WorkspaceBlock[] }) {
  return {
    title: draft.title.trim(),
    sequence: Math.max(0, Math.round(draft.sequence)),
    roles: draft.roles,
    blocks: draft.blocks.map((b): WorkspaceBlock => {
      if (b.type === 'Heading') return { type: 'Heading', text: (b.text ?? '').trim(), items: [] }
      if (b.type === 'Dashboard') return { type: 'Dashboard', dashboardId: b.dashboardId || null, items: [] }
      return { type: 'Shortcuts', items: b.items.filter((s) => s.target).map((s) => ({ ...s, label: s.label.trim() || s.target })) }
    }),
  }
}
