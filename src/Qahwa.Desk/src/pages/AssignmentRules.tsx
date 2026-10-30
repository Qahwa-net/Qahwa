import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { AssignmentRule, AssignmentRuleSet, AssignmentRuleView, AssignmentStrategy } from '../api/types.ts'
import { Avatar, Icon, IconButton } from '../controls/Icon.tsx'
import { LinkSelect } from '../controls/LinkSelect.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { STRATEGIES, addUser, moveRule, newRule, toSet } from '../lib/assignmentRules.ts'
import { rowErrors } from '../lib/workflow.ts'
import { Select } from '../controls/Select.tsx'

/** Assignment Rules: who gets which documents, without anyone having to assign them by hand. */
export function AssignmentRulesPage() {
  const { name } = useParams()
  const [filter, setFilter] = useState('')
  const items = useQuery({ queryKey: ['assignment-rules', 'list'], queryFn: api.assignmentRules.list, retry: false })

  const shown = useMemo(
    () => (items.data ?? []).filter((i) => i.docType.toLowerCase().includes(filter.trim().toLowerCase())),
    [items.data, filter],
  )

  if (items.isError) {
    const forbidden = items.error instanceof ApiError && items.error.status === 403
    return <div className="banner error" role="alert">{forbidden ? 'Only a System Manager can manage assignment rules.' : 'Could not load the DocTypes.'}</div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Assignment Rules</h1>
          <p className="muted">Give new documents to people automatically. Rules are tried in order and the first one that applies assigns the document, once. Everyone who is assigned is told, follows the document and can open it.</p>
        </div>
      </div>
      <div className="perm-layout">
        <aside>
          <input type="search" className="search" placeholder="Filter DocTypes…" aria-label="Filter DocTypes"
            value={filter} onChange={(e) => setFilter(e.target.value)} />
          <ul className="perm-list">
            {shown.map((item) => (
              <li key={item.docType} className={item.docType === name ? 'active' : undefined}>
                <Link to={`/assignment-rules/${encodeURIComponent(item.docType)}`}>
                  {item.docType}
                  {item.hasRules && <span className="badge" title={`${item.rules} rule(s)`}>{item.activeRules > 0 ? ` ${item.activeRules} active` : ' off'}</span>}
                </Link>
              </li>
            ))}
            {items.isPending && <li className="muted">Loading…</li>}
            {items.data && shown.length === 0 && <li className="muted">No DocTypes match.</li>}
          </ul>
        </aside>
        <section>{name ? <Editor key={name} name={name} /> : <p className="muted">Pick a DocType to give it assignment rules.</p>}</section>
      </div>
    </>
  )
}

function Editor({ name }: { name: string }) {
  const view = useQuery({ queryKey: ['assignment-rules', 'view', name], queryFn: () => api.assignmentRules.get(name), retry: false, staleTime: 0 })
  if (view.isError) {
    const status = view.error instanceof ApiError ? view.error.status : 0
    return <div className="banner error" role="alert">{status === 404 ? 'Assignment rules cannot be set for this DocType.' : 'Could not load the rules.'}</div>
  }
  if (!view.data) return <p className="muted">Loading…</p>
  // Keyed by the saved version so the editor starts afresh after every save.
  return <EditorBody key={view.data.modified ?? 'new'} view={view.data} />
}

function EditorBody({ view }: { view: AssignmentRuleView }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [draft, setDraft] = useState<AssignmentRuleSet>(() => structuredClone(view.set))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; conflict: boolean } | null>(null)
  const [removing, setRemoving] = useState(false)
  const readOnly = !view.canEdit

  const save = useMutation({
    mutationFn: () => api.assignmentRules.save(view.docType, toSet(draft), view.modified),
    onSuccess: async (saved) => {
      queryClient.setQueryData(['assignment-rules', 'view', view.docType], saved)
      await queryClient.invalidateQueries({ queryKey: ['assignment-rules', 'list'] })
      toast('Rules saved. They apply to documents saved from now on.')
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner({ text: 'Could not save the rules.', conflict: false })
      setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
      const general = error.details.filter((d) => !d.field).map((d) => d.message).join(' ')
      setBanner({ text: general || (error.details.length > 0 ? 'Please fix the highlighted rules.' : error.message), conflict: error.status === 409 })
    },
  })
  const remove = useMutation({
    mutationFn: () => api.assignmentRules.remove(view.docType),
    onSuccess: async () => {
      setRemoving(false)
      toast('Rules removed.')
      await queryClient.invalidateQueries({ queryKey: ['assignment-rules'] })
    },
    onError: (error) => { setRemoving(false); setBanner({ text: error instanceof ApiError ? error.message : 'Could not remove the rules.', conflict: false }) },
  })

  const update = (index: number, patch: Partial<AssignmentRule>) =>
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
      {errors._ && <div className="banner error" role="alert">{errors._}</div>}
      {errors.rules && <div className="banner error" role="alert">{errors.rules}</div>}

      <ol className="rule-list">
        {draft.rules.map((rule, i) => (
          <RuleCard key={i} index={i} rule={rule} count={draft.rules.length} errors={rowErrors(errors, 'rules', i)} userFields={view.userFields} readOnly={readOnly}
            onChange={(patch) => update(i, patch)}
            onMove={(by) => setDraft({ ...draft, rules: moveRule(draft.rules, i, by) })}
            onRemove={() => setDraft({ ...draft, rules: draft.rules.filter((_, j) => j !== i) })} />
        ))}
      </ol>
      {draft.rules.length === 0 && <p className="empty-note">No rules yet. Documents of this DocType are only assigned by hand.</p>}

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
          message={`Documents of ${view.docType} will no longer be assigned automatically. Assignments already made stay.`} />
      )}
    </>
  )
}

