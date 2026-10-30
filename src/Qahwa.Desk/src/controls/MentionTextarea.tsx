import { useEffect, useId, useRef, useState, type TextareaHTMLAttributes } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { insertMention, mentionAt } from '../lib/activity.ts'

type Props = Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'value' | 'onChange'> & {
  doctype: string
  docName: string
  value: string
  onChange: (value: string) => void
}

/** A comment box: typing "@" and a few letters offers the people who may read the document; pick one to insert their @email. */
export function MentionTextarea({ doctype, docName, value, onChange, onKeyDown, onBlur, ...rest }: Props) {
  const listId = useId()
  const ref = useRef<HTMLTextAreaElement>(null)
  const [caret, setCaret] = useState(0)
  const [dismissed, setDismissed] = useState<number | null>(null)
  const [active, setActive] = useState(0)
  const [debounced, setDebounced] = useState('')

  const at = mentionAt(value, caret)
  const open = at !== null && dismissed !== at.start
  const query = at?.query ?? ''

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(query), 150)
    return () => clearTimeout(timer)
  }, [query])

  const people = useQuery({
    queryKey: ['mention-users', doctype, docName, debounced],
    queryFn: () => api.activity.mentionUsers(doctype, docName, debounced),
    enabled: open,
    retry: false,
    staleTime: 15_000,
  })
  const options = open ? people.data ?? [] : []

  function pick(email: string) {
    const result = insertMention(value, caret, email)
    if (!result) return
    onChange(result.text)
    setCaret(result.caret)
    setActive(0)
    requestAnimationFrame(() => {
      ref.current?.focus()
      ref.current?.setSelectionRange(result.caret, result.caret)
    })
  }

  return (
    <div className="mention-box">
      <textarea
        {...rest} ref={ref} value={value}
        role="combobox" aria-expanded={open && options.length > 0} aria-controls={listId} aria-autocomplete="list"
        aria-activedescendant={open && options[active] ? `${listId}-${active}` : undefined}
        onChange={(e) => { onChange(e.target.value); setCaret(e.target.selectionStart); setActive(0) }}
        onSelect={(e) => setCaret(e.currentTarget.selectionStart)}
        onBlur={(e) => { setDismissed(at?.start ?? null); onBlur?.(e) }}
        onKeyDown={(e) => {
          if (open && options.length > 0) {
            if (e.key === 'ArrowDown') { e.preventDefault(); setActive((a) => Math.min(a + 1, options.length - 1)); return }
            if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); return }
            if ((e.key === 'Enter' || e.key === 'Tab') && !e.ctrlKey && !e.metaKey) { e.preventDefault(); pick(options[active]!.value); return }
          }
          if (e.key === 'Escape' && open) { e.preventDefault(); setDismissed(at!.start); return }
          onKeyDown?.(e)
        }}
      />
      {open && (people.isFetching || options.length > 0 || people.isSuccess) && (
        <ul id={listId} role="listbox" className="link-options mention-options">
          {people.isFetching && options.length === 0 && <li className="link-note" role="status">Searching…</li>}
          {people.isSuccess && options.length === 0 && <li className="link-note" role="status">No one to mention</li>}
          {options.map((o, i) => (
            <li key={o.value} id={`${listId}-${i}`} role="option" aria-selected={i === active} className={i === active ? 'active' : undefined}
              onMouseDown={(e) => { e.preventDefault(); pick(o.value) }} onMouseEnter={() => setActive(i)}>
              <span>{o.label}</span>
              {o.description && o.description !== o.label && <small className="muted">{o.description}</small>}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
