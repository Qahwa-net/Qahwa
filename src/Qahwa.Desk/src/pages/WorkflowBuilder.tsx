import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { IndicatorColor, WorkflowDefinition, WorkflowView } from '../api/types.ts'
import {
  DOCSTATUS_LABELS, STATE_COLORS, draftFrom, newState, newTransition, parseRoles, removeState, renameState, rowErrors, toDefinition, unreachableStates,
} from '../lib/workflow.ts'
import { Select } from '../controls/Select.tsx'

/** Workflow Builder: who moves a document from which state to which, and under what condition. */
export function WorkflowBuilderPage() {
  const { name } = useParams()
  const [filter, setFilter] = useState('')
  const items = useQuery({ queryKey: ['workflows', 'list'], queryFn: api.workflows.list, retry: false })

  const shown = useMemo(
    () => (items.data ?? []).filter((i) => i.docType.toLowerCase().includes(filter.trim().toLowerCase())),
    [items.data, filter],
  )

  if (items.isError) {
    const forbidden = items.error instanceof ApiError && items.error.status === 403
    return <div className="banner error" role="alert">{forbidden ? 'Only a System Manager can build workflows.' : 'Could not load the DocTypes.'}</div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Workflow Builder</h1>
          <p className="muted">Define the states a document goes through, who may move it on, and when. The first state is where new documents start. The state lives in a text or Select field: choose one, or let the builder create a "Workflow State" field for you (more in <Link to="/designer">the DocType Designer</Link>).</p>
        </div>
      </div>
      <div className="perm-layout">
        <aside>
          <input type="search" className="search" placeholder="Filter DocTypes…" aria-label="Filter DocTypes"
            value={filter} onChange={(e) => setFilter(e.target.value)} />
          <ul className="perm-list">
            {shown.map((item) => (
              <li key={item.docType} className={item.docType === name ? 'active' : undefined}>
                <Link to={`/workflows/${encodeURIComponent(item.docType)}`}>
                  {item.docType}
                  {item.hasWorkflow && <span className="badge" title={`${item.states} state(s)`}>{item.active ? ' active' : ' off'}</span>}
                </Link>
              </li>
            ))}
            {items.isPending && <li className="muted">Loading…</li>}
            {items.data && shown.length === 0 && <li className="muted">No DocTypes match.</li>}
          </ul>
        </aside>
        <section>{name ? <WorkflowEditor key={name} name={name} /> : <p className="muted">Pick a DocType to give it a workflow.</p>}</section>
      </div>
    </>
  )
}

function WorkflowEditor({ name }: { name: string }) {
  const [notice, setNotice] = useState<string | null>(null)
  const view = useQuery({ queryKey: ['workflows', 'view', name], queryFn: () => api.workflows.get(name), retry: false, staleTime: 0 })

  if (view.isError) {
    const status = view.error instanceof ApiError ? view.error.status : 0
    return <div className="banner error" role="alert">{status === 404 ? 'This DocType was not found.' : 'Could not load the workflow.'}</div>
  }
  if (!view.data) return <p className="muted">Loading…</p>
  // Keyed by the saved version so the editor starts afresh after every save.
  return <EditorBody key={view.data.modified ?? 'new'} view={view.data} notice={notice} setNotice={setNotice} />
}

