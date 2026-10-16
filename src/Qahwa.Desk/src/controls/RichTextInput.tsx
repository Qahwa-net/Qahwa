import { useEffect, useRef } from 'react'
import { safeHref, sanitizeHtml } from '../lib/richText.ts'

interface Props {
  id: string
  ariaLabel?: string
  value: string
  readOnly?: boolean
  invalid?: boolean
  onChange: (html: string) => void
}

const COMMANDS: { title: string; label: string; run: () => void }[] = [
  { label: 'B', title: 'Bold', run: () => document.execCommand('bold') },
  { label: 'I', title: 'Italic', run: () => document.execCommand('italic') },
  { label: 'U', title: 'Underline', run: () => document.execCommand('underline') },
  { label: '•', title: 'Bulleted list', run: () => document.execCommand('insertUnorderedList') },
  { label: '1.', title: 'Numbered list', run: () => document.execCommand('insertOrderedList') },
  {
    label: 'Link', title: 'Link',
    run: () => {
      const href = safeHref(window.prompt('Web address (https://…)') ?? '')
      if (href) document.execCommand('createLink', false, href)
    },
  },
  { label: '✕', title: 'Clear formatting', run: () => document.execCommand('removeFormat') },
]

/** Formatted text: a small toolbar and an editable area. What is typed is cleaned here and again by the server before it is stored. */
export function RichTextInput({ id, ariaLabel, value, readOnly, invalid, onChange }: Props) {
  const ref = useRef<HTMLDivElement>(null)
  const emitted = useRef<string | null>(null)

  useEffect(() => {
    const el = ref.current
    if (!el || readOnly || value === emitted.current) return
    el.innerHTML = sanitizeHtml(value) // only when the value came from outside, so the caret does not jump while typing
  }, [value, readOnly])

  if (readOnly) return <div id={id} className="rich-view" aria-label={ariaLabel} dangerouslySetInnerHTML={{ __html: sanitizeHtml(value) }} />

  const emit = () => {
    const html = sanitizeHtml(ref.current?.innerHTML ?? '')
    emitted.current = html
    onChange(html)
  }

  return (
    <div className="rich-editor" aria-invalid={invalid}>
      <div className="rich-toolbar" role="toolbar" aria-label="Formatting">
        {COMMANDS.map((c) => (
          <button key={c.title} type="button" title={c.title} aria-label={c.title}
            onMouseDown={(e) => e.preventDefault()} onClick={() => { ref.current?.focus(); c.run(); emit() }}>{c.label}</button>
        ))}
      </div>
      <div id={id} ref={ref} className="rich-area" contentEditable role="textbox" aria-multiline="true" aria-label={ariaLabel} suppressContentEditableWarning
        onInput={emit} onBlur={emit}
        onPaste={(e) => { e.preventDefault(); document.execCommand('insertText', false, e.clipboardData.getData('text/plain')) }} />
    </div>
  )
}
