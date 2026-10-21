import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { useI18n } from '../i18n.tsx'
import { formatCell, labelOf } from '../lib/fields.ts'

/**
 * A small preview of the document a Link field points to (ADR-116): the values of the fields its DocType marked "Show in the preview
 * card of a link". Nothing is shown (not even the button) when the DocType marked none. The document is read with the person's own
 * permissions, so a document they may not read shows nothing.
 */
export function LinkPreview({ doctype, name }: { doctype: string; name: string }) {
  useI18n() // re-renders when the language's words arrive, so translatable values follow
  const [open, setOpen] = useState(false)
  const meta = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), enabled: Boolean(doctype), retry: false, staleTime: 60_000 })
  const fields = (meta.data?.data.fields ?? []).filter((f) => f.inPreview && !f.hidden)
  const doc = useQuery({ queryKey: ['doc', doctype, name], queryFn: () => api.get(doctype, name), enabled: open && fields.length > 0, retry: false })
  if (fields.length === 0) return null

  return (
    <span className="link-preview">
      <button type="button" className="btn small" aria-expanded={open} aria-label={`Preview ${name}`} onClick={() => setOpen(!open)}>Preview</button>
      {open && (
        <div className="card link-preview-card" role="dialog" aria-label={`Preview of ${name}`}>
          {doc.isPending && <p className="muted" role="status">Loading…</p>}
          {doc.isError && <p className="muted">You cannot see this document.</p>}
          {doc.data && (
            <dl>
              {fields.map((f) => (
                <div key={f.name}><dt>{labelOf(f)}</dt><dd>{formatCell(f, doc.data[f.name]) || '—'}</dd></div>
              ))}
            </dl>
          )}
        </div>
      )}
    </span>
  )
}
