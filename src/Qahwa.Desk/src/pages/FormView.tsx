import { WorkflowBar } from './WorkflowBar.tsx'
import { ActivityPanel } from './ActivityPanel.tsx'
import { AssignPanel } from './AssignPanel.tsx'
import { FollowButton } from './FollowButton.tsx'
import { RenamePanel } from './RenamePanel.tsx'
import { SharePanel } from './SharePanel.tsx'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useMatch, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { Doc, DocField, DocTypeMeta, Permissions } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { FieldControl, type InputValue } from '../controls/FieldControl.tsx'
import { TableControl } from '../controls/TableControl.tsx'
import { fromInput, hasColumn, isEditable, toInput } from '../lib/fields.ts'
import {
  applyLogic, documentContext, expressionDefaults, fetchTargets, fieldState, rowContext, rowExpressionDefaults,
  type FieldState, type Session,
} from '../lib/logic.ts'
import { emptyRow, rowsChanged, rowsFromDoc, rowsToPayload, tableFieldsOf, type TableRow } from '../lib/tables.ts'

type Values = Record<string, InputValue>
type Tables = Record<string, TableRow[]>

function initialValues(meta: DocTypeMeta, doc: Doc | null): Values {
  const values: Values = {}
  for (const field of meta.fields.filter((f) => isEditable(f.type))) {
    if (doc) values[field.name] = toInput(field, doc[field.name])
    else if (field.type === 'Check') values[field.name] = field.default === '1' || field.default === 'true'
    else values[field.name] = field.default && !field.default.startsWith('=') ? field.default : ''
  }
  return values
}

function initialTables(meta: DocTypeMeta, children: Record<string, DocTypeMeta>, doc: Doc | null): Tables {
  const tables: Tables = {}
  for (const { field, child } of tableFieldsOf(meta, children)) tables[field.name] = rowsFromDoc(child, doc?.[field.name])
  return tables
}

/** A field with no expressions at all is the common case; skip building a context for it. */
const hasLogic = (f: DocField) => Boolean(f.dependsOn || f.mandatoryDependsOn || f.readOnlyDependsOn || f.formula)
const staticState = (f: DocField): FieldState => ({ active: true, required: f.required, readOnly: Boolean(f.readOnly) })

export function FormView() {
  const { doctype = '', name } = useParams()
  const isNew = Boolean(useMatch('/app/:doctype/new'))

  const metaQuery = useQuery({ queryKey: ['meta', doctype], queryFn: () => api.meta(doctype), retry: false })
  const docQuery = useQuery({
    queryKey: ['doc', doctype, name],
    queryFn: () => api.get(doctype, name!),
    enabled: !isNew && Boolean(name),
    staleTime: 0,
    retry: false,
  })

  const error = (isNew ? null : docQuery.error) ?? metaQuery.error
  if (error) {
    const status = error instanceof ApiError ? error.status : 0
    return (
      <div className="banner error" role="alert">
        {status === 404 ? 'This document does not exist.' : status === 403 ? 'You do not have access to this document.' : 'Could not load this document.'}
        {' '}<Link to={`/app/${encodeURIComponent(doctype)}`}>Back to {doctype}</Link>
      </div>
    )
  }

  if (!metaQuery.data || (!isNew && !docQuery.data)) return <p className="muted" role="status">Loading…</p>

  const doc = isNew ? null : docQuery.data!
  return (
    <FormBody
      // A fresh form state whenever the saved document changes (after save, reload or conflict).
      key={`${doctype}/${doc?.name ?? 'new'}/${String(doc?.modified ?? '')}`}
      meta={metaQuery.data.data}
      perms={metaQuery.data.permissions}
      childMetas={metaQuery.data.children ?? {}}
      writableLevels={metaQuery.data.writableLevels}
      doc={doc}
    />
  )
}

