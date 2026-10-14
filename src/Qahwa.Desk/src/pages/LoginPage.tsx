import { useState, type FormEvent } from 'react'
import { Navigate, useSearchParams } from 'react-router-dom'
import { ApiError } from '../api/client.ts'
import { useAuth } from '../auth.tsx'

/** Only follow same-site relative paths after login, never an arbitrary URL. */
function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/\\') ? next : '/'
}

export function LoginPage() {
  const { user, login } = useAuth()
  const [params] = useSearchParams()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (user) return <Navigate to={safeNext(params.get('next'))} replace />

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await login(email, password)
    } catch (e) {
      setPassword('')
      setError(
        e instanceof ApiError && e.status === 429
          ? 'Too many attempts. Please wait a minute and try again.'
          : e instanceof ApiError && e.status === 401
            ? 'Invalid email or password.'
            : 'Could not sign in. Please try again.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="center-screen">
      <form className="card login" onSubmit={submit}>
        <h1>Qahwa</h1>
        <p className="muted">Sign in to continue</p>

        {error && <div className="banner error" role="alert">{error}</div>}

        <label className="field">
          <span>Email</span>
          <input type="email" autoComplete="username" required autoFocus value={email}
            onChange={(e) => setEmail(e.target.value)} />
        </label>
        <label className="field">
          <span>Password</span>
          <input type="password" autoComplete="current-password" required value={password}
            onChange={(e) => setPassword(e.target.value)} />
        </label>

        <button className="btn primary" type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      </form>
    </main>
  )
}
