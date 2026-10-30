import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { NotificationEvent, NotificationRule, NotificationRuleSet, NotificationRuleView } from '../api/types.ts'
import { ChipPicker } from '../controls/ChipPicker.tsx'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { EVENTS, SPECIAL_NAMES, isTimeBased, newRule, toSet, unknownPlaceholders, withPlaceholder } from '../lib/notificationRules.ts'
import { moveRule } from '../lib/assignmentRules.ts'
import { rowErrors } from '../lib/workflow.ts'
import { Select } from '../controls/Select.tsx'

/** Notification Rules: who is told what when something happens to a document. */
export function NotificationRulesPage() {
  const { name } = useParams()
  const [filter, setFilter] = useState('')
  const items = useQuery({ queryKey: ['notification-rules', 'list'], queryFn: api.notificationRules.list, retry: false })

  const shown = useMemo(
    () => (items.data ?? []).filter((i) => i.docType.toLowerCase().includes(filter.trim().toLowerCase())),
    [items.data, filter],
  )

  if (items.isError) {
    const forbidden = items.error instanceof ApiError && items.error.status === 403
    return <div className="banner error" role="alert">{forbidden ? 'Only a System Manager can manage notification rules.' : 'Could not load the DocTypes.'}</div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Notification Rules</h1>
          <p className="muted">Tell people when something happens to a document: it is created or changed, a field changes, it is submitted, or a date is near. They see it in their notification bell, and only if they are allowed to open the document.</p>
        </div>
      </div>
      <div className="perm-layout">
        <aside>
          <input type="search" className="search" placeholder="Filter DocTypes…" aria-label="Filter DocTypes"
            value={filter} onChange={(e) => setFilter(e.target.value)} />
          <ul className="perm-list">
            {shown.map((item) => (
              <li key={item.docType} className={item.docType === name ? 'active' : undefined}>
                <Link to={`/notification-rules/${encodeURIComponent(item.docType)}`}>
                  {item.docType}
                  {item.hasRules && <span className="badge" title={`${item.rules} rule(s)`}>{item.activeRules > 0 ? ` ${item.activeRules} active` : ' off'}</span>}
                </Link>
              </li>
            ))}
            {items.isPending && <li className="muted">Loading…</li>}
            {items.data && shown.length === 0 && <li className="muted">No DocTypes match.</li>}
          </ul>
        </aside>
        <section>{name ? <Editor key={name} name={name} /> : <p className="muted">Pick a DocType to give it notification rules.</p>}</section>
      </div>
    </>
  )
}

function Editor({ name }: { name: string }) {
  const view = useQuery({ queryKey: ['notification-rules', 'view', name], queryFn: () => api.notificationRules.get(name), retry: false, staleTime: 0 })
  if (view.isError) {
    const status = view.error instanceof ApiError ? view.error.status : 0
    return <div className="banner error" role="alert">{status === 404 ? 'Notification rules cannot be set for this DocType.' : 'Could not load the rules.'}</div>
  }
  if (!view.data) return <p className="muted">Loading…</p>
  return <EditorBody key={view.data.modified ?? 'new'} view={view.data} />
}

