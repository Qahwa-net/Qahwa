import { useState, type FormEvent } from 'react'
import { Link, Navigate, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'
import { useAuth } from '../auth.tsx'

/** Only follow same-site relative paths after login, never an arbitrary URL. */
function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/\\') ? next : '/'
}

export function LoginPage() {
  const { user, login, loginWithCode } = useAuth()
  const [params] = useSearchParams()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [challenge, setChallenge] = useState<string | null>(null)
  const [expired, setExpired] = useState(false)
  const [code, setCode] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [notice, setNotice] = useState<string | null>(null)

  if (user) return <Navigate to={safeNext(params.get('next'))} replace />

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (challenge && expired) {
        if (newPassword !== repeat) throw new Error('mismatch')
        await api.changeExpiredPassword(challenge, password, newPassword)
        setChallenge(null)
        setExpired(false)
        setNotice('Your password has been changed. Sign in with the new one.')
        setNewPassword('')
        setRepeat('')
        setPassword('')
      } else if (challenge) {
        await loginWithCode(challenge, code)
      } else {
        const next = await login(email, password)
        if (next) {
          setChallenge(next.token)
          setExpired(next.kind === 'expired')
          if (next.kind === 'code') setPassword('')
        }
      }
    } catch (e) {
      setCode('')
      if (!(challenge && expired)) setPassword('')
      setError(
        e instanceof Error && e.message === 'mismatch'
          ? 'The two new passwords are not the same.'
        : e instanceof ApiError && challenge && expired && e.status === 400
          ? (e.details[0]?.message ?? e.message)
        : e instanceof ApiError && e.status === 429
          ? 'Too many attempts. Please wait a minute and try again.'
          : e instanceof ApiError && e.code === 'invalid_code'
            ? 'That code is not right. Try the next one, or use a recovery code.'
          : e instanceof ApiError && e.status === 401
            ? challenge ? 'This sign-in took too long. Start again.' : 'Invalid email or password.'
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
        <p className="muted">{challenge && expired ? 'Your password has expired. Choose a new one to continue.' : challenge ? 'Enter the code from your authenticator app' : 'Sign in to continue'}</p>
        {notice && !challenge && <div className="banner info" role="status">{notice}</div>}

        {error && <div className="banner error" role="alert">{error}</div>}

        {challenge && expired ? (<>
          <label className="field"><span>Current password</span>
            <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          <label className="field"><span>New password</span>
            <input type="password" autoComplete="new-password" required minLength={10} autoFocus value={newPassword} onChange={(e) => setNewPassword(e.target.value)} />
            <small className="muted">At least 10 characters, different from the old one.</small></label>
          <label className="field"><span>Repeat the new password</span>
            <input type="password" autoComplete="new-password" required value={repeat} onChange={(e) => setRepeat(e.target.value)} /></label>
        </>) : challenge ? (
          <label className="field">
            <span>Code</span>
            <input inputMode="text" autoComplete="one-time-code" required autoFocus maxLength={20} value={code}
              onChange={(e) => setCode(e.target.value)} />
            <small className="muted">The 6 digits from the app, or one of your recovery codes.</small>
          </label>
        ) : (<>
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
        </>)}

        <button className="btn primary" type="submit" disabled={busy}>{busy ? 'Working…' : challenge && expired ? 'Change password' : challenge ? 'Verify' : 'Sign in'}</button>
        {!challenge && <Link to="/forgot-password" className="muted">Forgot your password?</Link>}
        {challenge && <button className="btn" type="button" onClick={() => { setChallenge(null); setExpired(false); setCode(''); setError(null) }}>Back</button>}
      </form>
    </main>
  )
}
