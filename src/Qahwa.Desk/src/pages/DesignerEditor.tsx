import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { DesignerDetail, DocTypeMeta, FieldError, FieldTypeInfo, IndicatorColor } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { FieldControl, type InputValue } from '../controls/FieldControl.tsx'
import { TableControl } from '../controls/TableControl.tsx'
import { FieldCard, PermissionsEditor } from '../designer/FieldCard.tsx'
import { RulesEditor } from '../designer/RulesEditor.tsx'
import {
  emptyDraft, errorMap, fromDetail, moveItem, newField, toDefinition, type AutonameKind, type Draft, type DraftField,
} from '../lib/designer.ts'
import { isEditable } from '../lib/fields.ts'
import {
  applyLogic, documentContext, expressionDefaults, failingRules, fieldState, rowContext, rowExpressionDefaults,
  type FieldState, type Session,
} from '../lib/logic.ts'
import { emptyRow, type TableRow } from '../lib/tables.ts'

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
  const linkable = (doctypes.data ?? []).filter((d) => !d.isChildTable).map((d) => d.name)
  const childTargets = (doctypes.data ?? []).filter((d) => d.isChildTable).map((d) => d.name)
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
        {!draft.isNew && !draft.isChildTable && <Link className="btn" to={`/app/${encodeURIComponent(draft.name)}`}>Open documents</Link>}
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
              <div className="field full">
                <label className="check-line">
                  <input type="checkbox" checked={draft.isChildTable} disabled={disabled || !draft.isNew}
                    onChange={(e) => patch({ isChildTable: e.target.checked })} />
                  This DocType is a table used inside other documents (a child table)
                </label>
                <small className="muted">
                  {draft.isNew
                    ? 'Use it for lines such as the items of an order. It has no list or form of its own; other DocTypes add it as a Table field.'
                    : draft.isChildTable ? 'This is a child table. This cannot be changed after it is created.' : 'This is a normal DocType. This cannot be changed after it is created.'}
                </small>
                {errors.isChildTable && <small className="error-text" role="alert">{errors.isChildTable}</small>}
              </div>
              {!draft.isChildTable && <div className="field full">
                <label className="check-line">
                  <input type="checkbox" checked={draft.isSubmittable} disabled={disabled || !draft.isNew}
                    onChange={(e) => patch({ isSubmittable: e.target.checked })} />
                  Documents are submitted and then locked (submittable)
                </label>
                <small className="muted">
                  {draft.isNew ? 'Drafts can be edited freely. Submitting locks them except for fields marked as editable after submit; cancelling locks them for good. This cannot be changed after it is created.'
                    : draft.isSubmittable ? 'Submittable. This cannot be changed after it is created.' : 'Not submittable. This cannot be changed after it is created.'}
                </small>
                {errors.isSubmittable && <small className="error-text" role="alert">{errors.isSubmittable}</small>}
              </div>}
              {!draft.isChildTable && <div className="field full">
                <label className="check-line">
                  <input type="checkbox" checked={draft.isSingle} disabled={disabled || !draft.isNew || draft.isTree}
                    onChange={(e) => patch({ isSingle: e.target.checked, allowRename: e.target.checked ? false : draft.allowRename })} />
                  Only one document exists (settings)
                </label>
                <label className="check-line">
                  <input type="checkbox" checked={draft.isTree} disabled={disabled || !draft.isNew || draft.isSingle}
                    onChange={(e) => patch({ isTree: e.target.checked })} />
                  Documents form a tree (each can have a parent)
                </label>
                {!draft.isChildTable && <label className="check-line">
                  <input type="checkbox" checked={draft.trackChanges} disabled={disabled}
                    onChange={(e) => patch({ trackChanges: e.target.checked })} />
                  Keep an updates log (who changed what, and when)
                </label>}
                {!draft.isSingle && <StatusSettings draft={draft} disabled={disabled} patch={patch} />}
                {!draft.isSingle && <label className="check-line">
                  <input type="checkbox" checked={draft.allowRename} disabled={disabled}
                    onChange={(e) => patch({ allowRename: e.target.checked })} />
                  Documents can be renamed or merged
                </label>}
                <small className="muted">
                  A single DocType has one document named after it, with no list. A tree adds the fields Parent and Is Group. Neither can be changed after the DocType is created.
                </small>
                {errors.isSingle && <small className="error-text" role="alert">{errors.isSingle}</small>}
                {errors.isTree && <small className="error-text" role="alert">{errors.isTree}</small>}
                {errors.allowRename && <small className="error-text" role="alert">{errors.allowRename}</small>}
              </div>}
              {!draft.isChildTable && <label className="field">
                <span>Title field</span>
                <select value={draft.titleField} disabled={disabled} onChange={(e) => patch({ titleField: e.target.value })}>
                  <option value="">None (use the ID)</option>
                  {dataFields.map((f) => <option key={f.key} value={f.name}>{f.label || f.name}</option>)}
                </select>
                {errors.titleField && <small className="error-text" role="alert">{errors.titleField}</small>}
              </label>}
              {!draft.isChildTable && <div className="field">
                <label htmlFor="autoname-kind">Document names</label>
                <select id="autoname-kind" value={draft.autonameKind} disabled={disabled}
                  onChange={(e) => patch({ autonameKind: e.target.value as AutonameKind })}>
                  <option value="hash">Random ID</option>
                  <option value="series">Numbered series (e.g. SUP-00001)</option>
                  <option value="field">From a field</option>
                  <option value="expression">From an expression</option>
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
                {draft.autonameKind === 'series' && <small className="muted">The prefix may hold {'{YYYY}'}, {'{YY}'}, {'{MM}'}, {'{DD}'} (today) and {'{field_name}'}, for example INV-{'{YYYY}'}-: each different result has its own counter.</small>}
                {draft.autonameKind === 'field' && (
                  <select aria-label="Naming field" value={draft.autonameField} disabled={disabled}
                    onChange={(e) => patch({ autonameField: e.target.value })}>
                    <option value="">Choose a field…</option>
                    {dataFields.map((f) => <option key={f.key} value={f.name}>{f.label || f.name}</option>)}
                  </select>
                )}
                {draft.autonameKind === 'expression' && (
                  <input aria-label="Naming expression" placeholder={'upper(region) + "-" + customer'} value={draft.autonameField} disabled={disabled}
                    onChange={(e) => patch({ autonameField: e.target.value })} />
                )}
                {draft.autonameKind === 'expression' && <small className="muted">An expression over the fields of the document that gives its name, for example {'upper(region) + "-" + customer'}. The name must be unique.</small>}
                {errors.autoname && <small className="error-text" role="alert">{errors.autoname}</small>}
              </div>}
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
                        <button key={t.type} type="button" role="menuitem" className="type-option" disabled={!t.supported || (t.type === 'Table' && draft.isChildTable)}
                          title={!t.supported ? (t.unsupportedReason ?? 'Not supported yet') : t.type === 'Table' && draft.isChildTable ? 'A child table cannot contain another table' : t.hint} onClick={() => addField(t)}>
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
                  doctypes={linkable} childTables={childTargets} inChildTable={draft.isChildTable} submittable={draft.isSubmittable} errors={errors}
                  fieldNames={draft.fields.map((f) => f.name)} linkFields={draft.fields.filter((f) => f.type === 'Link')}
                  onToggle={() => setOpen(open === field.key ? null : field.key)}
                  onChange={(changes) => patchField(field.key, changes)}
                  onMove={(delta) => patch({ fields: moveItem(draft.fields, index, index + delta) })}
                  onRemove={() => removeField(field)} />
              ))}
            </ul>
          </section>

          <section className="card">
            <h2>Rules</h2>
            <p className="muted">
              Conditions every {draft.isChildTable ? 'row' : 'document'} must satisfy before it can be saved.
              {draft.isChildTable && ' Rules here are checked for each row, and can read the document as parent.'}
            </p>
            <RulesEditor rules={draft.validations}
              fieldNames={draft.fields.filter((f) => f.type !== 'SectionBreak' && f.type !== 'TabBreak' && f.type !== 'ColumnBreak').map((f) => f.name)}
              fieldLabels={Object.fromEntries(draft.fields.map((f) => [f.name, f.label ?? '']))} allowParent={draft.isChildTable}
              errors={errors} disabled={disabled} onChange={(validations) => patch({ validations })} />
          </section>

          {!draft.isChildTable && <section className="card">
            <h2>Who can use it</h2>
            <p className="muted">Administrators can always use every DocType. Add the roles that should be allowed to read, write, create or delete documents.</p>
            <PermissionsEditor submittable={draft.isSubmittable} permissions={draft.permissions} roles={(roles.data?.data ?? []).map((r) => r.name)}
              disabled={disabled} errors={errors} onChange={(permissions) => patch({ permissions })} />
          </section>}
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

