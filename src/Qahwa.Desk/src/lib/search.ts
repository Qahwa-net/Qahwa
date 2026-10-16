import type { MetaSummary } from '../api/types.ts'

export interface PaletteItem {
  id: string
  kind: 'page' | 'doctype' | 'new' | 'document'
  label: string
  hint?: string
  to: string
}

export interface SearchResponse {
  doctypes: { name: string; module?: string | null; can_create: boolean }[]
  documents: { doctype: string; name: string; title?: string | null }[]
}

export const MIN_SERVER_QUERY = 2

const appPath = (doctype: string, name?: string) => `/app/${encodeURIComponent(doctype)}${name === undefined ? '' : `/${encodeURIComponent(name)}`}`

/** The pages that are not DocTypes. Tools are only offered to the people who may use them (the server decides again). */
export function pageItems(canDesign: boolean, isAdministrator: boolean): PaletteItem[] {
  const pages: [string, string][] = [['Home', '/'], ['Dashboards', '/dashboards'], ['Reports', '/reports'], ['Auto Repeat', '/auto-repeat'], ['My account', '/account']]
  if (canDesign) {
    pages.push(
      ['DocType Designer', '/designer'], ['Customize Form', '/customize'], ['Role Permissions', '/permissions'], ['Workflow Builder', '/workflows'],
      ['Webhooks', '/webhooks'], ['Export & Import', '/bundle'], ['Workspaces', '/workspaces'], ['Brand', '/brand'], ['Print Formats', '/print-formats'],
      ['Notification Rules', '/notification-rules'], ['Assignment Rules', '/assignment-rules'], ['Naming Series', '/naming'],
    )
    if (isAdministrator) pages.push(['Backup & Restore', '/backup'])
  }

  return pages.map(([label, to]) => ({ id: `page:${to}`, kind: 'page', label, hint: 'Page', to }))
}

/** How well a label matches what was typed: 0 starts with it, 1 has a word that does, 2 contains it, null no match. Case and accents do not matter. */
export function score(label: string, typed: string): number | null {
  const fold = (s: string) => s.normalize('NFD').replace(/\p{Diacritic}/gu, '').toLowerCase()
  const l = fold(label)
  const t = fold(typed.trim())
  if (t === '') return 2
  if (l.startsWith(t)) return 0
  if (l.split(/[\s\-_/]+/).some((w) => w.startsWith(t))) return 1
  return l.includes(t) ? 2 : null
}

/**
 * What the palette shows for a typed text: pages and DocTypes known here (instantly), then what the server found (DocTypes you may
 * create, and documents). "new customer" and "open customer" work too: those words are dropped before matching.
 */
export function paletteItems(typed: string, metas: MetaSummary[], pages: PaletteItem[], found: SearchResponse | null): PaletteItem[] {
  const wantsNew = /^new\s+/i.test(typed)
  const text = typed.replace(/^(new|open|go to)\s+/i, '')
  const ranked = <T extends { label: string }>(items: T[]): T[] =>
    items.map((item, i) => ({ item, s: score(item.label, text), i })).filter((x) => x.s !== null).sort((a, b) => a.s! - b.s! || a.i - b.i).map((x) => x.item)

  const out: PaletteItem[] = []
  if (!wantsNew) {
    out.push(...ranked(pages).slice(0, 5))
    out.push(...ranked(metas.filter((m) => m.module !== 'Core').map<PaletteItem>((m) => ({ id: `doctype:${m.name}`, kind: 'doctype', label: m.name, hint: m.module ?? 'List', to: appPath(m.name) }))).slice(0, 6))
  }

  for (const d of found?.doctypes ?? []) {
    if (d.can_create && score(d.name, text) !== null) out.push({ id: `new:${d.name}`, kind: 'new', label: `New ${d.name}`, hint: d.module ?? 'Create', to: `${appPath(d.name)}/new` })
  }

  if (!wantsNew) {
    for (const h of found?.documents ?? []) {
      out.push({ id: `doc:${h.doctype}:${h.name}`, kind: 'document', label: h.title && h.title !== h.name ? `${h.title} (${h.name})` : h.name, hint: h.doctype, to: appPath(h.doctype, h.name) })
    }
  }

  const seen = new Set<string>()
  return out.filter((item) => (seen.has(item.id) ? false : (seen.add(item.id), true))).slice(0, 30)
}
