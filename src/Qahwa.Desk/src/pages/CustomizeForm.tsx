import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { CustomizationView, DocField, FieldTypeInfo } from '../api/types.ts'
import { ExpressionInput } from '../designer/ExpressionInput.tsx'
import {
  addableTypes, customName, errorsFor, moveRow, newCustomField, stateFromView, toCustomization,
  type CustomizeState, type Row,
} from '../lib/customize.ts'

const SOURCE_LABEL = { Core: 'Framework', App: 'App', Custom: 'Custom' } as const
const LAYOUT = new Set(['SectionBreak', 'ColumnBreak', 'TabBreak', 'Heading', 'Html'])

/** Customize Form: add fields to, and adjust, DocTypes that ship with the framework or an app, without editing their files. */
export function CustomizeFormPage() {
  const { name } = useParams()
  const [filter, setFilter] = useState('')
  const items = useQuery({ queryKey: ['customize', 'list'], queryFn: api.customize.list, retry: false })

  const shown = useMemo(
    () => (items.data ?? []).filter((i) => i.name.toLowerCase().includes(filter.trim().toLowerCase())),
    [items.data, filter],
  )

  if (items.isError) {
    const forbidden = items.error instanceof ApiError && items.error.status === 403
    return <div className="banner error" role="alert">{forbidden ? 'Only a System Manager can customise forms.' : 'Could not load the DocTypes.'}</div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Customize Form</h1>
          <p className="muted">Add fields to framework and app DocTypes, change how their fields look and behave, and reorder them. The DocType itself is not edited, so you can always reset. To change a DocType you made yourself, use the <Link to="/designer">DocType Designer</Link>.</p>
        </div>
      </div>
      <div className="perm-layout">
        <aside>
          <input type="search" className="search" placeholder="Filter DocTypes…" aria-label="Filter DocTypes"
            value={filter} onChange={(e) => setFilter(e.target.value)} />
          <ul className="perm-list">
            {shown.map((item) => (
              <li key={item.name} className={item.name === name ? 'active' : undefined}>
                <Link to={`/customize/${encodeURIComponent(item.name)}`}>
                  {item.name}
                  {item.customized && <span className="badge" title={`${item.customFieldCount} custom field(s)`}> customised</span>}
                </Link>
              </li>
            ))}
            {items.isPending && <li className="muted">Loading…</li>}
            {items.data && shown.length === 0 && <li className="muted">No DocTypes match.</li>}
          </ul>
        </aside>
        <section>{name ? <CustomizeEditor key={name} name={name} /> : <p className="muted">Pick a DocType to customise.</p>}</section>
      </div>
    </>
  )
}

function CustomizeEditor({ name }: { name: string }) {
  const queryClient = useQueryClient()
  // Kept here, not in the editor, because the editor starts afresh after every save.
  const [notice, setNotice] = useState<string | null>(null)
  const view = useQuery({ queryKey: ['customize', 'view', name], queryFn: () => api.customize.get(name), retry: false, staleTime: 0 })
  const types = useQuery({ queryKey: ['designer', 'types'], queryFn: api.designer.fieldTypes, retry: false, staleTime: Infinity })
  const doctypes = useQuery({ queryKey: ['designer', 'list'], queryFn: api.designer.list, retry: false })

  if (view.isError) {
    const status = view.error instanceof ApiError ? view.error.status : 0
    return <div className="banner error" role="alert">
      {status === 404 ? 'This DocType cannot be customised here.' : status === 409 ? 'This DocType was made in the DocType Designer; change it there.' : 'Could not load the DocType.'}
    </div>
  }
  if (!view.data) return <p className="muted" role="status">Loading…</p>

  return <Editor key={view.data.modified ?? 'default'} view={view.data} notice={notice} onNotice={setNotice} types={addableTypes(types.data ?? [])}
    linkTargets={(doctypes.data ?? []).filter((d) => !d.isChildTable).map((d) => d.name)}
    onSaved={(next) => {
      queryClient.setQueryData(['customize', 'view', name], next)
      void queryClient.invalidateQueries({ queryKey: ['customize', 'list'] })
      void queryClient.invalidateQueries({ queryKey: ['meta'] })
    }} />
}