/** The form exactly as users will see it, with the field logic running on throwaway values you can type into. */
function PreviewForm({ draft }: { draft: Draft }) {
  const { user } = useAuth()
  const [values, setValues] = useState<Record<string, InputValue>>({})
  const [tables, setTables] = useState<Record<string, TableRow[]>>({})

  const tableFields = draft.fields.filter((f) => f.type === 'Table' && f.options)
  const childQueries = useQueries({
    queries: tableFields.map((f) => ({
      queryKey: ['designer', 'detail', f.options],
      queryFn: () => api.designer.get(f.options!),
      retry: false,
    })),
  })
  const children: Record<string, DocTypeMeta> = {}
  tableFields.forEach((f, i) => {
    const detail = childQueries[i]?.data
    if (detail) children[f.options!] = detail.definition
  })

  const meta: DocTypeMeta = {
    name: draft.name || 'New DocType', isChildTable: draft.isChildTable, isSubmittable: draft.isSubmittable, trackChanges: draft.trackChanges,
    fields: draft.fields, validations: draft.validations,
  }
  const session: Session = { user: user?.user ?? '', roles: user?.roles ?? [], isNew: true }
  const fields = draft.fields.filter((f) => f.type === 'SectionBreak' || f.type === 'TabBreak' || f.type === 'Table' || isEditable(f.type))

  // Start from each field's default, let the person's input override it, then run the formulas.
  const defaults: Record<string, InputValue> = {}
  for (const f of draft.fields.filter((f) => isEditable(f.type))) {
    defaults[f.name] = f.type === 'Check' ? f.default === '1' : f.default && !f.default.startsWith('=') ? f.default : ''
  }
  const start = expressionDefaults(meta, children, { ...defaults, ...values }, session)
  const view = applyLogic(meta, children, start, tables, session)
  const context = documentContext(meta, children, view.values, view.tables, session)
  const failing = failingRules(meta, context)
  const stateOf = (f: (typeof draft.fields)[number]): FieldState => fieldState(f, context)

  return (
    <div className="card">
      <h3>{draft.name || 'New DocType'}{draft.isChildTable && <span className="badge"> child table</span>}</h3>
      {draft.isChildTable && <p className="muted">Its fields become the columns of the grid in every document that uses it. This is one row.</p>}
      {fields.length === 0 ? <p className="muted">Add fields to see the form.</p> : (
        <div className="form-grid">
          {fields.map((f) => {
            if (f.type !== 'Table') {
              return <FieldControl key={f.key} field={f} preview state={stateOf(f)} value={view.values[f.name] ?? ''}
                onChange={(v) => setValues({ ...values, [f.name]: v })} />
            }

            const child = f.options ? children[f.options] : undefined
            if (!f.options) return <div key={f.key} className="field full"><span>{f.label || f.name}</span><small className="muted">Choose the child table this field uses.</small></div>
            if (!child) return <div key={f.key} className="field full"><span>{f.label || f.name}</span><small className="muted">Loading…</small></div>
            return (
              <TableControl key={f.key} field={f} child={child} rows={view.tables[f.name] ?? []} preview errors={{}}
                rowState={(row, col) => fieldState(col, rowContext(child, row.values, view.values, meta, session))}
                makeRow={() => { const row = emptyRow(child); return { ...row, values: rowExpressionDefaults(child, row.values, view.values, meta, session) } }}
                onChange={(rows) => setTables({ ...tables, [f.name]: rows })} />
            )
          })}
        </div>
      )}
      {failing.length > 0 && (
        <div className="banner info" role="status">
          With this data, saving would be refused:
          <ul>{failing.map((r, i) => <li key={i}>{r.message || r.expression}</li>)}</ul>
        </div>
      )}
    </div>
  )
}