function EditorBody({ view }: { view: NotificationRuleView }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [draft, setDraft] = useState<NotificationRuleSet>(() => structuredClone(view.set))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; conflict: boolean } | null>(null)
  const [removing, setRemoving] = useState(false)
  const readOnly = !view.canEdit

  const save = useMutation({
    mutationFn: () => api.notificationRules.save(view.docType, toSet(draft), view.modified),
    onSuccess: async (saved) => {
      queryClient.setQueryData(['notification-rules', 'view', view.docType], saved)
      await queryClient.invalidateQueries({ queryKey: ['notification-rules', 'list'] })
      toast('Rules saved. They apply from now on.')
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner({ text: 'Could not save the rules.', conflict: false })
      setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
      const general = error.details.filter((d) => !d.field).map((d) => d.message).join(' ')
      setBanner({ text: general || (error.details.length > 0 ? 'Please fix the highlighted rules.' : error.message), conflict: error.status === 409 })
    },
  })
  const remove = useMutation({
    mutationFn: () => api.notificationRules.remove(view.docType),
    onSuccess: async () => { setRemoving(false); toast('Rules removed.'); await queryClient.invalidateQueries({ queryKey: ['notification-rules'] }) },
    onError: (error) => { setRemoving(false); setBanner({ text: error instanceof ApiError ? error.message : 'Could not remove the rules.', conflict: false }) },
  })

  const update = (index: number, patch: Partial<NotificationRule>) =>
    setDraft({ ...draft, rules: draft.rules.map((r, i) => (i === index ? { ...r, ...patch } : r)) })

  return (
    <>
      <h2>{view.docType}</h2>
      {readOnly && <div className="banner info" role="status">You cannot change these rules.</div>}
      {banner && (
        <div className="banner error" role="alert">
          {banner.text}
          {banner.conflict && <> <button className="link-button" onClick={() => window.location.reload()}>Reload</button></>}
        </div>
      )}
      {errors.rules && <div className="banner error" role="alert">{errors.rules}</div>}

      <ol className="rule-list">
        {draft.rules.map((rule, i) => (
          <RuleCard key={i} index={i} rule={rule} count={draft.rules.length} errors={rowErrors(errors, 'rules', i)} view={view} readOnly={readOnly}
            onChange={(patch) => update(i, patch)}
            onMove={(by) => setDraft({ ...draft, rules: moveRule(draft.rules, i, by) })}
            onRemove={() => setDraft({ ...draft, rules: draft.rules.filter((_, j) => j !== i) })} />
        ))}
      </ol>
      {draft.rules.length === 0 && <p className="empty-note">No rules yet. Nobody is told automatically about {view.docType} documents.</p>}

      {!readOnly && (
        <div className="form-actions">
          <button className="btn" onClick={() => setDraft({ ...draft, rules: [...draft.rules, newRule(draft.rules)] })}><Icon name="plus" /> Add rule</button>
          <span className="spacer" />
          {view.exists && <button className="btn danger" disabled={remove.isPending} onClick={() => setRemoving(true)}><Icon name="trash" /> Remove all</button>}
          <button className="btn primary" disabled={save.isPending} onClick={() => { setBanner(null); setErrors({}); save.mutate() }}>
            <Icon name="save" /> {save.isPending ? 'Saving…' : 'Save rules'}
          </button>
        </div>
      )}
      {removing && (
        <ConfirmDialog title="Remove all rules?" tone="danger" confirmLabel="Remove" busy={remove.isPending} onClose={() => setRemoving(false)} onConfirm={() => remove.mutate()}
          message={`Nobody will be told automatically about ${view.docType} documents any more. Notifications already sent stay.`} />
      )}
    </>
  )
}

