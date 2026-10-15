import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Icon } from './Icon.tsx'

type Kind = 'ok' | 'error' | 'info'
interface Toast { id: number; text: string; kind: Kind }

const ToastContext = createContext<(text: string, kind?: Kind) => void>(() => {})

/** Short messages that fade by themselves ("Saved"), announced to screen readers without taking focus. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const next = useRef(1)
  const timers = useRef(new Set<number>())

  const dismiss = useCallback((id: number) => setToasts((all) => all.filter((t) => t.id !== id)), [])
  const toast = useCallback((text: string, kind: Kind = 'ok') => {
    const id = next.current++
    setToasts((all) => [...all.slice(-3), { id, text, kind }])
    const timer = window.setTimeout(() => { timers.current.delete(timer); dismiss(id) }, kind === 'error' ? 7000 : 3500)
    timers.current.add(timer)
  }, [dismiss])

  useEffect(() => {
    const pending = timers.current
    return () => pending.forEach((t) => window.clearTimeout(t))
  }, [])

  const value = useMemo(() => toast, [toast])
  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="toasts" role="status" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`toast ${t.kind}`}>
            <Icon name={t.kind === 'error' ? 'info' : 'checkCircle'} />
            <span>{t.text}</span>
            <button type="button" className="icon-btn" aria-label="Dismiss" onClick={() => dismiss(t.id)}><Icon name="x" size={14} /></button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  )
}

export const useToast = () => useContext(ToastContext)
