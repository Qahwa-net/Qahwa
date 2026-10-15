import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { Icon } from '../controls/Icon.tsx'
import { ConfirmDialog, Modal } from '../controls/Modal.tsx'

/** Gives a document a new name, or merges it into another one (every link to it follows), in a dialog. */
export function RenameDialog({ doctype, name, onRenamed, onClose }: {
  doctype: string
  name: string
  onRenamed: (newName: string) => void
  onClose: () => void
}) {
  const [newName, setNewName] = useState(name)
  const [merge, setMerge] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const target = newName.trim()

  const rename = useMutation({
    mutationFn: () => api.rename(doctype, name, target, merge),
    onSuccess: onRenamed,
    onSettled: () => setConfirming(false),
  })
  const message = rename.error instanceof ApiError ? rename.error.details[0]?.message ?? rename.error.message : rename.isError ? 'Could not rename.' : null
  const submit = () => { if (merge) setConfirming(true); else rename.mutate() }
  const ready = target !== '' && target !== name && !rename.isPending

  return (
    <>
      <Modal title={merge ? 'Merge' : 'Rename'} subtitle={`${doctype} ${name}`} size="sm" onClose={onClose} dismissible={!rename.isPending}
        footer={<>
          <button className="btn" onClick={onClose} disabled={rename.isPending}>Cancel</button>
          <button className="btn primary" form="rename-form" disabled={!ready}><Icon name="pencil" /> {merge ? 'Merge' : 'Rename'}</button>
        </>}>
        <form id="rename-form" className="modal-form" onSubmit={(e) => { e.preventDefault(); if (ready) submit() }}>
          <div className="field">
            <label htmlFor="rename-new">New name</label>
            <input id="rename-new" data-autofocus value={newName} maxLength={140} onChange={(e) => setNewName(e.target.value)} />
          </div>
          <label className="switch">
            <input type="checkbox" checked={merge} onChange={(e) => setMerge(e.target.checked)} />
            <span>Merge into an existing {doctype}</span>
          </label>
          <p className="muted small-text">Links to this document, its table rows, shares and user permissions are updated.</p>
        </form>
        {message && <div className="banner error" role="alert">{message}</div>}
      </Modal>
      {confirming && (
        <ConfirmDialog title="Merge documents?" confirmLabel="Merge" tone="danger" busy={rename.isPending} onClose={() => setConfirming(false)} onConfirm={() => rename.mutate()}
          message={<>&ldquo;{name}&rdquo; will be removed and everything that used it will use &ldquo;{target}&rdquo; instead.</>} />
      )}
    </>
  )
}
