import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { SavedFilter } from '../api/types.ts'
import { decodeFilters, encodeFilters, type Filter } from '../lib/filters.ts'
import { Select } from './Select.tsx'

/** Pick a saved set of filters, save the current ones under a name (privately or for everyone), or delete one of yours. */
export function SavedFilters({ doctype, saved, current, canShare, onApply }: {
  doctype: string
  saved: SavedFilter[]
  current: Filter[]
  canShare: boolean
  onApply: (filters: Filter[]) => void
}) {
  const queryClient = useQueryClient()
  const [saving, setSaving] = useState(false)
  const [name, setName] = useState('')
  const [shared, setShared] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['list-settings', doctype] })
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not save.')

  const save = useMutation({
    mutationFn: () => api.listSettings.saveFilter(doctype, name.trim(), current, shared),
    onSuccess: async () => { setSaving(false); setName(''); setShared(false); setError(null); await refresh() },
    onError: fail,
  })
  const remove = useMutation({
    mutationFn: (id: string) => api.listSettings.deleteFilter(doctype, id),
    onSuccess: refresh,
    onError: fail,
  })

  // Which saved filter is showing now (so it can be deleted), by comparing what is applied with what was saved.
  const showing = saved.find((s) => s.filters && encodeFilters(decodeFilters(JSON.stringify(s.filters))) === encodeFilters(current) && current.length > 0)

  return (
    <div className="saved-filters">
      <Select aria-label="Saved filters" value={showing?.id ?? ''} onChange={(e) => {
        const chosen = saved.find((s) => s.id === e.target.value)
        if (chosen?.filters) onApply(decodeFilters(JSON.stringify(chosen.filters)))
      }}>
        <option value="">{saved.length === 0 ? 'No saved filters' : 'Saved filters…'}</option>
        {saved.map((s) => <option key={s.id} value={s.id}>{s.name}{s.isPublic ? (s.mine ? ' (shared)' : ` (by ${s.ownerName || s.owner})`) : ''}</option>)}
      </Select>
      {showing?.mine && (
        <button className="btn small" disabled={remove.isPending}
          onClick={() => { if (window.confirm(`Delete the saved filter "${showing.name}"?`)) remove.mutate(showing.id) }}>Delete</button>
      )}
      <button className="btn small" disabled={current.length === 0} aria-expanded={saving} onClick={() => setSaving(!saving)}>Save filters…</button>

      {saving && (
        <form className="save-filter" onSubmit={(e) => { e.preventDefault(); if (name.trim()) save.mutate() }}>
          <input aria-label="Name of the saved filter" placeholder="Name" maxLength={60} value={name} onChange={(e) => setName(e.target.value)} />
          {canShare && <label className="check-line"><input type="checkbox" checked={shared} onChange={(e) => setShared(e.target.checked)} /> Share with everyone</label>}
          <button className="btn small primary" disabled={!name.trim() || save.isPending}>Save</button>
        </form>
      )}
      {error && <small className="error-text" role="alert">{error}</small>}
    </div>
  )
}
