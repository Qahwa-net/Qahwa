import type { BundleKind, BundleReport } from '../api/types.ts'

export const MAX_BYTES = 5 * 1024 * 1024

export const KINDS: { value: BundleKind; label: string; help: string }[] = [
  { value: 'DocType', label: 'DocTypes made in the Desk', help: 'Fields, permissions and settings of your own DocTypes' },
  { value: 'Customization', label: 'Customizations', help: 'Custom fields and property changes on any DocType' },
  { value: 'Workflow', label: 'Workflows', help: 'States and transitions' },
  { value: 'AssignmentRules', label: 'Assignment rules', help: 'Who gets new documents' },
  { value: 'NotificationRules', label: 'Notification rules', help: 'Who is told about what' },
]

/** The download address. Every kind ticked (or none) means everything. */
export function exportUrl(name: string, kinds: BundleKind[]): string {
  const params = new URLSearchParams()
  if (name.trim()) params.set('name', name.trim())
  if (kinds.length > 0 && kinds.length < KINDS.length) params.set('kinds', kinds.join(','))
  const query = params.toString()
  return `/api/bundle/export${query ? `?${query}` : ''}`
}

/** Reads the text of a chosen file as a bundle. Returns the parsed object, or a message saying why not. */
export function readBundle(text: string): { bundle: unknown } | { error: string } {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return { error: 'This file is not valid JSON.' }
  }
  const b = parsed as { format?: unknown; items?: unknown } | null
  if (!b || typeof b !== 'object' || b.format !== 'qahwa-bundle' || !Array.isArray(b.items)) return { error: 'This is not a Qahwa bundle file.' }
  return { bundle: parsed }
}

export function summary(report: BundleReport): string {
  const done = `${report.applied} item${report.applied === 1 ? '' : 's'} ${report.dryRun ? 'can be applied' : 'applied'}`
  return report.failed > 0 ? `${done}, ${report.failed} refused` : done
}