function FormBody({ meta, perms, childMetas, writableLevels, doc }: {
  meta: DocTypeMeta
  perms: Permissions
  childMetas: Record<string, DocTypeMeta>
  writableLevels?: number[]
  doc: Doc | null
}) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const isNew = doc === null
  const workflow = useQuery({ queryKey: ['workflow', meta.name, doc?.name], queryFn: () => api.workflows.status(meta.name, doc!.name), enabled: !isNew, retry: false })
  const flow = workflow.data ?? null
  // With a workflow the state decides who may edit, and the workflow alone moves the document on.
  const editable = (isNew ? perms.create : perms.write) && (flow?.canEdit ?? true)
  /** Submittable DocTypes: 0 draft, 1 submitted (locked except "allow on submit" fields), 2 cancelled (locked). */
  const docstatus = meta.isSubmittable && doc ? Number(doc.docstatus ?? 0) : 0
  const locked = (f: DocField) => docstatus === 2 || (docstatus === 1 && !f.allowOnSubmit)
  const base = `/app/${encodeURIComponent(meta.name)}`
  /** Fields above level 0 are only editable by people with write rules at that level. */
  const canWriteField = (f: DocField) => editable && !locked(f) && f.name !== flow?.stateField && ((f.permLevel ?? 0) === 0 || (writableLevels?.includes(f.permLevel!) ?? false))

  const session = useMemo<Session>(
    () => ({ user: user?.user ?? '', roles: user?.roles ?? [], isNew }),
    [user, isNew],
  )
  const tableDefs = useMemo(() => tableFieldsOf(meta, childMetas), [meta, childMetas])
  const original = useMemo(() => initialValues(meta, doc), [meta, doc])
  const originalTables = useMemo(() => initialTables(meta, childMetas, doc), [meta, childMetas, doc])

  // What the user typed. Calculated values are derived from it below and never stored.
  const [values, setValues] = useState<Values>(() => (isNew ? expressionDefaults(meta, childMetas, original, session) : original))
  const [tables, setTables] = useState<Tables>(originalTables)
  const [promptName, setPromptName] = useState('')
  const [sharing, setSharing] = useState(false)
  const [renaming, setRenaming] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; conflict: boolean } | null>(null)

  const view = useMemo(() => applyLogic(meta, childMetas, values, tables, session), [meta, childMetas, values, tables, session])
  const docContext = documentContext(meta, childMetas, view.values, view.tables, session)
  const stateOf = (field: DocField): FieldState => (hasLogic(field) ? fieldState(field, docContext) : staticState(field))

  const changedNames = Object.keys(view.values).filter((key) => view.values[key] !== original[key] && canWriteField(meta.fields.find((f) => f.name === key)!))
  const changedTables = tableDefs.filter(({ field }) => canWriteField(field) && rowsChanged(originalTables[field.name] ?? [], view.tables[field.name] ?? []))
  const dirty = isNew ? true : changedNames.length > 0 || changedTables.length > 0

  // Warn before leaving the page with edits that are not saved.
  useEffect(() => {
    if (!dirty || isNew) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty, isNew])

  // "Fetch from": when a Link field changes, copy values out of the document it points at.
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>())
  useEffect(() => () => timers.current.forEach(clearTimeout), [])

  function scheduleFetch(key: string, run: () => Promise<void>) {
    clearTimeout(timers.current.get(key))
    timers.current.set(key, setTimeout(() => { void run().catch(() => undefined) }, 350))
  }

  function fetchForDocument(linkField: DocField, linkValue: string) {
    const targets = fetchTargets(meta, linkField.name)
    if (targets.length === 0) return
    scheduleFetch(`doc:${linkField.name}`, async () => {
      const source = linkValue && linkField.options ? await api.get(linkField.options, linkValue) : null
      setValues((current) => {
        const next = { ...current }
        for (const { field, source: from } of targets) {
          if (field.fetchIfEmpty && next[field.name] !== '' && next[field.name] !== undefined) continue
          next[field.name] = source ? toInput(field, source[from]) : ''
        }
        return next
      })
    })
  }

  function fetchForRow(table: { field: DocField; child: DocTypeMeta }, rowKey: string, linkField: DocField, linkValue: string) {
    const targets = fetchTargets(table.child, linkField.name)
    if (targets.length === 0) return
    scheduleFetch(`row:${table.field.name}:${rowKey}:${linkField.name}`, async () => {
      const source = linkValue && linkField.options ? await api.get(linkField.options, linkValue) : null
      setTables((current) => ({
        ...current,
        [table.field.name]: (current[table.field.name] ?? []).map((row) => {
          if (row.key !== rowKey) return row
          const nextValues = { ...row.values }
          for (const { field, source: from } of targets) {
            if (field.fetchIfEmpty && nextValues[field.name] !== '' && nextValues[field.name] !== undefined) continue
            nextValues[field.name] = source ? toInput(field, source[from]) : ''
          }
          return { ...row, values: nextValues }
        }),
      }))
    })
  }

  const save = useMutation({
    mutationFn: async () => {
      const send = (names: string[]) =>
        Object.fromEntries(names
          .filter((n) => canWriteField(meta.fields.find((f) => f.name === n)!))
          .map((n) => [n, fromInput(meta.fields.find((f) => f.name === n)!, view.values[n]!)]))
      const sendTables = (defs: typeof tableDefs) =>
        Object.fromEntries(defs.filter(({ field }) => canWriteField(field)).map(({ field, child }) => [field.name, rowsToPayload(child, view.tables[field.name] ?? [])]))

      if (isNew) {
        const filled = Object.keys(view.values).filter((n) => view.values[n] !== '')
        const withRows = tableDefs.filter(({ field }) => (view.tables[field.name]?.length ?? 0) > 0)
        return api.create(meta.name, {
          ...send(filled),
          ...sendTables(withRows),
          ...(meta.autoname === 'prompt' ? { name: promptName } : {}),
        })
      }
      return api.update(meta.name, doc.name, { ...send(changedNames), ...sendTables(changedTables), modified: doc.modified })
    },
    onSuccess: async (saved) => {
      setFieldErrors({})
      setBanner(null)
      await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
      queryClient.setQueryData(['doc', meta.name, saved.name], saved)
      if (isNew) navigate(`${base}/${encodeURIComponent(saved.name)}`, { replace: true })
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner({ text: 'Could not save. Please try again.', conflict: false })

      const byField = Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message]))
      setFieldErrors(byField)
      const general = error.details.filter((d) => !d.field).map((d) => d.message)
      // Rules that apply to the whole document have no field to sit under, so say them here.
      const shown = general.length > 0
        ? general.join(' ')
        : Object.keys(byField).length > 0 ? 'Please fix the highlighted fields.' : error.message
      setBanner({ text: shown, conflict: error.status === 409 && error.code === 'conflict' && !isNew })
    },
  })

  const transition = useMutation({
    mutationFn: (kind: 'submit' | 'cancel') => (kind === 'submit' ? api.submit : api.cancel)(meta.name, doc!.name, doc!.modified),
    onSuccess: async (saved) => {
      setFieldErrors({})
      setBanner(null)
      await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
      queryClient.setQueryData(['doc', meta.name, saved.name], saved)
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner({ text: 'Could not complete this. Please try again.', conflict: false })
      const general = error.details.map((d) => d.message).join(' ')
      setBanner({ text: general || error.message, conflict: error.status === 409 })
    },
  })

  const remove = useMutation({
    mutationFn: () => api.remove(meta.name, doc!.name),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
      queryClient.removeQueries({ queryKey: ['doc', meta.name, doc!.name] })
      navigate(base, { replace: true })
    },
    onError: (error) =>
      setBanner({ text: error instanceof ApiError ? error.message : 'Could not delete.', conflict: false }),
  })

  const clearErrorsFor = (name: string) =>
    setFieldErrors((errors) =>
      Object.fromEntries(Object.entries(errors).filter(([path]) => path !== name && !path.startsWith(`${name}[`))))

  const title = isNew ? `New ${meta.name}` : String((meta.titleField ? doc[meta.titleField] : null) || doc.name)
  const hasFields = meta.fields.some((f) => hasColumn(f.type) || f.type === 'SectionBreak' || f.type === 'Table')

  return (
    <>
      <div className="page-head">
        <div>
          {!meta.isSingle && <Link to={base} className="muted back">← {meta.name}</Link>}
          <h1>{title} {!isNew && dirty && <span className="badge">Not saved</span>}</h1>
        </div>
        <span className="spacer" />
        {!isNew && doc && <FollowButton doctype={meta.name} name={doc.name} />}
        {!isNew && meta.allowRename && perms.write && docstatus !== 1 && <button className="btn" onClick={() => setRenaming(!renaming)} aria-expanded={renaming}>Rename</button>}
        {!isNew && !meta.isSingle && perms.write && docstatus !== 2 && <button className="btn" onClick={() => setSharing(!sharing)} aria-expanded={sharing}>Share</button>}
        {!isNew && meta.isSubmittable && (
          <span className={`badge status-${docstatus}`}>{['Draft', 'Submitted', 'Cancelled'][docstatus]}</span>
        )}
        {!isNew && !flow && docstatus === 0 && meta.isSubmittable && perms.submit && (
          <button className="btn" disabled={dirty || transition.isPending} title={dirty ? 'Save your changes first' : undefined}
            onClick={() => { if (window.confirm(`Submit ${meta.name} "${doc!.name}"? It will be locked afterwards.`)) transition.mutate('submit') }}>
            Submit
          </button>
        )}
        {!isNew && !flow && docstatus === 1 && perms.cancel && (
          <button className="btn danger" disabled={dirty || transition.isPending}
            onClick={() => { if (window.confirm(`Cancel ${meta.name} "${doc!.name}"? It cannot be changed afterwards.`)) transition.mutate('cancel') }}>
            Cancel document
          </button>
        )}
        {!isNew && !meta.isSingle && perms.delete && docstatus !== 1 && (
          <button className="btn danger" disabled={remove.isPending}
            onClick={() => { if (window.confirm(`Delete ${meta.name} "${doc.name}"? This cannot be undone.`)) remove.mutate() }}>
            Delete
          </button>
        )}
        {editable && docstatus !== 2 && (
          <button className="btn primary" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        )}
      </div>

      {flow && doc && <WorkflowBar doctype={meta.name} doc={doc} status={flow} disabled={dirty} onError={(text, conflict) => setBanner({ text, conflict })} />}

      {renaming && doc && <RenamePanel doctype={meta.name} name={doc.name} onClose={() => setRenaming(false)}
        onRenamed={async (newName) => {
          await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
          queryClient.removeQueries({ queryKey: ['doc', meta.name, doc.name] })
          navigate(`${base}/${encodeURIComponent(newName)}`, { replace: true })
        }} />}
      {sharing && doc && <SharePanel doctype={meta.name} name={doc.name} onClose={() => setSharing(false)} />}
      {docstatus === 2 && <div className="banner info" role="status">This document is cancelled and cannot be changed.</div>}
      {docstatus === 1 && editable && <div className="banner info" role="status">This document is submitted. Only some fields can still be changed.</div>}
      {!editable && (
        <div className="banner info" role="status">
          {isNew ? 'You are not allowed to create this document.' : 'You have read-only access to this document.'}
        </div>
      )}
      {banner && (
        <div className="banner error" role="alert">
          {banner.text}
          {banner.conflict && (
            <> <button className="btn small" onClick={() => void queryClient.invalidateQueries({ queryKey: ['doc', meta.name, doc?.name] })}>
              Reload latest
            </button></>
          )}
        </div>
      )}

      <form className="card form-grid" onSubmit={(e) => { e.preventDefault(); if (dirty && editable) save.mutate() }}>
        {isNew && meta.autoname === 'prompt' && (
          <div className="field full">
            <label htmlFor="prompt-name">Name <span className="req">*</span></label>
            <input id="prompt-name" value={promptName} onChange={(e) => setPromptName(e.target.value)} aria-invalid={Boolean(fieldErrors.name)} />
            {fieldErrors.name && <small className="error-text" role="alert">{fieldErrors.name}</small>}
          </div>
        )}
        {meta.fields.map((field) => {
          if (field.type === 'Table') {
            const def = tableDefs.find((t) => t.field.name === field.name)
            if (!def || field.hidden) return null
            const state = stateOf(field)
            if (!state.active) return null

            const rowState = (row: TableRow, f: DocField): FieldState =>
              hasLogic(f) ? fieldState(f, rowContext(def.child, row.values, view.values, meta, session)) : staticState(f)
            const makeRow = (): TableRow => {
              const row = emptyRow(def.child)
              return { ...row, values: rowExpressionDefaults(def.child, row.values, view.values, meta, session) }
            }

            return (
              <TableControl key={field.name} field={field} child={def.child} rows={view.tables[field.name] ?? []}
                readOnly={!canWriteField(field) || state.readOnly} errors={fieldErrors} rowState={rowState} makeRow={makeRow}
                onChange={(rows) => { setTables({ ...tables, [field.name]: rows }); clearErrorsFor(field.name) }}
                onCellEdited={(index, name, value) => {
                  const column = def.child.fields.find((f) => f.name === name)
                  const row = (view.tables[field.name] ?? [])[index]
                  if (column?.type === 'Link' && row) fetchForRow(def, row.key, column, String(value))
                }} />
            )
          }

          return (
            <FieldControl key={field.name} field={field} state={stateOf(field)}
              value={view.values[field.name] ?? ''} readOnly={!canWriteField(field)} error={fieldErrors[field.name]}
              onChange={(v) => {
                setValues({ ...values, [field.name]: v })
                if (fieldErrors[field.name]) setFieldErrors({ ...fieldErrors, [field.name]: '' })
                if (field.type === 'Link') fetchForDocument(field, String(v))
              }} />
          )
        })}
        {!hasFields && <p className="muted full">This DocType has no editable fields.</p>}
        <button type="submit" hidden />
      </form>

      {doc && <AssignPanel doctype={meta.name} name={doc.name} canAssign={perms.write} />}
      {doc && <ActivityPanel meta={meta} childMetas={childMetas} name={doc.name} />}

      {!isNew && (
        <p className="muted meta-line">
          ID <code>{doc.name}</code> · created by {String(doc.owner ?? '')} · last modified by {String(doc.modified_by ?? '')}
          {' '}on {new Date(String(doc.modified)).toLocaleString()}
        </p>
      )}
    </>
  )
}
