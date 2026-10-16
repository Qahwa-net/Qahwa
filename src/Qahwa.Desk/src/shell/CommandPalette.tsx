import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client.ts'
import type { MetaSummary } from '../api/types.ts'
import { MIN_SERVER_QUERY, pageItems, paletteItems } from '../lib/search.ts'
import { Modal } from '../controls/Modal.tsx'

/** The search box: type to go to a page, open or create a DocType, or find a document. Ctrl+K (or ⌘K) opens it anywhere. */
export function CommandPalette({ metas, canDesign, isAdministrator, onClose }: {
  metas: MetaSummary[]
  canDesign: boolean
  isAdministrator: boolean
  onClose: () => void
}) {
  const navigate = useNavigate()
  const listId = useId()
  const [typed, setTyped] = useState('')
  const [settled, setSettled] = useState('')
  const [active, setActive] = useState(0)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Ask the server once typing pauses; what is known here shows at once.
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(() => setSettled(typed.replace(/^(new|open|go to)\s+/i, '').trim()), 200)
    return () => { if (timer.current) clearTimeout(timer.current) }
  }, [typed])

  const found = useQuery({
    queryKey: ['search', settled],
    queryFn: () => api.search(settled),
    enabled: settled.length >= MIN_SERVER_QUERY,
    staleTime: 15_000,
    retry: false,
  })

  const pages = useMemo(() => pageItems(canDesign, isAdministrator), [canDesign, isAdministrator])
  const items = useMemo(() => paletteItems(typed, metas, pages, settled.length >= MIN_SERVER_QUERY ? (found.data ?? null) : null), [typed, metas, pages, settled, found.data])
  const current = Math.min(active, Math.max(items.length - 1, 0))

  function open(index: number) {
    const item = items[index]
    if (!item) return
    onClose()
    navigate(item.to)
  }

  return (
    <Modal title="Search" onClose={onClose} size="md">
      <input
        data-autofocus role="combobox" aria-expanded="true" aria-controls={listId} aria-activedescendant={items[current] ? `${listId}-${current}` : undefined}
        aria-label="Search pages, DocTypes and documents" placeholder="Search or type a command (for example: new customer)"
        value={typed} onChange={(e) => { setTyped(e.target.value); setActive(0) }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') { e.preventDefault(); setActive((current + 1) % Math.max(items.length, 1)) }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((current + items.length - 1) % Math.max(items.length, 1)) }
          else if (e.key === 'Enter') { e.preventDefault(); open(current) }
        }} />
      <ul id={listId} role="listbox" className="palette-list" aria-label="Results">
        {items.map((item, i) => (
          <li key={item.id} id={`${listId}-${i}`} role="option" aria-selected={i === current} className={i === current ? 'on' : undefined}
            onMouseEnter={() => setActive(i)} onClick={() => open(i)}>
            <span>{item.label}</span>
            <small className="muted">{item.hint}</small>
          </li>
        ))}
      </ul>
      {items.length === 0 && !found.isFetching && <p className="muted" role="status">Nothing found.</p>}
      {found.isFetching && <p className="muted" role="status">Searching…</p>}
    </Modal>
  )
}
