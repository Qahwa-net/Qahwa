import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { BulkResult, DocField } from '../api/types.ts'
import { fromInput } from '../lib/fields.ts'
import { renamePairs } from '../lib/bulkRename.ts'
import { FieldInput, type InputValue } from './FieldControl.tsx'

/** Actions on the documents ticked in a list: set one field on all of them, or delete them. Each is reported on its own. */
export interface BulkReport {
  ok: number
  failed: BulkResult[]
}

export function BulkBar({ doctype, names, fields, canWrite, canDelete, canRename = false, onDone, onClear, onResult }: {
  doctype: string
  names: string[]
  /** The fields that can be set (not calculated, not passwords). */
  fields: DocField[]
  canWrite: boolean
  canDelete: boolean
  /** Rename by find and replace (only for types that allow renaming). */
  canRename?: boolean
  onDone: () => void
  onClear: () => void
  /** The outcome is shown by the list, which outlives this bar (it goes away when the selection is cleared). */
  onResult: (report: BulkReport) => void
}) {
  const queryClient = useQueryClient()
  const [field, setField] = useState('')
  const [value, setValue] = useState<InputValue>('')
  const [error, setError] = useState<string | null>(null)
  const [renaming, setRenaming] = useState(false)
  const [find, setFind] = useState('')
  const [replaceWith, setReplaceWith] = useState('')
  const plan = renamePairs(names, find, replaceWith)

  const target = fields.find((f) => f.name === field)
  const finish = async (result: { data: BulkResult[]; succeeded: number }) => {
    onResult({ ok: result.succeeded, failed: result.data.filter((r) => !r.ok) })
    setError(null)
    await queryClient.invalidateQueries({ queryKey: ['list', doctype] })
    onDone()
  }
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const update = useMutation({
    mutationFn: () => api.bulk(doctype, { action: 'update', names, field, value: target ? fromInput(target, value) : null }),
    onSuccess: finish,
    onError: fail,
  })
  const remove = useMutation({
    mutationFn: () => api.bulk(doctype, { action: 'delete', names }),
    onSuccess: finish,
    onError: fail,
  })
  const rename = useMutation({
    mutationFn: () => api.bulk(doctype, { action: 'rename', names, renames: plan.pairs }),
    onSuccess: finish,
    onError: fail,
  })
  const busy = update.isPending || remove.isPending || rename.isPending

  return (
    <div className="bulk-bar" role="region" aria-label="Actions for the selected documents">
      <strong>{names.length} selected</strong>
      {canWrite && fields.length > 0 && (
        <>
          <select aria-label="Field to set" value={field} onChange={(e) => { setField(e.target.value); setValue('') }}>
            <option value="">Set a field…</option>
            {fields.map((f) => <option key={f.name} value={f.name}>{f.label || f.name}</option>)}
          </select>
          {target && <span className="bulk-value"><FieldInput field={target} value={value} onChange={setValue} ariaLabel="New value" /></span>}
          <button className="btn small" disabled={!target || busy}
            onClick={() => { if (window.confirm(`Set "${target?.label || field}" on ${names.length} document(s)?`)) update.mutate() }}>Apply</button>
        </>
      )}
      {canRename && (renaming ? (
        <span className="bulk-rename">
          <input aria-label="Text to find" placeholder="Find" value={find} onChange={(e) => setFind(e.target.value)} />
          <input aria-label="Replace with" placeholder="Replace with" value={replaceWith} onChange={(e) => setReplaceWith(e.target.value)} />
          <button className="btn small" disabled={busy || plan.pairs.length === 0 || plan.problem !== null}
            onClick={() => { if (window.confirm(`Rename ${plan.pairs.length} document(s)? Links to them are updated.`)) rename.mutate() }}>Rename {plan.pairs.length}</button>
          <button className="btn small" onClick={() => setRenaming(false)}>Cancel</button>
          {plan.problem && <span role="alert" className="muted">{plan.problem}</span>}
          {!plan.problem && plan.pairs.length > 0 && <span className="muted">e.g. {plan.pairs[0]!.from} → {plan.pairs[0]!.to}</span>}
        </span>
      ) : <button className="btn small" disabled={busy} onClick={() => setRenaming(true)}>Rename…</button>)}
      {canDelete && (
        <button className="btn small danger" disabled={busy}
          onClick={() => { if (window.confirm(`Delete ${names.length} document(s)? This cannot be undone.`)) remove.mutate() }}>Delete</button>
      )}
      <span className="spacer" />
      <button className="btn small" onClick={onClear}>Clear selection</button>

      {error && <div className="banner error" role="alert">{error}</div>}
    </div>
  )
}
