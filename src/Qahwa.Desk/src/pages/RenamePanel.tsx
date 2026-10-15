import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'

/** Gives a document a new name, or merges it into another one (every link to it follows). */
export function RenamePanel({ doctype, name, onRenamed, onClose }: {
  doctype: string
  name: string
  onRenamed: (newName: string) => void
  onClose: () => void
}) {
  const [newName, setNewName] = useState(name)
  const [merge, setMerge] = useState(false)

  const rename = useMutation({
    mutationFn: () => api.rename(doctype, name, newName.trim(), merge),
    onSuccess: onRenamed,
  })
  const message = rename.error instanceof ApiError ? rename.error.details[0]?.message ?? rename.error.message : rename.isError ? 'Could not rename.' : null

  return (
    <section className="card rename-panel" aria-label="Rename">
      <div className="page-head">
        <h3>Rename</h3>
        <span className="spacer" />
        <button className="btn small" onClick={onClose}>Close</button>
      </div>
      <form className="share-add" onSubmit={(e) => {
        e.preventDefault()
        if (merge && !window.confirm(`Merge "${name}" into "${newName.trim()}"? "${name}" will be removed and everything that used it will use "${newName.trim()}" instead.`)) return
        rename.mutate()
      }}>
        <input aria-label="New name" value={newName} maxLength={140} onChange={(e) => setNewName(e.target.value)} />
        <label><input type="checkbox" checked={merge} onChange={(e) => setMerge(e.target.checked)} /> Merge into an existing {doctype}</label>
        <button className="btn primary" disabled={rename.isPending || !newName.trim() || newName.trim() === name}>{merge ? 'Merge' : 'Rename'}</button>
      </form>
      <p className="muted">Links to this document, its table rows, shares and user permissions are updated.</p>
      {message && <div className="banner error" role="alert">{message}</div>}
    </section>
  )
}