function Editor({ view, types, linkTargets, notice, onNotice, onSaved }: {
  view: CustomizationView
  notice: string | null
  onNotice: (text: string | null) => void
  types: FieldTypeInfo[]
  linkTargets: string[]
  onSaved: (view: CustomizationView) => void
}) {
  const initial = useMemo(() => stateFromView(view), [view])
  const [state, setState] = useState<CustomizeState>(initial)
  const [open, setOpen] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [problem, setProblem] = useState<string | null>(null)
  const [addType, setAddType] = useState('')
  const [addLabel, setAddLabel] = useState('')

  const payload = useMemo(() => toCustomization(state, view.definition), [state, view.definition])
  const dirty = JSON.stringify(payload) !== JSON.stringify(toCustomization(initial, view.definition))
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const fail = (error: unknown) => {
    onNotice(null)
    if (!(error instanceof ApiError)) return setProblem('Something went wrong. Please try again.')
    setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
    const general = error.details.filter((d) => !d.field).map((d) => d.message).join(' ')
    setProblem(error.status === 409 ? 'Someone else changed this. Reload the page to see their version.' : general || (error.details.length > 0 ? 'Please fix the highlighted fields.' : error.message))
  }

  const save = useMutation({
    mutationFn: () => api.customize.save(view.name, payload, view.modified),
    onSuccess: (next) => { setErrors({}); setProblem(null); onNotice('Saved. Forms use the changes at once.'); onSaved(next) },
    onError: fail,
  })
  const reset = useMutation({
    mutationFn: () => api.customize.reset(view.name),
    onSuccess: (next) => { setErrors({}); setProblem(null); onNotice('Back to the DocType as defined.'); onSaved(next) },
    onError: fail,
  })

  const disabled = !view.canEdit || save.isPending || reset.isPending
  const names = state.rows.map((r) => r.field.name)
  const patchRow = (index: number, patch: Partial<DocField>) =>
    setState((s) => ({ ...s, rows: s.rows.map((r, i) => (i === index ? { ...r, field: applyPatch(r, patch, names) } : r)) }))

  function add() {
    const info = types.find((t) => t.type === addType)
    if (!info) return
    const row = newCustomField(info, addLabel.trim(), names)
    setState((s) => ({ ...s, rows: [...s.rows, row] }))
    setOpen(row.key)
    setAddLabel('')
  }

  return (
    <div>
      <div className="page-head">
        <h2>{view.name} <span className="badge">{SOURCE_LABEL[view.source]}</span>{view.customized && <span className="badge"> customised</span>}</h2>
        <span className="spacer" />
        {view.customized && view.canEdit && (
          <button className="btn" disabled={disabled}
            onClick={() => { if (window.confirm(`Remove every customisation of ${view.name}? Custom fields disappear from forms; their data stays in the database.`)) reset.mutate() }}>
            Reset
          </button>
        )}
        {view.canEdit && <button className="btn primary" disabled={!dirty || disabled} onClick={() => save.mutate()}>{save.isPending ? 'Saving…' : 'Save'}</button>}
      </div>

      {!view.canEdit && <div className="banner info" role="status">Only an Administrator can customise the framework's own DocTypes.</div>}
      {problem && <div className="banner error" role="alert">{problem}</div>}
      {notice && !problem && <div className="banner info" role="status">{notice}</div>}
      {errors.customFields && <div className="banner error" role="alert">{errors.customFields}</div>}
      {errors.order && <div className="banner error" role="alert">{errors.order}</div>}

      <section className="card">
        <h3>Fields</h3>
        <ul className="custom-rows">
          {state.rows.map((row, index) => (
            <RowEditor key={row.key} row={row} index={index} total={state.rows.length} open={open === row.key} disabled={disabled}
              names={names} linkTargets={linkTargets} errors={errorsFor(errors, row.field.name)}
              onToggle={() => setOpen(open === row.key ? null : row.key)}
              onChange={(patch) => patchRow(index, patch)}
              onMove={(to) => setState((s) => ({ ...s, rows: moveRow(s.rows, index, to) }))}
              onRemove={() => setState((s) => ({ ...s, rows: s.rows.filter((_, i) => i !== index) }))} />
          ))}
        </ul>

        {view.canEdit && (
          <form className="share-add" onSubmit={(e) => { e.preventDefault(); add() }}>
            <select aria-label="Type of the new field" value={addType} disabled={disabled} onChange={(e) => setAddType(e.target.value)}>
              <option value="">Add a custom field…</option>
              {types.map((t) => <option key={t.type} value={t.type}>{t.label}</option>)}
            </select>
            <input aria-label="Label of the new field" placeholder="Label" value={addLabel} disabled={disabled || !addType}
              onChange={(e) => setAddLabel(e.target.value)} />
            <button className="btn" disabled={disabled || !addType}>Add</button>
          </form>
        )}
        <p className="muted">Added fields get a name starting with <code>custom_</code> and a column in the database. Removing one only hides it; its data is kept.</p>
      </section>

      <section className="card">
        <h3>Title field</h3>
        <label className="field">
          <span>The field that names a document in lists and links</span>
          <select value={state.titleField} disabled={disabled} onChange={(e) => setState((s) => ({ ...s, titleField: e.target.value }))}>
            <option value="">None (use the ID)</option>
            {state.rows.filter((r) => r.field.type === 'Data' || r.field.type === 'Email').map((r) => (
              <option key={r.key} value={r.field.name}>{r.field.label || r.field.name}</option>
            ))}
          </select>
          {errors.titleField && <small className="error-text" role="alert">{errors.titleField}</small>}
        </label>
      </section>
    </div>
  )
}

/** A custom field that has not been saved yet takes its name from its label, so the name is never something to type. */
function applyPatch(row: Row, patch: Partial<DocField>, names: string[]): DocField {
  const next = { ...row.field, ...patch }
  if (row.custom && row.isNew && patch.label !== undefined) {
    next.name = customName(patch.label || row.field.type, names.filter((n) => n !== row.field.name))
  }
  return next
}

function RowEditor({ row, index, total, open, disabled, names, linkTargets, errors, onToggle, onChange, onMove, onRemove }: {
  row: Row
  index: number
  total: number
  open: boolean
  disabled: boolean
  names: string[]
  linkTargets: string[]
  errors: Record<string, string>
  onToggle: () => void
  onChange: (patch: Partial<DocField>) => void
  onMove: (to: number) => void
  onRemove: () => void
}) {
  const { field } = row
  const layout = LAYOUT.has(field.type)
  const hasErrors = Object.keys(errors).length > 0
  const flag = (key: 'hidden' | 'readOnly' | 'required' | 'inListView', label: string, off = false) => (
    <label className="check-line"><input type="checkbox" checked={Boolean(field[key])} disabled={disabled || off}
      onChange={(e) => onChange({ [key]: e.target.checked })} /> {label}</label>
  )

  return (
    <li className={`custom-row${hasErrors ? ' has-error' : ''}`}>
      <div className="custom-row-head">
        <button type="button" className="link-button" aria-expanded={open} onClick={onToggle}>{field.label || field.name}</button>
        <small className="muted">{field.type}</small>
        {row.custom && <span className="badge">custom</span>}
        {hasErrors && <span className="error-text" role="alert">{Object.values(errors)[0]}</span>}
        <span className="spacer" />
        <button type="button" className="btn small icon" aria-label={`Move ${field.name} up`} disabled={disabled || index === 0} onClick={() => onMove(index - 1)}>↑</button>
        <button type="button" className="btn small icon" aria-label={`Move ${field.name} down`} disabled={disabled || index === total - 1} onClick={() => onMove(index + 1)}>↓</button>
        {row.custom && <button type="button" className="btn small icon danger" aria-label={`Remove ${field.name}`} disabled={disabled} onClick={onRemove}>✕</button>}
      </div>

      {open && (
        <div className="custom-row-body">
          <label className="field"><span>Label</span>
            <input value={field.label ?? ''} disabled={disabled} onChange={(e) => onChange({ label: e.target.value })} />
            {errors.label && <small className="error-text" role="alert">{errors.label}</small>}
          </label>
          {row.custom && <label className="field"><span>Name</span><input value={field.name} readOnly aria-readonly /><small className="muted">Cannot be changed.</small>
            {errors.name && <small className="error-text" role="alert">{errors.name}</small>}</label>}
          {row.custom && field.type === 'Link' && (
            <label className="field"><span>Links to</span>
              <select value={field.options ?? ''} disabled={disabled || !row.isNew} onChange={(e) => onChange({ options: e.target.value })}>
                <option value="">Choose a DocType…</option>
                {linkTargets.map((t) => <option key={t} value={t}>{t}</option>)}
              </select>
              {errors.options && <small className="error-text" role="alert">{errors.options}</small>}
            </label>
          )}
          {field.type === 'Select' && (
            <label className="field"><span>Choices (one per line)</span>
              <textarea rows={4} value={field.options ?? ''} disabled={disabled} onChange={(e) => onChange({ options: e.target.value })} />
              {errors.options && <small className="error-text" role="alert">{errors.options}</small>}
            </label>
          )}
          {!layout && (
            <>
              <label className="field"><span>Help text</span>
                <input value={field.description ?? ''} disabled={disabled} onChange={(e) => onChange({ description: e.target.value })} /></label>
              <label className="field"><span>Default value</span>
                <input value={field.default ?? ''} disabled={disabled} onChange={(e) => onChange({ default: e.target.value })} />
                {errors.default && <small className="error-text" role="alert">{errors.default}</small>}</label>
              <div className="field">
                {flag('required', 'Required')}
                {flag('readOnly', 'Read only')}
                {flag('inListView', 'Show in list')}
                {flag('hidden', 'Hidden')}
              </div>
              <label className="field"><span>Permission level</span>
                <select value={field.permLevel ?? 0} disabled={disabled} onChange={(e) => onChange({ permLevel: Number(e.target.value) })}>
                  {Array.from({ length: 10 }, (_, n) => <option key={n} value={n}>{n === 0 ? '0 (same as the document)' : `Level ${n}`}</option>)}
                </select>
                {errors.permLevel && <small className="error-text" role="alert">{errors.permLevel}</small>}
              </label>
            </>
          )}
          <ExpressionInput label="Show only when" value={field.dependsOn ?? ''} names={names} disabled={disabled} help={false}
            placeholder="customer_type == 'Company'" serverError={errors.dependsOn} onChange={(value) => onChange({ dependsOn: value })} />
          {!layout && (
            <>
              <ExpressionInput label="Required when" value={field.mandatoryDependsOn ?? ''} names={names} disabled={disabled} help={false}
                serverError={errors.mandatoryDependsOn} onChange={(value) => onChange({ mandatoryDependsOn: value })} />
              <ExpressionInput label="Read-only when" value={field.readOnlyDependsOn ?? ''} names={names} disabled={disabled} help={false}
                serverError={errors.readOnlyDependsOn} onChange={(value) => onChange({ readOnlyDependsOn: value })} />
            </>
          )}
        </div>
      )}
    </li>
  )
}
