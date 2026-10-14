import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useMatch, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { Doc, DocTypeMeta, Permissions } from '../api/types.ts'
import { FieldControl, type InputValue } from '../controls/FieldControl.tsx'
import { fromInput, hasColumn, isEditable, toInput } from '../lib/fields.ts'

type Values = Record<string, InputValue>

function initialValues(meta: DocTypeMeta, doc: Doc | null): Values {
  const values: Values = {}
  for (const field of meta.fields.filter((f) => isEditable(f.type))) {
    if (doc) values[field.name] = toInput(field, doc[field.name])
    else if (field.type === 'Check') values[field.name] = field.default === '1' || field.default === 'true'
    else values[field.name] = field.default ?? ''
  }
  return values
}

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
      doc={doc}
    />
  )
}

function FormBody({ meta, perms, doc }: { meta: DocTypeMeta; perms: Permissions; doc: Doc | null }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const isNew = doc === null
  const editable = isNew ? perms.create : perms.write
  const base = `/app/${encodeURIComponent(meta.name)}`

  const original = useMemo(() => initialValues(meta, doc), [meta, doc])
  const [values, setValues] = useState<Values>(original)
  const [promptName, setPromptName] = useState('')
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; conflict: boolean } | null>(null)

  const changedNames = Object.keys(values).filter((key) => values[key] !== original[key])
  const dirty = isNew ? true : changedNames.length > 0

  // Warn before leaving the page with edits that are not saved.
  useEffect(() => {
    if (!dirty || isNew) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty, isNew])

  const save = useMutation({
    mutationFn: async () => {
      const send = (names: string[]) =>
        Object.fromEntries(names.map((n) => [n, fromInput(meta.fields.find((f) => f.name === n)!, values[n]!)]))

      if (isNew) {
        const filled = Object.keys(values).filter((n) => values[n] !== '')
        return api.create(meta.name, { ...send(filled), ...(meta.autoname === 'prompt' ? { name: promptName } : {}) })
      }
      return api.update(meta.name, doc.name, { ...send(changedNames), modified: doc.modified })
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
      const shown = Object.keys(byField).length > 0 && general.length === 0
        ? 'Please fix the highlighted fields.'
        : general[0] ?? error.message
      setBanner({ text: shown, conflict: error.status === 409 && error.code === 'conflict' && !isNew })
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

  const title = isNew ? `New ${meta.name}` : String((meta.titleField ? doc[meta.titleField] : null) || doc.name)
  const hasFields = meta.fields.some((f) => hasColumn(f.type) || f.type === 'SectionBreak')

  return (
    <>
      <div className="page-head">
        <div>
          <Link to={base} className="muted back">← {meta.name}</Link>
          <h1>{title} {!isNew && dirty && <span className="badge">Not saved</span>}</h1>
        </div>
        <span className="spacer" />
        {!isNew && perms.delete && (
          <button className="btn danger" disabled={remove.isPending}
            onClick={() => { if (window.confirm(`Delete ${meta.name} "${doc.name}"? This cannot be undone.`)) remove.mutate() }}>
            Delete
          </button>
        )}
        {editable && (
          <button className="btn primary" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        )}
      </div>

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
        {meta.fields.map((field) => (
          <FieldControl key={field.name} field={field}
            value={values[field.name] ?? ''} readOnly={!editable} error={fieldErrors[field.name]}
            onChange={(v) => { setValues({ ...values, [field.name]: v }); if (fieldErrors[field.name]) setFieldErrors({ ...fieldErrors, [field.name]: '' }) }} />
        ))}
        {!hasFields && <p className="muted full">This DocType has no editable fields.</p>}
        <button type="submit" hidden />
      </form>

      {!isNew && (
        <p className="muted meta-line">
          ID <code>{doc.name}</code> · created by {String(doc.owner ?? '')} · last modified by {String(doc.modified_by ?? '')}
          {' '}on {new Date(String(doc.modified)).toLocaleString()}
        </p>
      )}
    </>
  )
}
