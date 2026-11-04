import { useEffect, useId, useRef, useState } from 'react'
import { Icon, type IconName } from './Icon.tsx'

export interface MenuItem {
  label: string
  icon?: IconName
  /** An action to run; or leave it out and give a link to follow (a download). */
  onSelect?: () => void
  /** A link the browser follows, for downloads: shown as a menu item that is an anchor. */
  href?: string
  tone?: 'default' | 'danger'
  disabled?: boolean
}

/**
 * A "Menu" button that opens a short list of actions, as in Frappe's form header. It closes when an action is chosen, on Escape (focus returns
 * to the button) and when the page is clicked elsewhere; the arrow keys, Home and End move between the actions. Nothing is drawn without actions.
 */
export function ActionMenu({ items, label = 'Menu' }: { items: MenuItem[]; label?: string }) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const button = useRef<HTMLButtonElement>(null)
  const id = useId()

  useEffect(() => {
    if (!open) return
    const away = (event: MouseEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', away)
    root.current?.querySelector<HTMLElement>('[role="menuitem"]:not(:disabled)')?.focus()
    return () => document.removeEventListener('mousedown', away)
  }, [open])

  if (items.length === 0) return null

  const move = (event: React.KeyboardEvent) => {
    const all = [...(root.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not(:disabled)') ?? [])]
    const at = all.indexOf(document.activeElement as HTMLElement)
    const go = (to: number) => { event.preventDefault(); all[(to + all.length) % all.length]?.focus() }
    if (event.key === 'Escape') { event.preventDefault(); setOpen(false); button.current?.focus() }
    else if (event.key === 'ArrowDown') go(at + 1)
    else if (event.key === 'ArrowUp') go(at - 1)
    else if (event.key === 'Home') go(0)
    else if (event.key === 'End') go(all.length - 1)
  }

  return (
    <div className="action-menu" ref={root} onKeyDown={open ? move : undefined}>
      <button ref={button} type="button" className="btn" aria-haspopup="menu" aria-expanded={open} aria-controls={open ? id : undefined} onClick={() => setOpen(!open)}>
        {label} <Icon name="chevronDown" size={14} />
      </button>
      {open && (
        <ul id={id} className="action-menu-list" role="menu" aria-label={label}>
          {items.map((item) => (
            <li key={item.label} role="none">
              {item.href ? (
                <a role="menuitem" href={item.href} download onClick={() => setOpen(false)}>
                  {item.icon && <Icon name={item.icon} size={16} />}<span>{item.label}</span>
                </a>
              ) : (
                <button type="button" role="menuitem" className={item.tone === 'danger' ? 'danger' : undefined} disabled={item.disabled}
                  onClick={() => { setOpen(false); item.onSelect?.() }}>
                  {item.icon && <Icon name={item.icon} size={16} />}<span>{item.label}</span>
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
