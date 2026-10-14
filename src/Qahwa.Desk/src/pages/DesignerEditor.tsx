import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { DesignerDetail, FieldError, FieldTypeInfo } from '../api/types.ts'
import { FieldControl, type InputValue } from '../controls/FieldControl.tsx'
import { FieldCard, PermissionsEditor } from '../designer/FieldCard.tsx'
import {
  emptyDraft, errorMap, fromDetail, moveItem, newField, toDefinition, type AutonameKind, type Draft, type DraftField,
} from '../lib/designer.ts'
import { isEditable } from '../lib/fields.ts'

function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), ms)
    return () => clearTimeout(timer)
  }, [value, ms])
  return debounced
}

export function DesignerEditor() {
  const { name } = useParams()
  const isNew = name === undefined

  const types = useQuery({ queryKey: ['designer', 'types'], queryFn: api.designer.fieldTypes, retry: false, staleTime: Infinity })
  const detail = useQuery({
    queryKey: ['designer', 'detail', name],
    queryFn: () => api.designer.get(name!),
    enabled: !isNew,
    staleTime: 0,
    retry: false,
  })

  const error = (isNew ? null : detail.error) ?? types.error
  if (error) {
    const status = error instanceof ApiError ? error.status : 0
    return (
      <div className="banner error" role="alert">
        {status === 403 ? 'Only a System Manager can design DocTypes.' : status === 404 ? 'This DocType does not exist.' : 'Could not load the designer.'}
        {' '}<Link to="/designer">Back to DocTypes</Link>
      </div>
    )
  }
  if (!types.data || (!isNew && !detail.data)) return <p className="muted" role="status">Loading…</p>

  return <EditorBody key={detail.data?.modified ?? name ?? 'new'} detail={detail.data ?? null} types={types.data} />
}

