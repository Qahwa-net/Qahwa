import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import { useAuth } from '../auth.tsx'

/** "Reports app wants to ...": the person decides. The address to go to next comes from the server, which has checked it against the registered ones. */
export function OAuthConsentPage() {
  const [params] = useSearchParams()
  const { user } = useAuth()
  const request = {
    clientId: params.get('client_id') ?? '',
    redirectUri: params.get('redirect_uri') ?? '',
    scope: params.get('scope') ?? '',
    state: params.get('state') ?? '',
    codeChallenge: params.get('code_challenge') ?? '',
    codeChallengeMethod: params.get('code_challenge_method') ?? '',
  }
  const consent = useQuery({ queryKey: ['oauth-consent', request], queryFn: () => api.oauth.consent(request), retry: false })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function decide(approve: boolean) {
    setBusy(true)
    setError(null)
    try {
      window.location.assign(await api.oauth.decide(request, approve))
    } catch (e) {
      setBusy(false)
      setError(e instanceof ApiError ? e.message : 'Something went wrong. Please try again.')
    }
  }

  if (consent.isPending) return <div className="center-screen" role="status">Loading…</div>
  if (consent.isError) {
    return (
      <main className="card narrow" style={{ margin: '3rem auto' }}>
        <h1>This request cannot be shown</h1>
        <p className="banner error" role="alert">{consent.error instanceof ApiError ? consent.error.message : 'The request is not valid.'}</p>
        <p className="muted">Nothing was shared. Go back to the application and try again, or ask whoever runs it to check its settings.</p>
      </main>
    )
  }

  const { clientName, redirectHost, scopes } = consent.data
  return (
    <main className="card narrow" style={{ margin: '3rem auto' }} aria-labelledby="oauth-title">
      <h1 id="oauth-title">{clientName} wants access to your account</h1>
      <p className="muted">You are signed in as <strong>{user?.full_name || user?.user}</strong>. After you decide, you will be sent to <strong>{redirectHost}</strong>.</p>
      <p>It would be allowed to:</p>
      <ul>{scopes.map((s) => <li key={s.name}>{s.description}</li>)}</ul>
      <p className="muted small-text">It can never do more than you can. You can take this back later in your account settings.</p>
      {error && <div className="banner error" role="alert">{error}</div>}
      <div className="row-actions">
        <button className="btn primary" disabled={busy} onClick={() => void decide(true)}>Allow</button>
        <button className="btn" disabled={busy} onClick={() => void decide(false)}>Deny</button>
      </div>
    </main>
  )
}