function EditorBody({ view, notice, setNotice }: { view: WorkflowView; notice: string | null; setNotice: (text: string | null) => void }) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<WorkflowDefinition>(() => draftFrom(view))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; conflict: boolean } | null>(null)
  const readOnly = !view.canEdit
  const stateNames = draft.states.map((s) => s.name)
  const orphans = unreachableStates(draft)

  const save = useMutation({
    mutationFn: () => api.workflows.save(view.docType, toDefinition(draft), view.modified),
    onSuccess: async (saved) => {
      queryClient.setQueryData(['workflows', 'view', view.docType], saved)
      await queryClient.invalidateQueries({ queryKey: ['workflows', 'list'] })
      setNotice('Workflow saved. It applies to documents straight away.')
    },
    onError: (error) => {
      setNotice(null)
      if (!(error instanceof ApiError)) return setBanner({ text: 'Could not save the workflow.', conflict: false })
      setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
      const general = error.details.filter((d) => !d.field).map((d) => d.message).join(' ')
      setBanner({ text: general || (error.details.length > 0 ? 'Please fix the highlighted rows.' : error.message), conflict: error.status === 409 })
    },
  })

  // Adds (or reuses) the field that holds the state, so a DocType does not have to be changed elsewhere first.
  const createField = useMutation({
    mutationFn: () => api.workflows.createStateField(view.docType),
    onSuccess: ({ field, data }) => {
      queryClient.setQueryData(['workflows', 'view', view.docType], { ...data, workflow: view.workflow, modified: view.modified })
      setDraft((d) => ({ ...d, stateField: field }))
      setErrors((e) => { const { stateField: _, ...rest } = e; return rest })
      setBanner(null)
      setNotice(`The field "${field}" is ready and chosen as the state field. Save the workflow to keep it.`)
    },
    onError: (error) => {
      setNotice(null)
      setBanner({ text: error instanceof ApiError ? (error.details[0]?.message ?? error.message) : 'Could not create the field.', conflict: false })
    },
  })

  const remove = useMutation({
    mutationFn: () => api.workflows.remove(view.docType),
    onSuccess: async () => {
      setNotice('Workflow removed.')
      await queryClient.invalidateQueries({ queryKey: ['workflows'] })
    },
    onError: (error) => setBanner({ text: error instanceof ApiError ? error.message : 'Could not remove the workflow.', conflict: false }),
  })

  const set = (next: WorkflowDefinition) => { setDraft(next); setNotice(null) }
  const updateState = (i: number, patch: Partial<WorkflowDefinition['states'][number]>) =>
    set({ ...draft, states: draft.states.map((s, j) => (j === i ? { ...s, ...patch } : s)) })
  const updateTransition = (i: number, patch: Partial<WorkflowDefinition['transitions'][number]>) =>
    set({ ...draft, transitions: draft.transitions.map((t, j) => (j === i ? { ...t, ...patch } : t)) })

  return (
    <>
      <h2>{view.docType} {view.exists && <span className="badge">{draft.active ? 'active' : 'switched off'}</span>}</h2>
      {readOnly && <div className="banner info" role="status">Only an Administrator can change the workflow of a framework DocType.</div>}
      {notice && <div className="banner ok" role="status">{notice}</div>}
      {banner && (
        <div className="banner error" role="alert">
          {banner.text}
          {banner.conflict && <> <button className="link-button" onClick={() => window.location.reload()}>Reload</button></>}
        </div>
      )}
      {errors._ && <div className="banner error" role="alert">{errors._}</div>}

      <div className="card form-grid">
        <label className="field">
          <span>State field</span>
          <Select value={draft.stateField} disabled={readOnly} aria-invalid={Boolean(errors.stateField)}
            onChange={(e) => set({ ...draft, stateField: e.target.value })}>
            <option value="">— choose —</option>
            {view.stateFields.map((f) => <option key={f} value={f}>{f}</option>)}
          </Select>
          <small className="muted">The field that holds the state. Only the workflow can change it.</small>
          {!readOnly && (
            <button type="button" className="link-button" disabled={createField.isPending} onClick={() => createField.mutate()}>
              {createField.isPending ? 'Creating…' : '+ Create a new "Workflow State" field'}
            </button>
          )}
          {errors.stateField && <small className="error-text" role="alert">{errors.stateField}</small>}
        </label>
        <label className="field check">
          <input type="checkbox" checked={draft.active} disabled={readOnly} onChange={(e) => set({ ...draft, active: e.target.checked })} />
          <span>Active</span>
        </label>
      </div>

      <h3>States</h3>
      <div className="table-wrap">
        <table aria-label="States">
          <thead><tr><th>State</th><th>Colour</th><th>Document status</th><th>Roles who can edit</th><th /></tr></thead>
          <tbody>
            {draft.states.map((s, i) => {
              const e = rowErrors(errors, 'states', i)
              return (
                <tr key={i}>
                  <td>
                    <input aria-label={`State ${i + 1} name`} value={s.name} disabled={readOnly} aria-invalid={Boolean(e.name)}
                      onChange={(ev) => set(renameState(draft, i, ev.target.value))} />
                    {i === 0 && <small className="muted"> start</small>}
                    {(e.name ?? e._) && <small className="error-text" role="alert">{e.name ?? e._}</small>}
                  </td>
                  <td>
                    <Select aria-label={`State ${i + 1} colour`} value={s.color} disabled={readOnly}
                      onChange={(ev) => updateState(i, { color: ev.target.value as IndicatorColor })}>
                      {STATE_COLORS.map((c) => <option key={c} value={c}>{c}</option>)}
                    </Select>
                    <span className={`pill pill-${s.color}`}>{s.name || '…'}</span>
                  </td>
                  <td>
                    <Select aria-label={`State ${i + 1} document status`} value={s.docStatus} disabled={readOnly}
                      onChange={(ev) => updateState(i, { docStatus: Number(ev.target.value) })}>
                      {DOCSTATUS_LABELS.map((l, n) => <option key={n} value={n}>{l}</option>)}
                    </Select>
                    {e.docStatus && <small className="error-text" role="alert">{e.docStatus}</small>}
                  </td>
                  <td>
                    <input aria-label={`State ${i + 1} edit roles`} placeholder="anyone with write access" disabled={readOnly}
                      defaultValue={s.editRoles.join(', ')} onBlur={(ev) => updateState(i, { editRoles: parseRoles(ev.target.value) })} />
                    {e.editRoles && <small className="error-text" role="alert">{e.editRoles}</small>}
                  </td>
                  <td>{!readOnly && <button className="link-button" aria-label={`Remove state ${s.name || i + 1}`} onClick={() => set(removeState(draft, i))}>Remove</button>}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {!readOnly && <button className="btn" onClick={() => set({ ...draft, states: [...draft.states, newState()] })}>Add state</button>}
      {orphans.length > 0 && <p className="muted" role="status">No transition uses: {orphans.join(', ')}.</p>}

      <h3>Transitions</h3>
      <datalist id="workflow-states">{stateNames.filter(Boolean).map((n) => <option key={n} value={n} />)}</datalist>
      <div className="table-wrap">
        <table aria-label="Transitions">
          <thead><tr><th>From</th><th>Action</th><th>To</th><th>Roles</th><th>Condition</th><th>Creator may</th><th /></tr></thead>
          <tbody>
            {draft.transitions.map((t, i) => {
              const e = rowErrors(errors, 'transitions', i)
              return (
                <tr key={i}>
                  <td><input aria-label={`Transition ${i + 1} from`} list="workflow-states" value={t.from} disabled={readOnly} aria-invalid={Boolean(e.from)}
                    onChange={(ev) => updateTransition(i, { from: ev.target.value })} />
                    {e.from && <small className="error-text" role="alert">{e.from}</small>}</td>
                  <td><input aria-label={`Transition ${i + 1} action`} value={t.action} disabled={readOnly} aria-invalid={Boolean(e.action)}
                    onChange={(ev) => updateTransition(i, { action: ev.target.value })} />
                    {(e.action ?? e._) && <small className="error-text" role="alert">{e.action ?? e._}</small>}</td>
                  <td><input aria-label={`Transition ${i + 1} to`} list="workflow-states" value={t.to} disabled={readOnly} aria-invalid={Boolean(e.to)}
                    onChange={(ev) => updateTransition(i, { to: ev.target.value })} />
                    {e.to && <small className="error-text" role="alert">{e.to}</small>}</td>
                  <td><input aria-label={`Transition ${i + 1} roles`} placeholder="Role, Role" disabled={readOnly} aria-invalid={Boolean(e.roles)}
                    defaultValue={t.roles.join(', ')} onBlur={(ev) => updateTransition(i, { roles: parseRoles(ev.target.value) })} />
                    {e.roles && <small className="error-text" role="alert">{e.roles}</small>}</td>
                  <td>
                    <input className="mono" aria-label={`Transition ${i + 1} condition`} placeholder="always" spellCheck={false}
                      value={t.condition ?? ''} disabled={readOnly} aria-invalid={Boolean(e.condition)}
                      onChange={(ev) => updateTransition(i, { condition: ev.target.value })} />
                    {e.condition && <small className="error-text" role="alert">{e.condition}</small>}
                  </td>
                  <td><input type="checkbox" aria-label={`Transition ${i + 1} creator may do this`} checked={t.allowSelfApproval} disabled={readOnly}
                    onChange={(ev) => updateTransition(i, { allowSelfApproval: ev.target.checked })} /></td>
                  <td>{!readOnly && <button className="link-button" aria-label={`Remove transition ${i + 1}`} onClick={() => set({ ...draft, transitions: draft.transitions.filter((_, j) => j !== i) })}>Remove</button>}</td>
                </tr>
              )
            })}
            {draft.transitions.length === 0 && <tr><td colSpan={7} className="empty">No transitions yet: documents cannot leave their first state.</td></tr>}
          </tbody>
        </table>
      </div>
      {!readOnly && (
        <button className="btn" onClick={() => set({ ...draft, transitions: [...draft.transitions, newTransition(stateNames[0] ?? '', stateNames[1] ?? '')] })}>
          Add transition
        </button>
      )}

      {!readOnly && (
        <div className="form-actions">
          <button className="btn primary" disabled={save.isPending} onClick={() => { setBanner(null); setErrors({}); save.mutate() }}>
            {save.isPending ? 'Saving…' : 'Save workflow'}
          </button>
          {view.exists && (
            <button className="btn danger" disabled={remove.isPending}
              onClick={() => { if (window.confirm(`Remove the workflow of ${view.docType}? Documents keep the state they are in, but can be moved freely again.`)) remove.mutate() }}>
              Remove workflow
            </button>
          )}
        </div>
      )}
    </>
  )
}
