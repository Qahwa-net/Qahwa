import { useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client.ts'

/** Ask for a reset link. The answer is always the same, so this page cannot be used to find out who has an account. */
export function ForgotPasswordPage() {
  const [email, setEmail] = useState('')
  const [done, setDone] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      setDone((await api.passwordReset.ask(email)).message)
    } catch (e) {
      setError(e instanceof ApiError && e.status === 429 ? 'Too many attempts. Please wait a minute and try again.' : 'Could not send the request. Please try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="center-screen">
      <form className="card login" onSubmit={submit}>
        <h1>Reset your password</h1>
        {error && <div className="banner error" role="alert">{error}</div>}
        {done ? (
          <div className="banner info" role="status">{done}</div>
        ) : (
          <>
            <p className="muted">Enter your email and we will send you a link to choose a new password.</p>
            <label className="field"><span>Email</span>
              <input type="email" autoComplete="username" required autoFocus value={email} onChange={(e) => setEmail(e.target.value)} /></label>
            <button className="btn primary" type="submit" disabled={busy}>{busy ? 'Sending…' : 'Send the link'}</button>
          </>
        )}
        <Link to="/login" className="muted">Back to sign in</Link>
      </form>
    </main>
  )
}

/** Choose a new password with the link from the email. */
export function ResetPasswordPage() {
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (password !== repeat) { setError('The two passwords are not the same.'); return }
    setBusy(true)
    setError(null)
    try {
      await api.passwordReset.reset(token, password)
      setDone(true)
    } catch (e) {
      setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not change the password. Please try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="center-screen">
      <form className="card login" onSubmit={submit}>
        <h1>Choose a new password</h1>
        {error && <div className="banner error" role="alert">{error}</div>}
        {done ? (
          <div className="banner info" role="status">Your password has been changed. You can sign in now. <Link to="/login">Sign in</Link></div>
        ) : !token ? (
          <div className="banner error" role="alert">This link is not complete. Ask for a new one. <Link to="/forgot-password">Send a new link</Link></div>
        ) : (
          <>
            <label className="field"><span>New password</span>
              <input type="password" autoComplete="new-password" required minLength={10} autoFocus value={password} onChange={(e) => setPassword(e.target.value)} />
              <small className="muted">At least 10 characters. Avoid common words, your name and simple patterns.</small></label>
            <label className="field"><span>Repeat the new password</span>
              <input type="password" autoComplete="new-password" required value={repeat} onChange={(e) => setRepeat(e.target.value)} /></label>
            <button className="btn primary" type="submit" disabled={busy}>{busy ? 'Saving…' : 'Change password'}</button>
          </>
        )}
      </form>
    </main>
  )
}
