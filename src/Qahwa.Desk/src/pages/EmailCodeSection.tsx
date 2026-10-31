import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { cleanCode } from '../lib/twofactor.ts'

/** Sign in with a code sent by email, for people who have no authenticator app. Turning it on or off is confirmed with a mailed code. */
export function EmailCodeSection() {
  const queryClient = useQueryClient()
  const app = useQuery({ queryKey: ['two-factor'], queryFn: api.twoFactor.status, retry: false })
  const hasApp = app.data?.enabled === true
  const status = useQuery({ queryKey: ['two-factor-email'], queryFn: api.twoFactor.email.status, retry: false })
  const [token, setToken] = useState<string | null>(null)
  const [purpose, setPurpose] = useState<'enable' | 'disable'>('enable')
  const [code, setCode] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong.')
  const finish = () => { setToken(null); setCode(''); setPassword(''); setError(null); void queryClient.invalidateQueries({ queryKey: ['two-factor-email'] }) }

  const send = useMutation({
    mutationFn: (p: 'enable' | 'disable') => { setPurpose(p); return api.twoFactor.email.send(p) },
    onSuccess: (t) => { setError(null); setToken(t) },
    onError: fail,
  })
  const confirm = useMutation({
    mutationFn: () => (purpose === 'enable' ? api.twoFactor.email.enable(token!, code) : api.twoFactor.email.disable(token!, code, password)),
    onSuccess: finish,
    onError: fail,
  })

  if (status.isPending || status.isError) return null
  const enabled = status.data.enabled

  return (
    <section className="card narrow" aria-labelledby="email-code-title">
      <h2 id="email-code-title">Sign-in code by email</h2>
      {error && <div className="banner error" role="alert">{error}</div>}
      {!token && (
        <>
          <p className="muted">
            {enabled ? 'Signing in also needs a code we email you.' : 'Ask for a code sent to your email address in addition to your password. Easy, but an authenticator app is safer.'}
            {hasApp && ' Your authenticator app is used first.'}
          </p>
          <button className="btn" onClick={() => send.mutate(enabled ? 'disable' : 'enable')} disabled={send.isPending}>
            {send.isPending ? 'Sending…' : enabled ? 'Turn off…' : 'Turn on…'}
          </button>
        </>
      )}
      {token && (
        <form onSubmit={(e) => { e.preventDefault(); confirm.mutate() }}>
          <p>We sent a code to your email. It works once, for 10 minutes.</p>
          <label className="field"><span>The 6-digit code</span>
            <input inputMode="numeric" autoComplete="one-time-code" required value={code} onChange={(e) => setCode(cleanCode(e.target.value))} /></label>
          {purpose === 'disable' && (
            <label className="field"><span>Password</span>
              <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          )}
          <button className={`btn ${purpose === 'disable' ? 'danger' : 'primary'}`} type="submit" disabled={confirm.isPending || code.length !== 6}>
            {purpose === 'enable' ? 'Turn on' : 'Turn off'}
          </button>{' '}
          <button className="btn" type="button" onClick={finish}>Cancel</button>
        </form>
      )}
    </section>
  )
}
