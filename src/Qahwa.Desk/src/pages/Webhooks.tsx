import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { Webhook, WebhookEvent, WebhookSet, WebhookView } from '../api/types.ts'
import { Icon, IconButton } from '../controls/Icon.tsx'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import { EVENTS, newHook, randomSecret, toSet, urlHint } from '../lib/webhooks.ts'
import { rowErrors } from '../lib/workflow.ts'
import { Select } from '../controls/Select.tsx'

/** Webhooks: tell other systems when documents change, with a signed JSON post. */
export function WebhooksPage() {
  const { name } = useParams()
  const [filter, setFilter] = useState('')
  const items = useQuery({ queryKey: ['webhooks', 'list'], queryFn: api.webhooks.list, retry: false })
  const shown = useMemo(() => (items.data ?? []).filter((i) => i.docType.toLowerCase().includes(filter.trim().toLowerCase())), [items.data, filter])

  if (items.isError) {
    const forbidden = items.error instanceof ApiError && items.error.status === 403
    return <div className="banner error" role="alert">{forbidden ? 'Only a System Manager can manage webhooks.' : 'Could not load the DocTypes.'}</div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Webhooks</h1>
          <p className="muted">Post a JSON message to another system when documents are created, changed, submitted, cancelled or deleted. Every post is signed with your secret so the receiver can check it came from here. Deliveries (and any failures) are listed under <Link to="/app/Webhook%20Delivery">Webhook Delivery</Link>.</p>
        </div>
      </div>
      <div className="perm-layout">
        <aside>
          <input type="search" className="search" placeholder="Filter DocTypes…" aria-label="Filter DocTypes" value={filter} onChange={(e) => setFilter(e.target.value)} />
          <ul className="perm-list">
            {shown.map((item) => (
              <li key={item.docType} className={item.docType === name ? 'active' : undefined}>
                <Link to={`/webhooks/${encodeURIComponent(item.docType)}`}>
                  {item.docType}
                  {item.hasHooks && <span className="badge" title={`${item.hooks} webhook(s)`}>{item.activeHooks > 0 ? ` ${item.activeHooks} active` : ' off'}</span>}
                </Link>
              </li>
            ))}
            {items.isPending && <li className="muted">Loading…</li>}
          </ul>
        </aside>
        <section>{name ? <Editor key={name} name={name} /> : <p className="muted">Pick a DocType to give it webhooks.</p>}</section>
      </div>
    </>
  )
}

function Editor({ name }: { name: string }) {
  const view = useQuery({ queryKey: ['webhooks', 'view', name], queryFn: () => api.webhooks.get(name), retry: false, staleTime: 0 })
  if (view.isError) return <div className="banner error" role="alert">{view.error instanceof ApiError && view.error.status === 404 ? 'Webhooks cannot be set for this DocType.' : 'Could not load the webhooks.'}</div>
  if (!view.data) return <p className="muted">Loading…</p>
  return <Body key={view.data.modified ?? 'new'} view={view.data} />
}

