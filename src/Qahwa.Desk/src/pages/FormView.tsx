import { UserPasswordActions, TestEmailAction, TestIncomingEmailAction } from './UserPasswordActions.tsx'
import { DocumentActionFields } from './DocumentActionFields.tsx'
import { WorkflowBar } from './WorkflowBar.tsx'
import { DocRail } from './DocRail.tsx'
import { buildLayout } from '../lib/layout.ts'
import { useLinkedDocs } from '../lib/useLinkedDocs.ts'
import { useToast } from '../controls/Toast.tsx'
import { FormSkeleton } from '../controls/Skeleton.tsx'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { ActivityPanel } from './ActivityPanel.tsx'
import { AssignDialog } from './AssignPanel.tsx'
import { RepeatDialog } from './RepeatDialog.tsx'
import { EmailDialog } from './EmailDialog.tsx'
import { PrintDialog } from './PrintDialog.tsx'
import { FollowButton } from './FollowButton.tsx'
import { LikeButton } from './LikeButton.tsx'
import { RenameDialog } from './RenamePanel.tsx'
import { ShareDialog } from './SharePanel.tsx'
import { useEffect, useMemo, useRef, useState } from 'react'
import { clearDraft, draftKey, loadDraft, saveDraft } from '../lib/draft.ts'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useMatch, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { Doc, DocField, DocTypeMeta, Permissions } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { liveAction } from '../lib/realtime.ts'
import { useLive } from '../lib/useLive.ts'
import { FieldControl, type InputValue } from '../controls/FieldControl.tsx'
import { TableControl } from '../controls/TableControl.tsx'
import { DocContext } from '../controls/DocContext.tsx'
import { fromInput, hasColumn, isEditable, toInput } from '../lib/fields.ts'
import {
  applyLogic, documentContext, expressionDefaults, fetchTargets, fieldState, rowContext, rowExpressionDefaults,
  type FieldState, type Session,
} from '../lib/logic.ts'
import { scriptDoc } from '../lib/clientScripts.ts'
import { copyOf } from '../lib/duplicate.ts'
import { useClientScripts } from '../lib/useClientScripts.ts'
import { emptyRow, rowsChanged, rowsFromDoc, rowsToPayload, tableFieldsOf, type TableRow } from '../lib/tables.ts'

type Values = Record<string, InputValue>

/** A client script refused the save (its message is shown as it is). */
class ScriptRefusal extends Error {}
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
  const [params] = useSearchParams()
  // "Duplicate" opens a new form filled in from this document (nothing is saved until Save).
  const copyFrom = isNew ? params.get('copy') : null
  const copyQuery = useQuery({ queryKey: ['doc', doctype, copyFrom], queryFn: () => api.get(doctype, copyFrom!), enabled: copyFrom !== null, staleTime: 0, retry: false })

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

  if (!metaQuery.data || (!isNew && !docQuery.data) || (copyFrom !== null && copyQuery.isPending && !copyQuery.isError)) return <FormSkeleton />

  const doc = isNew ? null : docQuery.data!
  return (
    <FormBody
      // A fresh form state whenever the saved document changes (after save, reload or conflict).
      key={`${doctype}/${doc?.name ?? 'new'}/${String(doc?.modified ?? '')}/${copyFrom ?? ''}`}
      meta={metaQuery.data.data}
      perms={metaQuery.data.permissions}
      childMetas={metaQuery.data.children ?? {}}
      writableLevels={metaQuery.data.writableLevels}
      doc={doc}
      template={copyQuery.data ? copyOf(metaQuery.data.data, metaQuery.data.children ?? {}, copyQuery.data) : null}
    />
  )
}