function RuleCard({ index, rule, count, errors, view, readOnly, onChange, onMove, onRemove }: {
  index: number
  rule: NotificationRule
  count: number
  errors: Record<string, string>
  view: NotificationRuleView
  readOnly: boolean
  onChange: (patch: Partial<NotificationRule>) => void
  onMove: (by: -1 | 1) => void
  onRemove: () => void
}) {
  const label = `Rule ${index + 1}`
  const event = EVENTS.find((e) => e.value === rule.event)
  const known = [...view.fields, ...SPECIAL_NAMES]
  const unknown = unknownPlaceholders(rule.message, known)
  const Err = ({ k }: { k: string }) => (errors[k] ? <small className="error-text" role="alert">{errors[k]}</small> : null)

  return (
    <li className={`card rule-card${rule.active ? '' : ' off'}${Object.keys(errors).length ? ' has-error' : ''}`} aria-label={rule.name || label}>
      <header className="rule-head">
        <span className="rule-no" aria-hidden="true">{index + 1}</span>
        <input aria-label={`${label} name`} className="rule-name" value={rule.name} disabled={readOnly} aria-invalid={Boolean(errors.name)} onChange={(e) => onChange({ name: e.target.value })} />
        <label className="switch"><input type="checkbox" aria-label={`${label} active`} checked={rule.active} disabled={readOnly} onChange={(e) => onChange({ active: e.target.checked })} /><span>Active</span></label>
        {!readOnly && (
          <span className="row-actions">
            <IconButton icon="arrowLeft" label="Move up" className="rot-up" disabled={index === 0} onClick={() => onMove(-1)} />
            <IconButton icon="arrowLeft" label="Move down" className="rot-down" disabled={index === count - 1} onClick={() => onMove(1)} />
            <IconButton icon="trash" label={`Remove ${rule.name || label}`} tone="danger" onClick={onRemove} />
          </span>
        )}
      </header>
      <Err k="name" />

      <div className="form-grid">
        <div className="field">
          <label>When</label>
          <Select aria-label={`${label} event`} value={rule.event} disabled={readOnly} onChange={(e) => onChange({ event: e.target.value as NotificationEvent })}>
            {EVENTS.map((e) => <option key={e.value} value={e.value}>{e.label}</option>)}
          </Select>
          <small className="muted">{event?.help}</small>
          <Err k="event" />
        </div>

        {rule.event === 'ValueChange' && (
          <div className="field">
            <label>Field</label>
            <Select aria-label={`${label} field`} value={rule.field ?? ''} disabled={readOnly} aria-invalid={Boolean(errors.field)} onChange={(e) => onChange({ field: e.target.value })}>
              <option value="">— choose —</option>
              {view.fields.map((f) => <option key={f} value={f}>{f}</option>)}
            </Select>
            <Err k="field" />
          </div>
        )}
        {isTimeBased(rule.event) && (
          <>
            <div className="field">
              <label>Date field</label>
              <Select aria-label={`${label} date field`} value={rule.dateField ?? ''} disabled={readOnly} aria-invalid={Boolean(errors.dateField)} onChange={(e) => onChange({ dateField: e.target.value })}>
                <option value="">— choose —</option>
                {view.dateFields.map((f) => <option key={f} value={f}>{f}</option>)}
              </Select>
              <Err k="dateField" />
            </div>
            <div className="field">
              <label htmlFor={`nr-${index}-days`}>Days {rule.event === 'DaysBefore' ? 'before' : 'after'}</label>
              <input id={`nr-${index}-days`} type="number" min={0} max={365} value={rule.days ?? ''} disabled={readOnly}
                onChange={(e) => onChange({ days: e.target.value === '' ? null : Number(e.target.value) })} />
              <Err k="days" />
            </div>
          </>
        )}

        <div className="field full">
          <label htmlFor={`nr-${index}-cond`}>Only when <span className="muted">(optional)</span></label>
          <input id={`nr-${index}-cond`} className="mono" placeholder="always  ·  e.g. amount > 1000" spellCheck={false} value={rule.condition ?? ''} disabled={readOnly}
            aria-invalid={Boolean(errors.condition)} onChange={(e) => onChange({ condition: e.target.value })} />
          <Err k="condition" />
        </div>

        <div className="field full">
          <label>Tell these people</label>
          <ChipPicker doctype="User" values={rule.users} onChange={(users) => onChange({ users })} label={`${label} people`} placeholder="Add a person…" readOnly={readOnly} avatars />
          <Err k="users" />
        </div>
        <div className="field">
          <label>Everyone with these roles</label>
          <ChipPicker doctype="Role" values={rule.roles} onChange={(roles) => onChange({ roles })} label={`${label} roles`} placeholder="Add a role…" readOnly={readOnly} />
          <Err k="roles" />
        </div>
        <div className="field">
          <label>The people named in these fields</label>
          <div className="check-group">
            {view.userFields.map((f) => (
              <label key={f} className="switch">
                <input type="checkbox" checked={rule.userFields.includes(f)} disabled={readOnly}
                  onChange={(e) => onChange({ userFields: e.target.checked ? [...rule.userFields, f] : rule.userFields.filter((x) => x !== f) })} />
                <span>{f}</span>
              </label>
            ))}
            {view.userFields.length === 0 && <small className="muted">This DocType has no field that holds a user.</small>}
          </div>
          <Err k="userFields" />
        </div>
        <div className="field full">
          <div className="check-row">
            <label className="switch"><input type="checkbox" checked={rule.notifyOwner} disabled={readOnly} onChange={(e) => onChange({ notifyOwner: e.target.checked })} /><span>The person who created it</span></label>
            <label className="switch"><input type="checkbox" checked={rule.notifyFollowers} disabled={readOnly} onChange={(e) => onChange({ notifyFollowers: e.target.checked })} /><span>Everyone following it</span></label>
            <label className="switch"><input type="checkbox" checked={rule.email} disabled={readOnly} onChange={(e) => onChange({ email: e.target.checked })} /><span>Also send an email</span></label>
          </div>
        </div>

        <div className="field full">
          <label htmlFor={`nr-${index}-msg`}>Message</label>
          <input id={`nr-${index}-msg`} maxLength={300} value={rule.message} disabled={readOnly} aria-invalid={Boolean(errors.message)} placeholder="e.g. {{user}} changed {{name}}"
            onChange={(e) => onChange({ message: e.target.value })} />
          {!readOnly && (
            <Select aria-label={`${label} insert field`} className="compact" value="" onChange={(e) => { if (e.target.value) onChange({ message: withPlaceholder(rule.message, e.target.value) }) }}>
              <option value="">Insert a field…</option>
              {[...SPECIAL_NAMES, ...view.fields].map((f) => <option key={f} value={f}>{f}</option>)}
            </Select>
          )}
          {unknown.length > 0 && !errors.message && <small className="error-text" role="status">Not a field: {unknown.join(', ')}</small>}
          <Err k="message" />
        </div>
      </div>
    </li>
  )
}