const COLORS: IndicatorColor[] = ['gray', 'blue', 'green', 'orange', 'red', 'purple']

/** Pick the field shown as a coloured status in lists, and a colour for each of its values. */
function StatusSettings({ draft, disabled, patch }: { draft: Draft; disabled: boolean; patch: (changes: Partial<Draft>) => void }) {
  const candidates = draft.fields.filter((f) => f.type === 'Select' || f.type === 'Data' || f.type === 'Link')
  const field = candidates.find((f) => f.name === draft.statusField)
  const values = field?.type === 'Select' ? (field.options ?? '').split('\n').map((o) => o.trim()).filter(Boolean) : []
  const colorOf = (value: string) => draft.indicators.find((i) => i.value === value)?.color ?? ''
  const setColor = (value: string, color: string) => patch({
    indicators: [...draft.indicators.filter((i) => i.value !== value), ...(color ? [{ value, color: color as IndicatorColor }] : [])],
  })

  return (
    <div className="status-settings">
      <label className="field"><span>Status field (shown as a coloured label in lists)</span>
        <select value={draft.statusField} disabled={disabled} onChange={(e) => patch({ statusField: e.target.value, indicators: [] })}>
          <option value="">None</option>
          {candidates.map((f) => <option key={f.key} value={f.name}>{f.label || f.name}</option>)}
        </select>
      </label>
      {values.length > 0 && (
        <ul className="status-colors">
          {values.map((value) => (
            <li key={value}>
              <span className={`pill pill-${colorOf(value) || 'gray'}`}>{value}</span>
              <select aria-label={`Colour of ${value}`} value={colorOf(value)} disabled={disabled} onChange={(e) => setColor(value, e.target.value)}>
                <option value="">Grey</option>
                {COLORS.filter((c) => c !== 'gray').map((c) => <option key={c} value={c}>{c[0]!.toUpperCase() + c.slice(1)}</option>)}
              </select>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