function EditorBody({ detail, types }: { detail: DesignerDetail | null; types: FieldTypeInfo[] }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const initial = useMemo(() => (detail ? fromDetail(detail) : emptyDraft()), [detail])
  const [draft, setDraft] = useState<Draft>(initial)
  const [open, setOpen] = useState<string | null>(null)
  const [tab, setTab] = useState<'preview' | 'changes' | 'json'>('preview')
  const [picker, setPicker] = useState(false)
  const [attempted, setAttempted] = useState(false)
  const [banner, setBanner] = useState<string | null>(null)
  const [serverErrors, setServerErrors] = useState<FieldError[]>([])

  const disabled = !draft.editable
  const typeInfo = useMemo(() => new Map(types.map((t) => [t.type, t])), [types])
  const definition = useMemo(() => toDefinition(draft), [draft])
  const initialJson = useMemo(() => JSON.stringify(toDefinition(initial)), [initial])
  const dirty = JSON.stringify(definition) !== initialJson

  // Warn before leaving with changes that are not saved.
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const doctypes = useQuery({ queryKey: ['designer', 'list'], queryFn: api.designer.list, retry: false })
  const roles = useQuery({
    queryKey: ['designer', 'roles'],
    queryFn: () => api.list('Role', { fields: ['name'], filters: [], orderBy: 'name', descending: false, limit: 200, offset: 0 }),
    retry: false,
  })

  // Live validation and the database changes this would cause, checked by the server as you type.
  const debouncedDefinition = useDebounced(definition, 450)
  const preview = useQuery({
    queryKey: ['designer', 'preview', JSON.stringify(debouncedDefinition), draft.isNew ? null : draft.name],
    queryFn: () => api.designer.preview(debouncedDefinition, draft.isNew ? null : draft.name),
    enabled: draft.editable,
    placeholderData: keepPreviousData,
    retry: false,
  })

  const errors = useMemo(() => {
    const all = [...(preview.data?.errors ?? []), ...serverErrors]
    const map = errorMap(all)
    // Do not shout about an empty name before the user has had a chance to type one.
    if (draft.isNew && !attempted && draft.name === '') delete map.name
    return map
  }, [preview.data, serverErrors, draft.isNew, draft.name, attempted])
  const generalErrors = [...(preview.data?.errors ?? []), ...serverErrors].filter((e) => e.field === '')
  const blocking = (preview.data?.errors.length ?? 0) > 0

  const patch = (changes: Partial<Draft>) => { setDraft((d) => ({ ...d, ...changes })); setServerErrors([]) }
  const patchField = (key: string, changes: Partial<DraftField>) =>
    patch({ fields: draft.fields.map((f) => (f.key === key ? { ...f, ...changes } : f)) })

  function addField(info: FieldTypeInfo) {
    const field = newField(info, draft.fields.map((f) => f.name))
    patch({ fields: [...draft.fields, field] })
    setOpen(field.key)
    setPicker(false)
  }

  function removeField(field: DraftField) {
    if (field.locked && !window.confirm(
      `Remove "${field.label || field.name}"? Its column and data stay in the database but are no longer shown.`)) return
    patch({
      fields: draft.fields.filter((f) => f.key !== field.key),
      titleField: draft.titleField === field.name ? '' : draft.titleField,
      autonameField: draft.autonameField === field.name ? '' : draft.autonameField,
    })
  }

  const save = useMutation({
    mutationFn: async () =>
      draft.isNew ? api.designer.create(definition) : api.designer.update(draft.name, definition, draft.modified),
    onSuccess: async (saved) => {
      setBanner(null)
      setServerErrors([])
      const savedName = saved.definition.name
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['designer'] }),
        queryClient.invalidateQueries({ queryKey: ['metas'] }),
        queryClient.invalidateQueries({ queryKey: ['meta', savedName] }),
        queryClient.invalidateQueries({ queryKey: ['list', savedName] }),
      ])
      queryClient.setQueryData(['designer', 'detail', savedName], saved)
      if (draft.isNew) navigate(`/designer/${encodeURIComponent(savedName)}`, { replace: true })
    },
    onError: (e) => {
      if (e instanceof ApiError && e.details.length > 0) {
        setServerErrors(e.details)
        setBanner('Please fix the highlighted problems.')
      } else {
        setBanner(e instanceof ApiError ? e.message : 'Could not save.')
      }
    },
  })

  const remove = useMutation({
    mutationFn: () => api.designer.remove(draft.name),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['designer'] }),
        queryClient.invalidateQueries({ queryKey: ['metas'] }),
      ])
      queryClient.removeQueries({ queryKey: ['meta', draft.name] })
      navigate('/designer', { replace: true })
    },
    onError: (e) => setBanner(e instanceof ApiError ? e.message : 'Could not delete.'),
  })

  const groups = useMemo(() => {
    const byGroup = new Map<string, FieldTypeInfo[]>()
    for (const t of types) byGroup.set(t.group, [...(byGroup.get(t.group) ?? []), t])
    return [...byGroup.entries()]
  }, [types])

  const modules = [...new Set((doctypes.data ?? []).map((d) => d.module).filter((m): m is string => Boolean(m)))]
  const linkable = (doctypes.data ?? []).map((d) => d.name)
  const dataFields = draft.fields.filter((f) => isEditable(f.type))

  return (
    <>
      <div className="page-head">
        <div>
          <Link to="/designer" className="muted back">← DocTypes</Link>
          <h1>{draft.isNew ? 'New DocType' : draft.name} {dirty && <span className="badge">Not saved</span>}</h1>
        </div>
        <span className="spacer" />
        {!draft.isNew && draft.editable && (
          <button className="btn danger" disabled={remove.isPending || (draft.documentCount ?? 0) > 0}
            title={(draft.documentCount ?? 0) > 0 ? `${draft.documentCount} document(s) exist; delete them first` : undefined}
            onClick={() => { if (window.confirm(`Delete the DocType "${draft.name}" and its table? This cannot be undone.`)) remove.mutate() }}>
            Delete
          </button>
        )}
        {!draft.isNew && <Link className="btn" to={`/app/${encodeURIComponent(draft.name)}`}>Open documents</Link>}
        {draft.editable && (
          <button className="btn primary" disabled={!dirty || save.isPending}
            onClick={() => {
              setAttempted(true)
              if (blocking) {
                setBanner('Please fix the highlighted problems first. The Changes tab lists them.')
                setTab('changes')
                return
              }
              setBanner(null)
              save.mutate()
            }}>
            {save.isPending ? 'Saving…' : draft.isNew ? 'Create DocType' : 'Save changes'}
          </button>
        )}
      </div>

      {!draft.editable && (
        <div className="banner info" role="status">
          This is a {detail?.source === 'Core' ? 'framework' : 'standard'} DocType, so it is read-only here.
        </div>
      )}
      {banner && <div className="banner error" role="alert">{banner}</div>}
      {generalErrors.map((e) => <div key={e.message} className="banner error" role="alert">{e.message}</div>)}

      <div className="designer">
        <div className="designer-edit">
          <section className="card">
            <h2>Settings</h2>
            <div className="form-grid">
              <label className="field">
                <span>Name <span className="req">*</span></span>
                <input value={draft.name} disabled={disabled || !draft.isNew} autoFocus={draft.isNew}
                  onChange={(e) => patch({ name: e.target.value })} aria-invalid={Boolean(errors.name)} />
                {errors.name && <small className="error-text" role="alert">{errors.name}</small>}
                {!draft.isNew && <small className="muted">A DocType cannot be renamed yet.</small>}
              </label>
              <label className="field">
                <span>Module</span>
                <input list="module-list" value={draft.module} disabled={disabled} onChange={(e) => patch({ module: e.target.value })} />
                <datalist id="module-list">{modules.map((m) => <option key={m} value={m} />)}</datalist>
                {errors.module && <small className="error-text" role="alert">{errors.module}</small>}
              </label>
              <label className="field">
                <span>Title field</span>
                <select value={draft.titleField} disabled={disabled} onChange={(e) => patch({ titleField: e.target.value })}>
                  <option value="">None (use the ID)</option>
                  {dataFields.map((f) => <option key={f.key} value={f.name}>{f.label || f.name}</option>)}
                </select>
                {errors.titleField && <small className="error-text" role="alert">{errors.titleField}</small>}
              </label>
              <div className="field">
                <label htmlFor="autoname-kind">Document names</label>
                <select id="autoname-kind" value={draft.autonameKind} disabled={disabled}
                  onChange={(e) => patch({ autonameKind: e.target.value as AutonameKind })}>
                  <option value="hash">Random ID</option>
                  <option value="series">Numbered series (e.g. SUP-00001)</option>
                  <option value="field">From a field</option>
                  <option value="prompt">Typed by the user</option>
                </select>
                {draft.autonameKind === 'series' && (
                  <span className="inline">
                    <input aria-label="Series prefix" placeholder="SUP-" value={draft.seriesPrefix} disabled={disabled}
                      onChange={(e) => patch({ seriesPrefix: e.target.value })} />
                    <input aria-label="Series digits" type="number" min={1} max={10} value={draft.seriesDigits} disabled={disabled}
                      onChange={(e) => patch({ seriesDigits: Number(e.target.value) || 5 })} />
                  </span>
                )}
                {draft.autonameKind === 'field' && (
                  <select aria-label="Naming field" value={draft.autonameField} disabled={disabled}
                    onChange={(e) => patch({ autonameField: e.target.value })}>
                    <option value="">Choose a field…</option>
                    {dataFields.map((f) => <option key={f.key} value={f.name}>{f.label || f.name}</option>)}
                  </select>
                )}
                {errors.autoname && <small className="error-text" role="alert">{errors.autoname}</small>}
              </div>
            </div>
          </section>

          <section className="card">
            <div className="section-head">
              <h2>Fields</h2>
              <span className="spacer" />
              {draft.editable && (
                <button type="button" className="btn small" aria-expanded={picker} onClick={() => setPicker(!picker)}>+ Add field</button>
              )}
            </div>
            {errors.fields && <div className="banner error" role="alert">{errors.fields}</div>}

            {picker && (
              <div className="type-picker" role="menu" aria-label="Field types">
                {groups.map(([group, items]) => (
                  <div key={group}>
                    <h3>{group}</h3>
                    <div className="type-grid">
                      {items.map((t) => (
                        <button key={t.type} type="button" role="menuitem" className="type-option" disabled={!t.supported}
                          title={t.supported ? t.hint : (t.unsupportedReason ?? 'Not supported yet')} onClick={() => addField(t)}>
                          <strong>{t.label}</strong>
                          <small className="muted">{t.supported ? t.hint : 'Coming soon'}</small>
                        </button>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            )}

            {draft.fields.length === 0 && <p className="muted">No fields yet. Add one to start designing the form.</p>}
            <ul className="field-list">
              {draft.fields.map((field, index) => (
                <FieldCard key={field.key} field={field} index={index} total={draft.fields.length}
                  expanded={open === field.key} info={typeInfo.get(field.type)} disabled={disabled}
                  otherNames={draft.fields.filter((f) => f.key !== field.key).map((f) => f.name)}
                  doctypes={linkable} errors={errors}
                  onToggle={() => setOpen(open === field.key ? null : field.key)}
                  onChange={(changes) => patchField(field.key, changes)}
                  onMove={(delta) => patch({ fields: moveItem(draft.fields, index, index + delta) })}
                  onRemove={() => removeField(field)} />
              ))}
            </ul>
          </section>

          <section className="card">
            <h2>Who can use it</h2>
            <p className="muted">Administrators can always use every DocType. Add the roles that should be allowed to read, write, create or delete documents.</p>
            <PermissionsEditor permissions={draft.permissions} roles={(roles.data?.data ?? []).map((r) => r.name)}
              disabled={disabled} errors={errors} onChange={(permissions) => patch({ permissions })} />
          </section>
        </div>

        <aside className="designer-side">
          <div className="tabs" role="tablist">
            {(['preview', 'changes', 'json'] as const).map((t) => (
              <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>
                {t === 'preview' ? 'Form preview' : t === 'changes' ? `Changes${blocking ? ' ⚠' : ''}` : 'JSON'}
              </button>
            ))}
          </div>

          {tab === 'preview' && <PreviewForm draft={draft} />}
          {tab === 'changes' && (
            <div className="card">
              {preview.isFetching && <p className="muted" role="status">Checking…</p>}
              {!blocking && preview.data?.schema && (
                <>
                  <h3>What saving does to the database</h3>
                  {preview.data.schema.changes.length === 0
                    ? <p className="muted">Nothing: the table is already up to date.</p>
                    : <ul>{preview.data.schema.changes.map((c) => <li key={c.sql}>{c.description}</li>)}</ul>}
                  {preview.data.notes.map((n) => <p key={n} className="banner info">{n}</p>)}
                  {preview.data.schema.changes.length > 0 && (
                    <details><summary>Show SQL</summary>
                      <pre className="sql">{preview.data.schema.changes.map((c) => `${c.sql};`).join('\n\n')}</pre>
                    </details>
                  )}
                </>
              )}
              {blocking && (
                <>
                  <h3>Fix these before saving</h3>
                  <ul>{(preview.data?.errors ?? []).map((e, i) => <li key={i} className="error-text">{e.message}</li>)}</ul>
                </>
              )}
            </div>
          )}
          {tab === 'json' && (
            <div className="card">
              <p className="muted">The definition as saved. It can be kept in source control and shipped with an app.</p>
              <pre className="sql">{JSON.stringify(definition, null, 2)}</pre>
              <button className="btn small" onClick={() => void navigator.clipboard?.writeText(JSON.stringify(definition, null, 2))}>Copy</button>
            </div>
          )}
        </aside>
      </div>
    </>
  )
}

/** The form exactly as users will see it, with throwaway values you can type into. */
function PreviewForm({ draft }: { draft: Draft }) {
  const [values, setValues] = useState<Record<string, InputValue>>({})
  const fields = draft.fields.filter((f) => f.type === 'SectionBreak' || f.type === 'TabBreak' || isEditable(f.type))

  return (
    <div className="card">
      <h3>{draft.name || 'New DocType'}</h3>
      {fields.length === 0 ? <p className="muted">Add fields to see the form.</p> : (
        <div className="form-grid">
          {fields.map((f) => (
            <FieldControl key={f.key} field={f} preview
              value={values[f.name] ?? (f.type === 'Check' ? f.default === '1' : (f.default ?? ''))}
              onChange={(v) => setValues({ ...values, [f.name]: v })} />
          ))}
        </div>
      )}
    </div>
  )
}
