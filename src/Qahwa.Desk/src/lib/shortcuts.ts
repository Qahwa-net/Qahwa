export type ShortcutAction = 'search' | 'help' | 'new'

export interface KeyLike {
  key: string
  ctrlKey: boolean
  metaKey: boolean
  altKey: boolean
  shiftKey: boolean
}

/** What a key press means, or null. Typing in a field is never interrupted, except by Ctrl/⌘+K. */
export function shortcutFor(event: KeyLike, typing: boolean): ShortcutAction | null {
  const key = event.key.toLowerCase()
  if ((event.ctrlKey || event.metaKey) && !event.altKey && key === 'k') return 'search'
  if (typing || event.ctrlKey || event.metaKey) return null
  if (event.altKey && !event.shiftKey && key === 'n') return 'new'
  if (!event.altKey && event.key === '?') return 'help'
  return null
}

/** True when the key press lands in something people type into. */
export function isTyping(target: EventTarget | null): boolean {
  const element = target as { tagName?: string; isContentEditable?: boolean } | null
  const tag = element?.tagName?.toLowerCase()
  return tag === 'input' || tag === 'textarea' || tag === 'select' || element?.isContentEditable === true
}

/** From a list or a document page to the form for a new document of the same type; null elsewhere. */
export function newDocumentPath(pathname: string): string | null {
  const match = /^\/app\/([^/]+)(?:\/([^/]+))?\/?$/.exec(pathname)
  if (!match || match[2] === 'new') return null
  return `/app/${match[1]}/new`
}

export const SHORTCUTS: { keys: string; does: string }[] = [
  { keys: 'Ctrl/⌘ + K', does: 'Search, go to a page or create something' },
  { keys: 'Ctrl/⌘ + S', does: 'Save the document you are editing' },
  { keys: 'Alt + N', does: 'New document of the type you are looking at' },
  { keys: '?', does: 'Show this list' },
]
