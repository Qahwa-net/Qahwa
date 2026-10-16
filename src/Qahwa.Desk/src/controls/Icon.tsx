import type { ButtonHTMLAttributes, ReactNode } from 'react'

const PATHS = {
  share: <><path d="M4 12v7a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-7" /><path d="m16 6-4-4-4 4" /><path d="M12 2v13" /></>,
  userPlus: <><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M19 8v6M22 11h-6" /></>,
  pencil: <><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" /></>,
  bell: <><path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" /><path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" /></>,
  trash: <><path d="M3 6h18" /><path d="m19 6-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6" /><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></>,
  check: <path d="M20 6 9 17l-5-5" />,
  checkCircle: <><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14" /><path d="m22 4-10 10.01-3-3" /></>,
  ban: <><circle cx="12" cy="12" r="10" /><path d="m4.9 4.9 14.2 14.2" /></>,
  save: <><path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2Z" /><path d="M17 21v-8H7v8M7 3v5h8" /></>,
  chevronDown: <path d="m6 9 6 6 6-6" />,
  repeat: <><path d="m17 2 4 4-4 4" /><path d="M3 11v-1a4 4 0 0 1 4-4h14" /><path d="m7 22-4-4 4-4" /><path d="M21 13v1a4 4 0 0 1-4 4H3" /></>,
  play: <path d="m6 3 14 9-14 9z" />,
  pause: <><rect x="6" y="4" width="4" height="16" /><rect x="14" y="4" width="4" height="16" /></>,
  mail: <><rect x="2" y="4" width="20" height="16" rx="2" /><path d="m22 7-10 6L2 7" /></>,
  chart: <><path d="M3 3v18h18" /><path d="M7 15v3M12 9v9M17 5v13" /></>,
  printer: <><path d="M6 9V2h12v7" /><path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2" /><rect x="6" y="14" width="12" height="8" /></>,
  upload: <><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><path d="m17 8-5-5-5 5" /><path d="M12 3v12" /></>,
  download: <><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><path d="m7 10 5 5 5-5" /><path d="M12 15V3" /></>,
  x: <path d="M18 6 6 18M6 6l12 12" />,
  plus: <path d="M12 5v14M5 12h14" />,
  arrowLeft: <path d="m12 19-7-7 7-7M19 12H5" />,
  flag: <><path d="M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z" /><path d="M4 22v-7" /></>,
  calendar: <><rect x="3" y="4" width="18" height="18" rx="2" /><path d="M16 2v4M8 2v4M3 10h18" /></>,
  users: <><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" /></>,
  lock: <><rect x="3" y="11" width="18" height="11" rx="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" /></>,
  info: <><circle cx="12" cy="12" r="10" /><path d="M12 16v-4M12 8h.01" /></>,
} satisfies Record<string, ReactNode>

export type IconName = keyof typeof PATHS

/** Every icon the Desk has, for choosing one. */
export const ICON_NAMES = Object.keys(PATHS) as IconName[]

export const isIconName = (name: unknown): name is IconName => typeof name === 'string' && Object.prototype.hasOwnProperty.call(PATHS, name)

/** A line icon that takes the colour of the text around it. Decorative: give the button, not the icon, a label. */
export function Icon({ name, size = 18, filled = false }: { name: IconName; size?: number; filled?: boolean }) {
  return (
    <svg className="icon" width={size} height={size} viewBox="0 0 24 24" fill={filled ? 'currentColor' : 'none'} stroke="currentColor"
      strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      {PATHS[name]}
    </svg>
  )
}

interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children' | 'title'> {
  icon: IconName
  /** What the button does: read by screen readers and shown as a tooltip. */
  label: string
  tone?: 'default' | 'primary' | 'danger'
  /** A small number on the corner (followers, assignments...). */
  count?: number
  filled?: boolean
  pressed?: boolean
}

export function IconButton({ icon, label, tone = 'default', count, filled, pressed, className, ...rest }: IconButtonProps) {
  return (
    <button type="button" className={`icon-btn ${tone}${className ? ` ${className}` : ''}`} aria-label={label} data-tip={label}
      aria-pressed={pressed} {...rest}>
      <Icon name={icon} filled={filled} />
      {count !== undefined && count > 0 && <span className="icon-count" aria-hidden="true">{count > 99 ? '99+' : count}</span>}
    </button>
  )
}

/** Initials on a colour chosen from the name, so the same person always looks the same. */
export function Avatar({ name, size = 32 }: { name: string; size?: number }) {
  const text = name.trim()
  const parts = text.split(/[\s@._-]+/).filter(Boolean)
  const initials = ((parts[0]?.[0] ?? '?') + (parts.length > 1 ? parts[1]?.[0] ?? "" : '')).toUpperCase()
  let hash = 0
  for (const ch of text) hash = (hash * 31 + ch.charCodeAt(0)) % 360
  return (
    <span className="avatar" style={{ inlineSize: size, blockSize: size, background: `hsl(${hash} 45% 38%)`, fontSize: size * 0.4 }} aria-hidden="true">
      {initials}
    </span>
  )
}
