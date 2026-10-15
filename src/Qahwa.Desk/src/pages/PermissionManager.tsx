import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import type { DocPerm, PermissionView } from '../api/types.ts'
import { PermissionsEditor } from '../designer/FieldCard.tsx'

const SOURCE_LABEL = { Core: 'Framework', App: 'App', Custom: 'Custom' } as const

/** The Role Permission Manager: pick a DocType on the left, edit who may do what (per field level) on the right. */
export function PermissionManagerPage() {
  const { name } = useParams()
  const [filter, setFilter] = useState('')
  const items = useQuery({ queryKey: ['permissions', 'list'], queryFn: api.permissions.list, retry: false })

  const shown = useMemo(
    () => (items.data ?? []).filter((i) => i.name.toLowerCase().includes(filter.trim().toLowerCase())),
    [items.data, filter],
  )

  if (items.isError) {
    const forbidden = items.error instanceof ApiError && items.error.status === 403
    return <div className="banner error" role="alert">
      {forbidden ? 'Only a System Manager can manage permissions.' : 'Could not load the DocTypes.'}
    </div>
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Role Permissions</h1>
          <p className="muted">Choose who can read, write, create and delete each DocType, and who may see or change its protected fields. Changes apply at once.</p>
        </div>
      </div>

      <div className="perm-layout">
        <aside>
          <input type="search" className="search" placeholder="Filter DocTypes…" aria-label="Filter DocTypes"
            value={filter} onChange={(e) => setFilter(e.target.value)} />
          <ul className="perm-list">
            {shown.map((item) => (
              <li key={item.name} className={item.name === name ? 'active' : undefined}>
                <Link to={`/permissions/${encodeURIComponent(item.name)}`}>
                  {item.name}
                  {item.customized && <span className="badge" title="Rules differ from the DocType's definition"> customised</span>}
                </Link>
              </li>
            ))}
            {items.isPending && <li className="muted">Loading…</li>}
            {items.data && shown.length === 0 && <li className="muted">No DocTypes match.</li>}
          </ul>
        </aside>
        <section>
          {name ? <PermissionEditor key={name} name={name} sourceLabel={(items.data ?? []).find((i) => i.name === name)?.source} /> :
            <p className="muted">Pick a DocType to see who can use it.</p>}
        </section>
      </div>
    </>
  )
}

function PermissionEditor({ name }: { name: string; sourceLabel?: keyof typeof SOURCE_LABEL }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const view = useQuery({ queryKey: ['permissions', 'view', name], queryFn: () => api.permissions.get(name), retry: false, staleTime: 0 })
  const roles = useQuery({
    queryKey: ['designer', 'roles'],
    queryFn: () => api.list('Role', { fields: ['name'], filters: [], orderBy: 'name', descending: false, limit: 200, offset: 0 }),
    retry: false,
  })

  if (view.isError) {
    const status = view.error instanceof ApiError ? view.error.status : 0
    return <div className="banner error" role="alert">
      {status === 404 ? 'This DocType has no permissions of its own.' : 'Could not load the permissions.'}
      {' '}<button className="btn small" onClick={() => navigate('/permissions')}>Back</button>
    </div>
  }
  if (!view.data) return <p className="muted" role="status">Loading…</p>

  return <Editor key={`${view.data.modified ?? 'default'}`} view={view.data}
    roles={(roles.data?.data ?? []).map((r) => r.name)} onSaved={(next) => {
      queryClient.setQueryData(['permissions', 'view', name], next)
      void queryClient.invalidateQueries({ queryKey: ['permissions', 'list'] })
      void queryClient.invalidateQueries({ queryKey: ['meta'] })
    }} />
}

function Editor({ view, roles, onSaved }: { view: PermissionView; roles: string[]; onSaved: (view: PermissionView) => void }) {
  const [rules, setRules] = useState<DocPerm[]>(() => view.rules.map((r) => ({ ...r, permLevel: r.permLevel ?? 0 })))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [banner, setBanner] = useState<{ text: string; kind: 'error' | 'ok' } | null>(null)

  const dirty = JSON.stringify(rules) !== JSON.stringify(view.rules.map((r) => ({ ...r, permLevel: r.permLevel ?? 0 })))
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const fail = (error: unknown) => {
    if (!(error instanceof ApiError)) return setBanner({ text: 'Something went wrong. Please try again.', kind: 'error' })
    setErrors(Object.fromEntries(error.details.filter((d) => d.field).map((d) => [d.field, d.message])))
    setBanner({
      text: error.status === 409 ? 'Someone else changed these permissions. Reload the page to see their version.' : error.message,
      kind: 'error',
    })
  }

  const save = useMutation({
    mutationFn: () => api.permissions.save(view.name, rules.filter((r) => r.role.trim() !== ''), view.modified),
    onSuccess: (next) => { setErrors({}); setBanner({ text: 'Saved. The new rules apply immediately.', kind: 'ok' }); onSaved(next) },
    onError: fail,
  })
  const reset = useMutation({
    mutationFn: () => api.permissions.reset(view.name),
    onSuccess: (next) => { setErrors({}); setBanner({ text: 'Back to the definition’s rules.', kind: 'ok' }); onSaved(next) },
    onError: fail,
  })

  const levels = [...new Set(view.fields.map((f) => f.level))].sort((a, b) => a - b)
  const disabled = !view.canEdit || save.isPending || reset.isPending

  return (
    <div>
      <div className="page-head">
        <h2>{view.name} <span className="badge">{SOURCE_LABEL[view.source]}</span>{view.customized && <span className="badge"> customised</span>}</h2>
        <span className="spacer" />
        {view.customized && view.canEdit && (
          <button className="btn" disabled={disabled}
            onClick={() => { if (window.confirm(`Throw away the customised rules of ${view.name} and use the DocType's own?`)) reset.mutate() }}>
            Reset to default
          </button>
        )}
        {view.canEdit && <button className="btn primary" disabled={!dirty || disabled} onClick={() => save.mutate()}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>}
      </div>

      {!view.canEdit && <div className="banner info" role="status">Only an Administrator can change the permissions of framework DocTypes.</div>}
      {banner && <div className={`banner ${banner.kind === 'ok' ? 'info' : 'error'}`} role={banner.kind === 'ok' ? 'status' : 'alert'}>{banner.text}</div>}

      <p className="muted">
        Level 0 is access to the document itself. A level above 0 protects the fields placed at that level: only roles with a rule at that level can see or
        change them. Administrators can always use everything.
      </p>
      <PermissionsEditor submittable={view.isSubmittable} permissions={rules} roles={roles} disabled={disabled} errors={errors} errorPrefix="rules" onChange={setRules} />

      {levels.length > 0 && (
        <div className="card">
          <h3>Fields by level</h3>
          <ul>
            {levels.map((level) => (
              <li key={level}><strong>Level {level}:</strong> {view.fields.filter((f) => f.level === level).map((f) => f.label || f.name).join(', ')}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
