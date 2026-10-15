import { useEffect, useId, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { LinkOption } from '../api/types.ts'

interface Props {
  /** The stored value (a document name, or an email for users). */
  value: string
  onChange: (value: string, option?: LinkOption) => void
  /** Finds choices matching what was typed. An empty text asks for the most likely ones. */
  search: (text: string) => Promise<LinkOption[]>
  /** Finds the friendly name of the current value, so the box shows "Acme Ltd" rather than "CUST-00001". */
  resolve?: (value: string) => Promise<LinkOption | undefined>
  /** Identifies the source of the choices in the query cache (for example ['link', 'Customer']). */
  cacheKey: readonly unknown[]
  readOnly?: boolean
  invalid?: boolean
  id?: string
  ariaLabel?: string
  placeholder?: string
  /** A label already known for the current value (for example right after choosing it). */
  knownLabel?: string
}

const DELAY = 200

/**
 * A search-as-you-type select for documents: type a name or ID, pick from the list with the mouse or the arrow keys.
 * Choices show the friendly name with the ID as a smaller hint, and the chosen value is shown by its friendly name.
 */
export function LinkSelect({ value, onChange, search, resolve, cacheKey, readOnly, invalid, id, ariaLabel, placeholder, knownLabel }: Props) {
  const listId = useId()
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const [typed, setTyped] = useState(false) // true once the user has typed since opening
  const [debounced, setDebounced] = useState('')
  const [active, setActive] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(typed ? text : ''), DELAY)
    return () => clearTimeout(timer)
  }, [text, typed])

  const choices = useQuery({
    queryKey: [...cacheKey, 'search', debounced],
    queryFn: () => search(debounced),
    enabled: open && !readOnly,
    retry: false, // a person who cannot read the target simply gets no choices
    staleTime: 15_000,
  })
  const options = choices.data ?? []

  const title = useQuery({
    queryKey: [...cacheKey, 'title', value],
    queryFn: () => resolve!(value),
    enabled: Boolean(value) && Boolean(resolve) && !knownLabel,
    retry: false,
    staleTime: 60_000,
  })
  const label = knownLabel ?? title.data?.label ?? value

  useEffect(() => setActive(0), [debounced, open])

  function pick(option: LinkOption) {
    onChange(option.value, option)
    setOpen(false)
    setTyped(false)
    setText('')
  }

  function finish() {
    // Leaving the box with an exact ID typed counts as choosing it; anything else keeps the previous value.
    const exact = typed ? options.find((o) => o.value === text.trim() || o.label === text.trim()) : undefined
    if (exact && exact.value !== value) onChange(exact.value, exact)
    setOpen(false)
    setTyped(false)
    setText('')
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      if (!open) setOpen(true)
      else setActive((a) => Math.min(a + 1, Math.max(options.length - 1, 0)))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((a) => Math.max(a - 1, 0))
    } else if (e.key === 'Enter' && open) {
      e.preventDefault() // do not submit the form
      const option = options[active]
      if (option) pick(option)
    } else if (e.key === 'Escape' && open) {
      e.preventDefault()
      setOpen(false)
      setTyped(false)
      setText('')
    }
  }

  if (readOnly) {
    return <input id={id} type="text" readOnly value={label} aria-label={ariaLabel} aria-invalid={invalid} />
  }

  return (
    <div className="link-select">
      <input
        id={id} ref={inputRef} type="text" role="combobox" autoComplete="off" spellCheck={false}
        aria-expanded={open} aria-controls={listId} aria-autocomplete="list" aria-label={ariaLabel} aria-invalid={invalid}
        aria-activedescendant={open && options[active] ? `${listId}-${active}` : undefined}
        placeholder={value ? undefined : placeholder ?? 'Search…'}
        value={typed ? text : open ? '' : label}
        onFocus={() => setOpen(true)}
        onBlur={finish}
        onChange={(e) => { setText(e.target.value); setTyped(true); setOpen(true) }}
        onKeyDown={onKeyDown}
      />
      {value && (
        <button type="button" className="link-clear" aria-label="Clear" tabIndex={-1}
          onMouseDown={(e) => { e.preventDefault(); onChange('') ; inputRef.current?.focus() }}>✕</button>
      )}
      {open && (
        <ul id={listId} role="listbox" className="link-options">
          {choices.isPending && <li className="link-note" role="status">Searching…</li>}
          {choices.isSuccess && options.length === 0 && <li className="link-note" role="status">Nothing found</li>}
          {options.map((option, index) => (
            <li key={option.value} id={`${listId}-${index}`} role="option" aria-selected={index === active}
              className={index === active ? 'active' : undefined}
              // mouse down (not click) so the box does not lose focus, and finish() does not run first
              onMouseDown={(e) => { e.preventDefault(); pick(option) }}
              onMouseEnter={() => setActive(index)}>
              <span>{option.label}</span>
              {option.description && <small className="muted">{option.description}</small>}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
