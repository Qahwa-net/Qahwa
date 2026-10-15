import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client.ts'
import type { NotificationItem } from '../api/types.ts'
import { timeAgo } from '../lib/activity.ts'

const POLL_MS = 30_000

/** The bell in the top bar: how many notifications are unread, and a list to open them from. */
export function NotificationBell() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)

  const unread = useQuery({ queryKey: ['notifications', 'count'], queryFn: api.notifications.unread, refetchInterval: POLL_MS, retry: false })
  const list = useQuery({ queryKey: ['notifications', 'list'], queryFn: () => api.notifications.list(false), enabled: open, retry: false })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['notifications'] })

  const read = useMutation({ mutationFn: (id: string) => api.notifications.read(id), onSuccess: refresh })
  const readAll = useMutation({ mutationFn: api.notifications.readAll, onSuccess: refresh })

  // Close when clicking elsewhere or pressing Escape.
  useEffect(() => {
    if (!open) return
    const away = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false) }
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', away)
    document.addEventListener('keydown', esc)
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', esc) }
  }, [open])

  function openNotification(item: NotificationItem) {
    if (!item.read) read.mutate(item.id)
    setOpen(false)
    navigate(`/app/${encodeURIComponent(item.docType)}/${encodeURIComponent(item.docName)}`)
  }

  const count = unread.data ?? 0
  return (
    <div className="bell" ref={box}>
      <button className="btn icon" aria-label={count > 0 ? `Notifications, ${count} unread` : 'Notifications'} aria-expanded={open} onClick={() => setOpen(!open)}>
        🔔{count > 0 && <span className="bell-count">{count > 99 ? '99+' : count}</span>}
      </button>
      {open && (
        <div className="bell-panel" role="dialog" aria-label="Notifications">
          <div className="bell-head">
            <strong>Notifications</strong>
            <button className="link-button" disabled={count === 0 || readAll.isPending} onClick={() => readAll.mutate()}>Mark all read</button>
          </div>
          {list.isPending && <p className="muted pad" role="status">Loading…</p>}
          {list.data?.length === 0 && <p className="muted pad">Nothing yet.</p>}
          <ul>
            {list.data?.map((item) => (
              <li key={item.id} className={item.read ? 'read' : 'unread'}>
                <button onClick={() => openNotification(item)}>
                  <span>{item.subject}</span>
                  <small className="muted">{timeAgo(item.at)}</small>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
