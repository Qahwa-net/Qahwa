import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { ConfirmDialog } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'
import type { OAuthClientInfo } from '../api/types.ts'

const SCOPES = [
  { name: 'profile', label: 'Name and email address' },
  { name: 'api', label: 'Read data through the API' },
  { name: 'api:write', label: 'Change data through the API' },
]

/** Applications that may ask people for access (OAuth). System Managers. The secret of a new confidential application is shown once. */
export function OAuthClientsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const clients = useQuery({ queryKey: ['oauth-clients'], queryFn: api.oauth.clients, retry: false })
  const [name, setName] = useState('')
  const [redirects, setRedirects] = useState('')
  const [scopes, setScopes] = useState<string[]>(['profile'])
  const [isPublic, setIsPublic] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [secret, setSecret] = useState<{ client: OAuthClientInfo; secret: string | null } | null>(null)
  const [removing, setRemoving] = useState<OAuthClientInfo | null>(null)
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong. Please try again.')

  const create = useMutation({
    mutationFn: () => api.oauth.createClient({ name, redirectUris: redirects, scopes, public: isPublic }),
    onSuccess: async (made) => { setError(null); setSecret({ client: made.info, secret: made.clientSecret }); setName(''); setRedirects(''); await queryClient.invalidateQueries({ queryKey: ['oauth-clients'] }) },
    onError: fail,
  })
  const remove = useMutation({
    mutationFn: (c: OAuthClientInfo) => api.oauth.deleteClient(c.id),
    onSuccess: async () => { setRemoving(null); toast('Removed'); await queryClient.invalidateQueries({ queryKey: ['oauth-clients'] }) },
    onError: (e) => { setRemoving(null); fail(e) },
  })

  if (clients.isError) return <div className="banner error" role="alert">You need the System Manager role to manage applications.</div>
  return (
    <>
      <div className="page-head">
        <div>
          <h1>OAuth applications</h1>
          <p className="muted">Programs and sites that may ask people to let them use their Qahwa account. They always use the authorization-code flow with PKCE.</p>
        </div>
      </div>
      {error && <div className="banner error" role="alert">{error}</div>}
      {secret && (
        <div className="banner info" role="status">
          <strong>{secret.client.name}</strong> was registered. Client ID: <code>{secret.client.clientId}</code>
          {secret.secret ? <> Client secret (shown once, copy it now): <code>{secret.secret}</code></> : ' It is a public client: it has no secret.'}
          {' '}<button className="btn small" onClick={() => setSecret(null)}>Done</button>
        </div>
      )}
      <form className="card form-stack" onSubmit={(e) => { e.preventDefault(); create.mutate() }} aria-label="Register an application">
        <label className="field"><span>Application name</span><input maxLength={100} value={name} onChange={(e) => setName(e.target.value)} /></label>
        <label className="field"><span>Redirect addresses (one per line; https, or http://localhost for programs on a computer)</span>
          <textarea rows={3} value={redirects} onChange={(e) => setRedirects(e.target.value)} /></label>
        <fieldset className="field"><legend>May ask for</legend>
          {SCOPES.map((s) => (
            <label key={s.name} className="check-line"><input type="checkbox" checked={scopes.includes(s.name)}
              onChange={(e) => setScopes(e.target.checked ? [...scopes, s.name] : scopes.filter((x) => x !== s.name))} /> {s.label}</label>
          ))}
        </fieldset>
        <label className="check-line"><input type="checkbox" checked={isPublic} onChange={(e) => setIsPublic(e.target.checked)} /> Public client (a mobile or browser app that cannot keep a secret)</label>
        <div><button className="btn primary" type="submit" disabled={create.isPending || name.trim() === '' || redirects.trim() === '' || scopes.length === 0}>Register</button></div>
      </form>
      {clients.data && clients.data.length === 0 && <p className="empty-note">No applications are registered.</p>}
      <ul className="repeat-page-list">
        {(clients.data ?? []).map((c) => (
          <li key={c.id} className="card">
            <p><strong>{c.name}</strong> <small className="muted">· {c.clientId} · {c.public ? 'public' : 'confidential'} · {c.scopes.join(', ')}</small></p>
            <p className="muted small-text">Redirects to {c.redirectUris.join(', ')}</p>
            <div className="row-actions"><button className="btn small danger" onClick={() => setRemoving(c)}>Remove</button></div>
          </li>
        ))}
      </ul>
      {removing && (
        <ConfirmDialog title={`Remove ${removing.name}?`} tone="danger" confirmLabel="Remove" busy={remove.isPending} onClose={() => setRemoving(null)}
          onConfirm={() => remove.mutate(removing)} message="It stops working at once, and every access it was given is withdrawn." />
      )}
    </>
  )
}
