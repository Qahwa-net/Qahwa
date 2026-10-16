import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import type { Doc } from '../api/types.ts'
import { Avatar, Icon, IconButton } from '../controls/Icon.tsx'
import { AssignmentRow, assignmentsKey } from './AssignPanel.tsx'
import { sharesKey } from './SharePanel.tsx'
import { Attachments } from './Attachments.tsx'
import { TagsPanel } from './TagsPanel.tsx'

const when = (value: unknown) => (value ? new Date(String(value)).toLocaleString() : '')

/** The side of a saved document: who it is assigned to, who it is shared with, and its details. */
export function DocRail({ doctype, doc, canAssign, canShare, onAssign, onShare }: {
  doctype: string
  doc: Doc
  canAssign: boolean
  canShare: boolean
  onAssign: () => void
  onShare: () => void
}) {
  const assignments = useQuery({ queryKey: assignmentsKey(doctype, doc.name), queryFn: () => api.assignments.forDocument(doctype, doc.name), retry: false })
  const shares = useQuery({ queryKey: sharesKey(doctype, doc.name), queryFn: () => api.share.list(doctype, doc.name), retry: false, enabled: canShare })
  const open = (assignments.data ?? []).filter((a) => a.status === 'Open')

  return (
    <aside className="doc-rail" aria-label="Document sidebar">
      {(canAssign || open.length > 0) && !assignments.isError && (
        <section className="rail-card" aria-label="Assigned to">
          <header>
            <h3>Assigned to</h3>
            {canAssign && <IconButton icon="plus" label="Assign to someone" onClick={onAssign} />}
          </header>
          {open.length === 0
            ? <p className="muted small-text">Nobody yet.</p>
            : <ul className="person-list compact">{open.map((a) => <li key={a.id}><AssignmentRow a={a} /></li>)}</ul>}
        </section>
      )}

      {canShare && !shares.isError && (
        <section className="rail-card" aria-label="Shared with">
          <header>
            <h3>Shared with</h3>
            <IconButton icon="plus" label="Share with someone" onClick={onShare} />
          </header>
          {!shares.data || shares.data.length === 0
            ? <p className="muted small-text">Not shared.</p>
            : (
              <ul className="avatar-stack">
                {shares.data.map((s) => <li key={s.user} title={`${s.fullName || s.user}${s.write ? ' (can edit)' : ''}`}><Avatar name={s.fullName || s.user} /></li>)}
              </ul>
            )}
        </section>
      )}

      {doctype !== 'Deleted Document' && <TagsPanel doctype={doctype} name={doc.name} canEdit={canAssign} />}

      <Attachments doctype={doctype} name={doc.name} canUpload={canAssign} />

      <section className="rail-card" aria-label="Details">
        <header><h3>Details</h3></header>
        <dl className="detail-list">
          <dt>ID</dt><dd><code>{doc.name}</code></dd>
          <dt>Created</dt><dd>{when(doc.creation)}<small className="muted"> by {String(doc.owner ?? '')}</small></dd>
          <dt>Modified</dt><dd>{when(doc.modified)}<small className="muted"> by {String(doc.modified_by ?? '')}</small></dd>
        </dl>
        <p className="muted small-text hint"><Icon name="info" size={14} /> Press <kbd>Ctrl</kbd>+<kbd>S</kbd> to save.</p>
      </section>
    </aside>
  )
}