function FormBody({ meta, perms, childMetas, writableLevels, doc, template }: {
  meta: DocTypeMeta
  perms: Permissions
  childMetas: Record<string, DocTypeMeta>
  writableLevels?: number[]
  doc: Doc | null
  /** A new form filled in from another document (Duplicate). */
  template?: Doc | null
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
  const original = useMemo(() => initialValues(meta, doc ?? template ?? null), [meta, doc, template])
  const originalTables = useMemo(() => initialTables(meta, childMetas, doc ?? template ?? null), [meta, childMetas, doc, template])

  // What the user typed. Calculated values are derived from it below and never stored.
  const [values, setValues] = useState<Values>(() => (isNew && !template ? expressionDefaults(meta, childMetas, original, session) : original))
  const [tables, setTables] = useState<Tables>(originalTables)
  const [promptName, setPromptName] = useState('')
  const [sharing, setSharing] = useState(false)
  const [renaming, setRenaming] = useState(false)
  const [assigning, setAssigning] = useState(false)
  const [repeating, setRepeating] = useState(false)
  const [emailing, setEmailing] = useState(false)
  const [printing, setPrinting] = useState(false)
  const [activeTab, setActiveTab] = useState(0)
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({})
  const toast = useToast()
  const [confirm, setConfirm] = useState<'submit' | 'cancel' | 'delete' | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; conflict: boolean } | null>(null)

  // lookup(): the linked documents formulas and rules read, fetched as a Link field is chosen.
  const linked = useLinkedDocs(meta, childMetas, values, tables)
  const liveSession = useMemo<Session>(() => ({ ...session, linked }), [session, linked])
  const view = useMemo(() => applyLogic(meta, childMetas, values, tables, liveSession), [meta, childMetas, values, tables, liveSession])
  const docContext = documentContext(meta, childMetas, view.values, view.tables, liveSession)
  const latestValues = useRef(view.values)
  latestValues.current = view.values
  const scripts = useClientScripts({
    meta,
    getDoc: () => scriptDoc(meta, latestValues.current),
    values: view.values,
    applyValue: (field, value) => setValues((v) => ({ ...v, [field.name]: toInput(field, value) })),
    version: doc ? String(doc.modified ?? '') : null,
  })
  const stateOf = (field: DocField): FieldState => {
    const base = hasLogic(field) ? fieldState(field, docContext) : staticState(field)
    const o = scripts.overrides[field.name]
    return o ? { ...base, active: o.hidden ? false : base.active, required: o.reqd ?? base.required, readOnly: o.read_only ?? base.readOnly } : base
  }

  const changedNames = Object.keys(view.values).filter((key) => view.values[key] !== original[key] && canWriteField(meta.fields.find((f) => f.name === key)!))
  const changedTables = tableDefs.filter(({ field }) => canWriteField(field) && rowsChanged(originalTables[field.name] ?? [], view.tables[field.name] ?? []))
  const dirty = isNew ? true : changedNames.length > 0 || changedTables.length > 0

  // Unsaved typing is kept in this browser (ADR-109) and offered back after a reload or a lost tab.
  const draftId = draftKey(meta.name, doc?.name ?? null)
  const draftVersion = doc ? String(doc.modified ?? '') : ''
  const secretFields = useMemo(() => new Set(meta.fields.filter((f) => f.type === 'Password').map((f) => f.name)), [meta])
  const startValues = useRef(values)
  const [restored, setRestored] = useState(false)
  useEffect(() => {
    if (!editable) return
    const draft = loadDraft<Values, Tables>(window.localStorage, draftId, draftVersion, Date.now())
    if (draft) {
      setValues((v) => ({ ...v, ...draft.values }))
      setTables(draft.tables ?? originalTables)
      setRestored(true)
    }
    // Only when the form opens on a document: later edits are written by the effect below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draftId])
  const hasEdits = isNew ? Object.keys(values).some((n) => values[n] !== startValues.current[n]) || changedTables.length > 0 : dirty
  useEffect(() => {
    if (!editable) return
    const timer = window.setTimeout(() => {
      if (hasEdits) saveDraft(window.localStorage, draftId, { values, tables, modified: draftVersion, at: Date.now() }, secretFields)
      else clearDraft(window.localStorage, draftId)
    }, 800)
    return () => window.clearTimeout(timer)
  }, [editable, hasEdits, values, tables, draftId, draftVersion, secretFields])

  // Someone else saved or deleted this document while it is open: show it, or warn when there are unsaved edits (never replace them).
  useLive((event) => {
    const action = liveAction(event, user?.user, dirty)
    if (action.kind === 'refresh') {
      void queryClient.invalidateQueries({ queryKey: ['doc', meta.name, doc?.name] })
      toast(`Updated by ${action.by}`)
    } else if (action.kind === 'warn') {
      setBanner({ text: `${action.by} changed this document. Your edits are not saved yet; load the latest to see their changes.`, conflict: true })
    } else if (action.kind === 'deleted') {
      setBanner({ text: `${action.by} deleted this document.`, conflict: false })
    }
  }, meta.name, doc?.name, !isNew && doc !== undefined && doc !== null)

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
      const refusal = await scripts.beforeSave()
      if (refusal) throw new ScriptRefusal(refusal)
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
      toast(isNew ? 'Created' : 'Saved')
      clearDraft(window.localStorage, draftId)
      setRestored(false)
      setFieldErrors({})
      setBanner(null)
      await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
      void queryClient.invalidateQueries({ queryKey: ['activity', meta.name, saved.name] })
      queryClient.setQueryData(['doc', meta.name, saved.name], saved)
      if (isNew) navigate(`${base}/${encodeURIComponent(saved.name)}`, { replace: true })
    },
    onError: (error) => {
      if (error instanceof ScriptRefusal) return setBanner({ text: error.message, conflict: false })
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

  const amend = useMutation({
    mutationFn: () => api.amend(meta.name, doc!.name),
    onSuccess: async (created) => {
      toast('Amended: a new draft was made')
      await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
      navigate(`${base}/${encodeURIComponent(created.name)}`)
    },
    onError: (error) => setBanner({ text: error instanceof ApiError ? error.message : 'Could not amend this document.', conflict: false }),
  })

  const transition = useMutation({
    mutationFn: (kind: 'submit' | 'cancel') => (kind === 'submit' ? api.submit : api.cancel)(meta.name, doc!.name, doc!.modified),
    onSuccess: async (saved, kind) => {
      toast(kind === 'submit' ? 'Submitted' : 'Cancelled')
      setFieldErrors({})
      setBanner(null)
      await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
      void queryClient.invalidateQueries({ queryKey: ['activity', meta.name, saved.name] })
      queryClient.setQueryData(['doc', meta.name, saved.name], saved)
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner({ text: 'Could not complete this. Please try again.', conflict: false })
      const general = error.details.map((d) => d.message).join(' ')
      setBanner({ text: general || error.message, conflict: error.status === 409 })
    },
  })

  const restore = useMutation({
    mutationFn: () => api.restoreDeleted(doc!.name),
    onSuccess: async (restored) => {
      toast('Restored')
      await queryClient.invalidateQueries({ queryKey: ['list'] })
      navigate(`/app/${encodeURIComponent(String(doc!.deleted_doctype))}/${encodeURIComponent(restored)}`, { replace: true })
    },
    onError: (error) =>
      setBanner({ text: error instanceof ApiError ? error.message : 'Could not restore.', conflict: false }),
  })
  const remove = useMutation({
    mutationFn: () => api.remove(meta.name, doc!.name),
    onSuccess: async () => {
      toast('Deleted')
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

  // Ctrl+S (or Cmd+S) saves, as in any editor.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
        event.preventDefault()
        if (dirty && editable && !save.isPending) save.mutate()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  })

  const title = isNew ? `New ${meta.name}` : String((meta.titleField ? doc[meta.titleField] : null) || doc.name)
  const hasFields = meta.fields.some((f) => hasColumn(f.type) || f.type === 'SectionBreak' || f.type === 'Table')

  const shownInForm = (f: DocField) => !f.hidden && stateOf(f).active && (f.type !== 'Table' || tableDefs.some((t) => t.field.name === f.name))
  const layout = buildLayout(meta.fields, shownInForm)
  // Errors come keyed by field (or `table[row].field`); show which tab holds them and jump there when saving fails.
  const errorKeys = new Set(Object.entries(fieldErrors).filter(([, message]) => message).map(([path]) => path.split(/[[.]/)[0]!))
  const tabHasError = (tab: (typeof layout)[number]) => tab.sections.some((sec) => sec.fields.some((f) => errorKeys.has(f.name)))
  const current = Math.min(activeTab, Math.max(layout.length - 1, 0))
  useEffect(() => {
    const first = layout.findIndex(tabHasError)
    if (first >= 0 && !tabHasError(layout[current]!)) setActiveTab(first)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fieldErrors])

  const renderField = (field: DocField) => {
          if (field.type === 'Table') {
            const def = tableDefs.find((t) => t.field.name === field.name)
            if (!def || field.hidden) return null
            const state = stateOf(field)
            if (!state.active) return null

            const rowState = (row: TableRow, f: DocField): FieldState =>
              hasLogic(f) ? fieldState(f, rowContext(def.child, row.values, view.values, meta, liveSession)) : staticState(f)
            const makeRow = (): TableRow => {
              const row = emptyRow(def.child)
              return { ...row, values: rowExpressionDefaults(def.child, row.values, view.values, meta, liveSession) }
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
            <div key={field.name} style={{ display: 'contents' }}><FieldControl field={field} state={stateOf(field)} siblings={view.values}
              value={view.values[field.name] ?? ''} readOnly={!canWriteField(field)} error={fieldErrors[field.name]}
              onChange={(v) => {
                // A document chosen for a Dynamic Link belongs to the old DocType: clear it when the DocType changes.
                const dependants = Object.fromEntries(meta.fields.filter((f) => f.type === 'DynamicLink' && f.options === field.name).map((f) => [f.name, '']))
                setValues({ ...values, ...dependants, [field.name]: v })
                if (fieldErrors[field.name]) setFieldErrors({ ...fieldErrors, [field.name]: '' })
                if (field.type === 'Link') fetchForDocument(field, String(v))
              }} />
            {meta.name === 'Document Action' && ['event', 'watch_field', 'target_field', 'value', 'mapping'].includes(field.name) && stateOf(field).active &&
              <DocumentActionFields field={field.name} values={view.values} readOnly={!canWriteField(field) || stateOf(field).readOnly}
                onChange={v => { setValues({ ...values, [field.name]: v }); clearErrorsFor(field.name) }} />}
            </div>
          )
  }

  return (
    <>
      <header className="doc-bar">
        <div className="doc-title">
          {!meta.isSingle && (
            <Link to={base} className="icon-btn" aria-label={`Back to ${meta.name} list`} data-tip={`Back to ${meta.name}`}><Icon name="arrowLeft" /></Link>
          )}
          <div className="doc-heading">
            <p className="doc-kicker">{meta.name}</p>
            <h1>{title}</h1>
          </div>
          {!isNew && meta.isSubmittable && <span className={`badge status-${docstatus}`}>{['Draft', 'Submitted', 'Cancelled'][docstatus]}</span>}
          {!isNew && dirty && <span className="badge unsaved">Not saved</span>}
        </div>
        <div className="doc-actions">
          {!isNew && doc && user?.roles.some(r => r === 'Administrator' || r === 'System Manager') && meta.name === 'User' && <UserPasswordActions key={doc.name} email={doc.name} />}
          {!isNew && user?.roles.some(r => r === 'Administrator' || r === 'System Manager') && meta.name === 'Email Account' && <TestEmailAction disabled={dirty} />}
          {!isNew && user?.roles.some(r => r === 'Administrator' || r === 'System Manager') && meta.name === 'Email Account' && <TestIncomingEmailAction disabled={dirty} />}
          {!isNew && doc && meta.module !== 'Core' && !meta.isSingle && <LikeButton doctype={meta.name} name={doc.name} />}
          {!isNew && doc && <FollowButton doctype={meta.name} name={doc.name} />}
          {!isNew && !meta.isSingle && meta.allowCopy !== false && perms.create && doc && <IconButton icon="copy" label="Duplicate" onClick={() => navigate(`${base}/new?copy=${encodeURIComponent(doc.name)}`)} />}
          {!isNew && !meta.isSingle && perms.write && docstatus !== 2 && <IconButton icon="userPlus" label="Assign" onClick={() => setAssigning(true)} />}
          {!isNew && !meta.isSingle && perms.write && perms.share !== false && docstatus !== 2 && <IconButton icon="share" label="Share" onClick={() => setSharing(true)} />}
          {!isNew && perms.read && perms.print !== false && <IconButton icon="printer" label="Print" onClick={() => setPrinting(true)} />}
          {!isNew && !meta.isSingle && perms.write && perms.email !== false && docstatus !== 2 && <IconButton icon="mail" label="Email" onClick={() => setEmailing(true)} />}
          {!isNew && !meta.isSingle && perms.create && meta.module !== 'Core' && meta.autoname !== 'prompt' && <IconButton icon="repeat" label="Repeat" onClick={() => setRepeating(true)} />}
          {!isNew && meta.allowRename && perms.write && docstatus !== 1 && <IconButton icon="pencil" label="Rename" onClick={() => setRenaming(true)} />}
          {!isNew && meta.name === 'Deleted Document' && perms.delete && <IconButton icon="undo" label="Restore" tone="primary" disabled={restore.isPending} onClick={() => restore.mutate()} />}
          {!isNew && !meta.isSingle && perms.delete && docstatus !== 1 && <IconButton icon="trash" label="Delete" tone="danger" disabled={remove.isPending} onClick={() => setConfirm('delete')} />}
          {!isNew && !flow && docstatus === 0 && meta.isSubmittable && perms.submit && (
            <IconButton icon="checkCircle" label={dirty ? 'Save your changes before submitting' : 'Submit'} tone="primary" disabled={dirty || transition.isPending} onClick={() => setConfirm('submit')} />
          )}
          {!isNew && !flow && docstatus === 1 && perms.cancel && (
            <IconButton icon="ban" label={dirty ? 'Save your changes before cancelling' : 'Cancel document'} tone="danger" disabled={dirty || transition.isPending} onClick={() => setConfirm('cancel')} />
          )}
          {scripts.buttons.map((b) => <button key={b.id} type="button" className="btn" onClick={() => scripts.runButton(b.id)}>{b.label}</button>)}
          {editable && docstatus !== 2 && (
            <button className="btn primary save-btn" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>
              <Icon name="save" /> <span className="btn-label">{save.isPending ? 'Saving…' : 'Save'}</span>
            </button>
          )}
        </div>
      </header>

      {flow && doc && <WorkflowBar doctype={meta.name} doc={doc} status={flow} disabled={dirty} onError={(text, conflict) => setBanner({ text, conflict })} />}

      {renaming && doc && <RenameDialog doctype={meta.name} name={doc.name} onClose={() => setRenaming(false)}
        onRenamed={async (newName) => {
          await queryClient.invalidateQueries({ queryKey: ['list', meta.name] })
          queryClient.removeQueries({ queryKey: ['doc', meta.name, doc.name] })
          navigate(`${base}/${encodeURIComponent(newName)}`, { replace: true })
        }} />}
      {sharing && doc && <ShareDialog doctype={meta.name} name={doc.name} onClose={() => setSharing(false)} />}
      {printing && doc && <PrintDialog doctype={meta.name} name={doc.name} onClose={() => setPrinting(false)} />}
      {emailing && doc && <EmailDialog doctype={meta.name} name={doc.name} onClose={() => setEmailing(false)} />}
      {repeating && doc && <RepeatDialog meta={meta} name={doc.name} onClose={() => setRepeating(false)} />}
      {assigning && doc && <AssignDialog doctype={meta.name} name={doc.name} canAssign={perms.write} onClose={() => setAssigning(false)} />}
      {confirm && doc && (
        <ConfirmDialog
          title={confirm === 'submit' ? 'Submit this document?' : confirm === 'cancel' ? 'Cancel this document?' : 'Delete this document?'}
          confirmLabel={confirm === 'submit' ? 'Submit' : confirm === 'cancel' ? 'Cancel document' : 'Delete'}
          tone={confirm === 'submit' ? 'primary' : 'danger'}
          busy={transition.isPending || remove.isPending}
          message={confirm === 'submit' ? `${meta.name} "${doc.name}" will be locked afterwards.`
            : confirm === 'cancel' ? `${meta.name} "${doc.name}" cannot be changed afterwards.`
            : `${meta.name} "${doc.name}" will be deleted. This cannot be undone.`}
          onClose={() => setConfirm(null)}
          onConfirm={() => {
            const kind = confirm
            setConfirm(null)
            if (kind === 'delete') remove.mutate()
            else transition.mutate(kind)
          }} />
      )}
      {scripts.intro && <div className="banner info" role="status">{scripts.intro}</div>}
      {scripts.notice && <div className="banner info" role="status">{scripts.notice} <button type="button" className="btn small" onClick={scripts.dismissNotice}>OK</button></div>}
      {restored && <div className="banner info" role="status">Your unsaved changes were restored. <button type="button" className="btn small" onClick={() => { clearDraft(window.localStorage, draftId); setValues(startValues.current); setTables(originalTables); setRestored(false) }}>Discard them</button></div>}
      {docstatus === 2 && <div className="banner info" role="status">This document is cancelled and cannot be changed.{meta.isSubmittable && perms.create && perms.amend !== false && <> <button type="button" className="btn small" disabled={amend.isPending} onClick={() => amend.mutate()}>Amend</button></>}</div>}
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

      <div className={doc ? 'doc-layout' : undefined}>
      <div className="doc-main">
      <DocContext.Provider value={{ doctype: meta.name, name: doc?.name ?? null }}>
      <form className="form-stack" onSubmit={(e) => { e.preventDefault(); if (dirty && editable) save.mutate() }}>
        {isNew && meta.autoname === 'prompt' && (
          <section className="card form-section">
            <div className="field">
              <label htmlFor="prompt-name">Name <span className="req">*</span></label>
              <input id="prompt-name" value={promptName} onChange={(e) => setPromptName(e.target.value)} aria-invalid={Boolean(fieldErrors.name)} />
              {fieldErrors.name && <small className="error-text" role="alert">{fieldErrors.name}</small>}
            </div>
          </section>
        )}
        {layout.length > 1 && (
          <div className="tabs" role="tablist" aria-label="Form sections"
            onKeyDown={(e) => {
              if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return
              e.preventDefault()
              const next = (current + (e.key === 'ArrowRight' ? 1 : layout.length - 1)) % layout.length
              setActiveTab(next)
              document.getElementById(`form-tab-${next}`)?.focus()
            }}>
            {layout.map((tab, i) => (
              <button key={i} type="button" role="tab" id={`form-tab-${i}`} aria-selected={i === current} aria-controls={`form-panel-${i}`}
                tabIndex={i === current ? 0 : -1} className={i === current ? 'on' : undefined} onClick={() => setActiveTab(i)}>
                {tab.label}{tabHasError(tab) && <span className="tab-dot" role="img" aria-label="has errors" />}
              </button>
            ))}
          </div>
        )}
        {layout.map((tab, ti) => (
          <div key={ti} role={layout.length > 1 ? 'tabpanel' : undefined} id={`form-panel-${ti}`} className="form-stack" hidden={ti !== current}>
            {tab.sections.map((section, si) => {
              const id = `${ti}:${si}`
              const closed = Boolean(section.label) && Boolean(collapsed[id]) && !section.fields.some((f) => errorKeys.has(f.name))
              return (
                <section key={si} className="card form-section" aria-label={section.label || undefined}>
                  {section.label && (
                    <h2 className="section-toggle">
                      <button type="button" aria-expanded={!closed} aria-controls={`form-section-${id}`} onClick={() => setCollapsed({ ...collapsed, [id]: !closed })}>
                        <span className={closed ? 'chev shut' : 'chev'}><Icon name="chevronDown" size={16} /></span>
                        {section.label}
                      </button>
                    </h2>
                  )}
                  {section.columns.length > 1 ? (
                    <div id={`form-section-${id}`} className="form-columns" style={{ gridTemplateColumns: `repeat(${section.columns.length}, minmax(0, 1fr))` }} hidden={closed}>
                      {section.columns.map((column, ci) => <div key={ci} className="form-column">{column.map(renderField)}</div>)}
                    </div>
                  ) : (
                    <div id={`form-section-${id}`} className="form-grid" hidden={closed}>
                      {section.fields.map(renderField)}
                    </div>
                  )}
                </section>
              )
            })}
          </div>
        ))}
        {!hasFields && <p className="muted full">This DocType has no editable fields.</p>}
        <button type="submit" hidden />
      </form>
      </DocContext.Provider>

      {doc && <ActivityPanel meta={meta} childMetas={childMetas} name={doc.name} />}
      </div>
      {doc && <DocRail doctype={meta.name} doc={doc} trackViews={meta.trackViews} canAssign={perms.write && docstatus !== 2 && !meta.isSingle} canShare={perms.write && !meta.isSingle && docstatus !== 2}
        onAssign={() => setAssigning(true)} onShare={() => setSharing(true)} />}
      </div>
    </>
  )
}
