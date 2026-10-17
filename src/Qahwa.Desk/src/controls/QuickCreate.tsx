import { InQuickCreate } from './QuickCreateContext.ts'
import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { Doc, DocField, MetaResponse } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { fromInput, isEditable } from '../lib/fields.ts'
import { applyLogic, documentContext, expressionDefaults, fieldState, rowContext, type Tables, type Values } from '../lib/logic.ts'
import { rowsToPayload } from '../lib/tables.ts'
import { FieldControl } from './FieldControl.tsx'
import { TableControl } from './TableControl.tsx'
import { Modal } from './Modal.tsx'

export function QuickCreate({ doctype, initialText, onCreated, onClose }: { doctype: string; initialText: string; onCreated: (doc: Doc) => void; onClose: () => void }) {
  const [saving, setSaving] = useState(false)
  const meta = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  return <Modal title={`New ${doctype}`} subtitle="Create and select a linked document without leaving this page." onClose={onClose} size="lg" dismissible={!saving}>
    {meta.isPending && <p role="status">Loading fields…</p>}
    {meta.isError && <p className="error-text" role="alert">Could not load this document type.</p>}
    {meta.data && (meta.data.permissions.create ? <QuickCreateForm doctype={doctype} response={meta.data} onBusyChange={setSaving} initialText={initialText} onCreated={onCreated} onClose={onClose} /> : <p role="alert">You do not have permission to create this document.</p>)}
  </Modal>
}
function QuickCreateForm({ doctype, response, initialText, onCreated, onClose, onBusyChange }: { onBusyChange: (busy: boolean) => void; doctype: string; response: MetaResponse; initialText: string; onCreated: (doc: Doc) => void; onClose: () => void }) {
  const meta = response.data, children = response.children ?? {}
  const { user } = useAuth(), queryClient = useQueryClient()
  const session = { user: user?.user ?? '', roles: user?.roles ?? [], isNew: true }
  const [values, setValues] = useState<Values>(() => {
    const initial: Values = {}
    for (const f of meta.fields.filter(f => isEditable(f.type))) initial[f.name] = f.type === 'Check' ? ['1', 'true'].includes(f.default ?? '') : f.default && !f.default.startsWith('=') ? f.default : ''
    const title = meta.titleField ?? (meta.autoname?.startsWith('field:') ? meta.autoname.slice(6) : null)
    if (title && meta.fields.some(f => f.name === title && ['Data', 'Email', 'Phone', 'Url', 'Text', 'SmallText'].includes(f.type) && !f.readOnly && !f.formula)) initial[title] = initialText
    return expressionDefaults(meta, children, initial, session)
  })
  const [tables, setTables] = useState<Tables>({})
  const [name, setName] = useState(initialText)
  const [busy, setBusy] = useState(false), [error, setError] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const view = applyLogic(meta, children, values, tables, session)
  const context = documentContext(meta, children, view.values, view.tables, session)
  const writable = (f: DocField) => (response.writableLevels ?? [0]).includes(f.permLevel ?? 0)
  async function save() {
    setBusy(true); onBusyChange(true); setError(null); setErrors({})
    try {
      const payload: Record<string, unknown> = {}
      if (meta.autoname === 'prompt') payload.name = name
      for (const f of meta.fields.filter(f => isEditable(f.type) && writable(f) && !f.readOnly && !f.formula)) {
        const value = view.values[f.name]
        if (value !== undefined && value !== '') payload[f.name] = fromInput(f, value)
      }
      for (const f of meta.fields.filter(f => f.type === 'Table' && f.options && children[f.options] && writable(f))) payload[f.name] = rowsToPayload(children[f.options!]!, view.tables[f.name] ?? [])
      const doc = await api.create(doctype, payload)
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['link', doctype] }), queryClient.invalidateQueries({ queryKey: ['list', doctype] })])
      onCreated(doc)
    } catch (e) {
      if (e instanceof ApiError) { setError(e.message); setErrors(Object.fromEntries(e.details.map(d => [d.field, d.message]))) }
      else setError('Could not create the document.')
    } finally { setBusy(false); onBusyChange(false) }
  }
  // A portal inside the parent form must not submit that parent form.
  return <InQuickCreate.Provider value={true}><form className="form-stack" onSubmit={e => { e.preventDefault(); e.stopPropagation(); void save() }}>
    {error && <div className="banner error" role="alert">{error}</div>}
    <fieldset className="quick-create-fields" disabled={busy}>
      {meta.autoname === 'prompt' && <label className="field"><span>Name *</span><input required value={name} onChange={e => setName(e.target.value)} /></label>}
      <div className="form-grid">{meta.fields.map(f => {
        if (f.type === 'Table') {
          const child = f.options ? children[f.options] : undefined
          if (!child || f.hidden) return null
          return <TableControl key={f.name} field={f} child={child} rows={view.tables[f.name] ?? []} errors={errors} readOnly={!writable(f) || fieldState(f, context).readOnly} rowState={(row, cf) => fieldState(cf, rowContext(child, row.values, view.values, meta, session))} onChange={rows => setTables(t => ({ ...t, [f.name]: rows }))} />
        }
        return <FieldControl key={f.name} field={f} value={view.values[f.name] ?? ''} siblings={view.values} state={fieldState(f, context)} readOnly={!writable(f)} error={errors[f.name]} onChange={v => setValues(current => ({ ...current, [f.name]: v }))} />
      })}</div>
    </fieldset>
    <div className="row"><a href={`/app/${encodeURIComponent(doctype)}/new`} target="_blank" rel="noopener noreferrer">Open full form</a><span className="spacer" /><button type="button" className="btn" disabled={busy} onClick={onClose}>Cancel</button><button className="btn primary" type="submit" disabled={busy}>{busy ? 'Creating…' : 'Create and select'}</button></div>
  </form></InQuickCreate.Provider>
}
