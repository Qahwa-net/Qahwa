import { useEffect, useId, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Icon } from './Icon.tsx'

const FOCUSABLE = 'a[href],button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),[tabindex]:not([tabindex="-1"])'

/**
 * A dialog over the page: Escape or a click outside closes it, Tab stays inside it, focus goes to it when it opens
 * and back to where it came from when it closes, and the page behind does not scroll.
 */
export function Modal({ title, subtitle, onClose, children, footer, size = 'md', dismissible = true }: {
  title: string
  subtitle?: string
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  size?: 'sm' | 'md' | 'lg' | 'xl'
  /** False while something is being saved, so the dialog cannot be dismissed half-way. */
  dismissible?: boolean
}) {
  const titleId = useId()
  const panel = useRef<HTMLDivElement>(null)
  const close = useRef(onClose)
  close.current = onClose
  const allowed = useRef(dismissible)
  allowed.current = dismissible

  useEffect(() => {
    const before = document.activeElement as HTMLElement | null
    const node = panel.current!
    const first = node.querySelector<HTMLElement>('[data-autofocus]') ?? node.querySelector<HTMLElement>(FOCUSABLE)
    ;(first ?? node).focus()
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && allowed.current) {
        event.stopPropagation()
        close.current()
      } else if (event.key === 'Tab') {
        const items = [...node.querySelectorAll<HTMLElement>(FOCUSABLE)]
        if (items.length === 0) return event.preventDefault()
        const head = items[0]!
        const tail = items[items.length - 1]!
        if (event.shiftKey && document.activeElement === head) { event.preventDefault(); tail.focus() }
        else if (!event.shiftKey && document.activeElement === tail) { event.preventDefault(); head.focus() }
      }
    }
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = overflow
      before?.focus?.()
    }
  }, [])

  return createPortal(
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget && allowed.current) close.current() }}>
      <div ref={panel} className={`modal ${size}`} role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
        <header className="modal-head">
          <div>
            <h2 id={titleId}>{title}</h2>
            {subtitle && <p className="muted">{subtitle}</p>}
          </div>
          <button type="button" className="icon-btn" aria-label="Close" data-tip="Close" disabled={!dismissible} onClick={onClose}>
            <Icon name="x" />
          </button>
        </header>
        <div className="modal-body">{children}</div>
        {footer && <footer className="modal-foot">{footer}</footer>}
      </div>
    </div>,
    document.body,
  )
}

/** Asks before something that cannot be undone, in place of the browser's own confirm box. */
export function ConfirmDialog({ title, message, confirmLabel, tone = 'primary', busy = false, onConfirm, onClose }: {
  title: string
  message: ReactNode
  confirmLabel: string
  tone?: 'primary' | 'danger'
  busy?: boolean
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Modal title={title} size="sm" onClose={onClose} dismissible={!busy}
      footer={<>
        <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button className={`btn ${tone === 'danger' ? 'danger-solid' : 'primary'}`} data-autofocus onClick={onConfirm} disabled={busy}>{confirmLabel}</button>
      </>}>
      <p>{message}</p>
    </Modal>
  )
}