function RuleCard({ index, rule, count, errors, userFields, readOnly, onChange, onMove, onRemove }: {
  index: number
  rule: AssignmentRule
  count: number
  errors: Record<string, string>
  userFields: string[]
  readOnly: boolean
  onChange: (patch: Partial<AssignmentRule>) => void
  onMove: (by: -1 | 1) => void
  onRemove: () => void
}) {
  const strategy = STRATEGIES.find((s) => s.value === rule.strategy)
  const Err = ({ k }: { k: string }) => (errors[k] ? <small className="error-text" role="alert">{errors[k]}</small> : null)
  const label = `Rule ${index + 1}`
  return (
    <li className={`card rule-card${rule.active ? '' : ' off'}${Object.keys(errors).length ? ' has-error' : ''}`} aria-label={rule.name || label}>
      <header className="rule-head">
        <span className="rule-no" aria-hidden="true">{index + 1}</span>
        <input aria-label={`${label} name`} className="rule-name" value={rule.name} disabled={readOnly} aria-invalid={Boolean(errors.name)} onChange={(e) => onChange({ name: e.target.value })} />
        <label className="switch"><input type="checkbox" aria-label={`${label} active`} checked={rule.active} disabled={readOnly} onChange={(e) => onChange({ active: e.target.checked })} /><span>Active</span></label>
        {!readOnly && (
          <span className="row-actions">
            <IconButton icon="arrowLeft" label="Try earlier" className="rot-up" disabled={index === 0} onClick={() => onMove(-1)} />
            <IconButton icon="arrowLeft" label="Try later" className="rot-down" disabled={index === count - 1} onClick={() => onMove(1)} />
            <IconButton icon="trash" label={`Remove ${rule.name || label}`} tone="danger" onClick={onRemove} />
          </span>
        )}
      </header>
      <Err k="name" />

      <div className="form-grid">
        <div className="field">
          <label>Choose the person by</label>
          <Select aria-label={`${label} strategy`} value={rule.strategy} disabled={readOnly} onChange={(e) => onChange({ strategy: e.target.value as AssignmentStrategy })}>
            {STRATEGIES.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
          </Select>
          <small className="muted">{strategy?.help}</small>
          <Err k="strategy" />
        </div>

        {rule.strategy === 'Field' ? (
          <div className="field">
            <label>Field holding the user</label>
            <Select aria-label={`${label} user field`} value={rule.userField ?? ''} disabled={readOnly} aria-invalid={Boolean(errors.userField)} onChange={(e) => onChange({ userField: e.target.value })}>
              <option value="">— choose —</option>
              {userFields.map((f) => <option key={f} value={f}>{f}</option>)}
            </Select>
            <Err k="userField" />
          </div>
        ) : (
          <div className="field">
            <label>People</label>
            <ul className="chip-list" aria-label={`${label} people`}>
              {rule.users.map((u) => (
                <li key={u} className="person-chip">
                  <Avatar name={u} size={22} /> <span>{u}</span>
                  {!readOnly && <button type="button" className="icon-btn" aria-label={`Remove ${u}`} onClick={() => onChange({ users: rule.users.filter((x) => x !== u) })}><Icon name="x" size={14} /></button>}
                </li>
              ))}
            </ul>
            {!readOnly && (
              <LinkSelect ariaLabel={`${label} add person`} placeholder="Add a person…" value="" cacheKey={['rule-users']}
                search={(text) => api.linkSearch('User', text)} onChange={(value) => { if (value) onChange({ users: addUser(rule.users, value) }) }} />
            )}
            <Err k="users" />
          </div>
        )}

        <div className="field full">
          <label htmlFor={`rule-${index}-cond`}>Applies when <span className="muted">(optional)</span></label>
          <input id={`rule-${index}-cond`} className="mono" placeholder="always  ·  e.g. urgency == 'High'" spellCheck={false} value={rule.condition ?? ''} disabled={readOnly}
            aria-invalid={Boolean(errors.condition)} onChange={(e) => onChange({ condition: e.target.value })} />
          <Err k="condition" />
        </div>
        <div className="field full">
          <label htmlFor={`rule-${index}-close`}>Withdraw the assignment when <span className="muted">(optional)</span></label>
          <input id={`rule-${index}-close`} className="mono" placeholder="never  ·  e.g. status == 'Resolved'" spellCheck={false} value={rule.closeCondition ?? ''} disabled={readOnly}
            aria-invalid={Boolean(errors.closeCondition)} onChange={(e) => onChange({ closeCondition: e.target.value })} />
          <Err k="closeCondition" />
        </div>
        <div className="field full">
          <label htmlFor={`rule-${index}-desc`}>What to tell them <span className="muted">(optional)</span></label>
          <input id={`rule-${index}-desc`} maxLength={500} value={rule.description ?? ''} disabled={readOnly} onChange={(e) => onChange({ description: e.target.value })} />
          <Err k="description" />
        </div>
        <div className="field">
          <label htmlFor={`rule-${index}-due`}>Due after (days)</label>
          <input id={`rule-${index}-due`} type="number" min={0} max={365} value={rule.dueInDays ?? ''} disabled={readOnly}
            onChange={(e) => onChange({ dueInDays: e.target.value === '' ? null : Number(e.target.value) })} />
          <Err k="dueInDays" />
        </div>
        <div className="field">
          <label htmlFor={`rule-${index}-prio`}>Priority</label>
          <Select id={`rule-${index}-prio`} value={rule.priority} disabled={readOnly} onChange={(e) => onChange({ priority: e.target.value as AssignmentRule['priority'] })}>
            <option>Low</option><option>Medium</option><option>High</option>
          </Select>
          <Err k="priority" />
        </div>
      </div>
    </li>
  )
}
