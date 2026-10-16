import { useState, type FormEvent } from 'react'
import { ApiError, api } from '../api/client.ts'
import { useAuth } from '../auth.tsx'
import { ApiKeysSection } from './ApiKeysSection.tsx'
import { SessionsSection } from './SessionsSection.tsx'
import { TwoFactorSection } from './TwoFactorSection.tsx'

export function AccountPage() {
  const { user } = useAuth()
  const [oldPassword, setOldPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (newPassword !== repeat) {
      setMessage({ kind: 'error', text: 'The new passwords do not match.' })
      return
    }

    setBusy(true)
    setMessage(null)
    try {
      await api.changePassword(oldPassword, newPassword)
      setOldPassword('')
      setNewPassword('')
      setRepeat('')
      setMessage({ kind: 'ok', text: 'Password changed. Your other sessions have been signed out.' })
    } catch (e) {
      const text = e instanceof ApiError && e.details.length > 0
        ? e.details.map((d) => d.message).join(' ')
        : e instanceof ApiError ? e.message : 'Could not change the password.'
      setMessage({ kind: 'error', text })
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <h1>My account</h1>
      <p className="muted">{user?.full_name} · {user?.user}</p>
      <p className="muted">Roles: {user?.roles.join(', ') || 'none'}</p>

      <form className="card narrow" onSubmit={submit}>
        <h2>Change password</h2>
        {message && <div className={`banner ${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}>{message.text}</div>}
        <label className="field"><span>Current password</span>
          <input type="password" autoComplete="current-password" required value={oldPassword}
            onChange={(e) => setOldPassword(e.target.value)} /></label>
        <label className="field"><span>New password</span>
          <input type="password" autoComplete="new-password" required minLength={10} value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)} />
          <small className="muted">At least 10 characters.</small></label>
        <label className="field"><span>Repeat new password</span>
          <input type="password" autoComplete="new-password" required value={repeat}
            onChange={(e) => setRepeat(e.target.value)} /></label>
        <button className="btn primary" type="submit" disabled={busy}>Change password</button>
      </form>

      <SessionsSection />
      <TwoFactorSection />
      <ApiKeysSection />
    </>
  )
}