function Body({ view }: { view: WebhookView }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [draft, setDraft] = useState<WebhookSet>(() => structuredClone(view.set))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; conflict: boolean } | null>(null)
  const [removing, setRemoving] = useState(false)
  const readOnly = !view.canEdit

  const save = useMutation({
    mutationFn: () => api.webhooks.save(view.docType, toSet(draft), view.modified),
    onSuccess: async (saved) => {
      queryClient.setQueryData(['webhooks', 'view', view.docType], saved)
      await queryClient.invalidateQueries({ queryKey: ['webhooks', 'list'] })
      toast('Webhooks saved')
    },
    onError: (error) => {
      if (!(error instanceof ApiError)) return setBanner({ text: 'Could not save the webhooks.', conflict: false })
      setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
      const general = error.details.filter((d) => !d.field).map((d) => d.message).join(' ')
      setBanner({ text: general || (error.details.length > 0 ? 'Please fix the highlighted webhooks.' : error.message), conflict: error.status === 409 })
    },
  })
  const remove = useMutation({
    mutationFn: () => api.webhooks.remove(view.docType),
    onSuccess: async () => { setRemoving(false); toast('Webhooks removed'); await queryClient.invalidateQueries({ queryKey: ['webhooks'] }) },
    onError: () => setRemoving(false),
  })
  const update = (i: number, patch: Partial<Webhook>) => setDraft({ ...draft, hooks: draft.hooks.map((h, j) => (j === i ? { ...h, ...patch } : h)) })

  return (
    <>
      <h2>{view.docType}</h2>
      {banner && <div className="banner error" role="alert">{banner.text}{banner.conflict && <> <button className="link-button" onClick={() => window.location.reload()}>Reload</button></>}</div>}
      {errors.hooks && <div className="banner error" role="alert">{errors.hooks}</div>}
      <ol className="rule-list">
        {draft.hooks.map((hook, i) => {
          const e = rowErrors(errors, 'hooks', i)
          const hint = urlHint(hook.url)
          const label = `Webhook ${i + 1}`
          return (
            <li key={i} className={`card rule-card${hook.active ? '' : ' off'}${Object.keys(e).length ? ' has-error' : ''}`}>
              <header className="rule-head">
                <span className="rule-no" aria-hidden="true">{i + 1}</span>
                <input aria-label={`${label} name`} className="rule-name" value={hook.name} disabled={readOnly} aria-invalid={Boolean(e.name)} onChange={(ev) => update(i, { name: ev.target.value })} />
                <label className="switch"><input type="checkbox" aria-label={`${label} active`} checked={hook.active} disabled={readOnly} onChange={(ev) => update(i, { active: ev.target.checked })} /><span>Active</span></label>
                {!readOnly && <IconButton icon="trash" label={`Remove ${hook.name}`} tone="danger" onClick={() => setDraft({ ...draft, hooks: draft.hooks.filter((_, j) => j !== i) })} />}
              </header>
              {e.name && <small className="error-text" role="alert">{e.name}</small>}
              <div className="form-grid">
                <div className="field">
                  <label>When</label>
                  <Select aria-label={`${label} event`} value={hook.event} disabled={readOnly} onChange={(ev) => update(i, { event: ev.target.value as WebhookEvent })}>
                    {EVENTS.map((ev) => <option key={ev.value} value={ev.value}>{ev.label}</option>)}
                  </Select>
                  {e.event && <small className="error-text" role="alert">{e.event}</small>}
                </div>
                <div className="field">
                  <label htmlFor={`wh-${i}-cond`}>Only when <span className="muted">(optional)</span></label>
                  <input id={`wh-${i}-cond`} className="mono" spellCheck={false} placeholder="always  ·  e.g. amount > 1000" value={hook.condition ?? ''} disabled={readOnly} aria-invalid={Boolean(e.condition)} onChange={(ev) => update(i, { condition: ev.target.value })} />
                  {e.condition && <small className="error-text" role="alert">{e.condition}</small>}
                </div>
                <div className="field full">
                  <label htmlFor={`wh-${i}-url`}>Post to</label>
                  <input id={`wh-${i}-url`} type="url" inputMode="url" placeholder="https://example.com/hooks/qahwa" value={hook.url} disabled={readOnly} aria-invalid={Boolean(e.url)} onChange={(ev) => update(i, { url: ev.target.value })} />
                  {(e.url ?? hint) && <small className="error-text" role={e.url ? 'alert' : 'status'}>{e.url ?? hint}</small>}
                </div>
                <div className="field full">
                  <label htmlFor={`wh-${i}-secret`}>Secret</label>
                  <div className="filter-row preview-row">
                    <input id={`wh-${i}-secret`} type="password" autoComplete="new-password" placeholder={hook.secretSet ? 'A secret is set. Leave blank to keep it.' : 'At least 16 characters'}
                      value={hook.secret ?? ''} disabled={readOnly} aria-invalid={Boolean(e.secret)} onChange={(ev) => update(i, { secret: ev.target.value })} />
                    {!readOnly && <button type="button" className="btn small" onClick={() => update(i, { secret: randomSecret() })}>Make one</button>}
                  </div>
                  <small className="muted">Each post carries <code>X-Qahwa-Signature: t=…,v1=…</code>, the HMAC-SHA256 of the timestamp, a dot and the body. After saving the secret cannot be shown again. A secret you generated is visible only until you save: copy it first.</small>
                  {hook.secret && <input aria-label={`${label} secret (to copy)`} readOnly className="mono" value={hook.secret} onFocus={(ev) => ev.currentTarget.select()} />}
                  {e.secret && <small className="error-text" role="alert">{e.secret}</small>}
                </div>
                <fieldset className="field full check-group">
                  <legend>Fields to send <span className="muted">(none ticked = all fields most people can see)</span></legend>
                  <div className="check-row">
                    {view.fields.map((f) => (
                      <label key={f} className="switch">
                        <input type="checkbox" checked={hook.fields.includes(f)} disabled={readOnly} onChange={(ev) => update(i, { fields: ev.target.checked ? [...hook.fields, f] : hook.fields.filter((x) => x !== f) })} />
                        <span>{f}</span>
                      </label>
                    ))}
                  </div>
                  {e.fields && <small className="error-text" role="alert">{e.fields}</small>}
                </fieldset>
              </div>
            </li>
          )
        })}
      </ol>
      {draft.hooks.length === 0 && <p className="empty-note">No webhooks yet.</p>}
      {!readOnly && (
        <div className="form-actions">
          <button className="btn" onClick={() => setDraft({ ...draft, hooks: [...draft.hooks, newHook(draft.hooks)] })}><Icon name="plus" /> Add webhook</button>
          <span className="spacer" />
          {view.exists && <button className="btn danger" onClick={() => setRemoving(true)}><Icon name="trash" /> Remove all</button>}
          <button className="btn primary" disabled={save.isPending} onClick={() => { setBanner(null); setErrors({}); save.mutate() }}><Icon name="save" /> {save.isPending ? 'Saving…' : 'Save webhooks'}</button>
        </div>
      )}
      {removing && <ConfirmDialog title="Remove all webhooks?" tone="danger" confirmLabel="Remove" busy={remove.isPending} onClose={() => setRemoving(false)} onConfirm={() => remove.mutate()}
        message={`Other systems will no longer be told about ${view.docType} documents. Deliveries already queued are marked failed.`} />}
    </>
  )
}
