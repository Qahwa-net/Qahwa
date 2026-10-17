import { useState } from 'react'
import { ApiError, api } from '../api/client.ts'
import { IconButton } from '../controls/Icon.tsx'
import { Modal } from '../controls/Modal.tsx'
import { useToast } from '../controls/Toast.tsx'

export function UserPasswordActions({ email }: { email: string }) {
  const [action, setAction] = useState<'password' | 'reset' | null>(null)
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const toast = useToast()
  function close() { if (!busy) { setAction(null); setPassword(''); setRepeat(''); setError(null) } }
  async function submit() {
    setError(null)
    if (action === 'password' && password !== repeat) return setError('The passwords do not match.')
    setBusy(true)
    try {
      const result = action === 'password' ? await api.userSecurity.setPassword(email, password) : await api.userSecurity.sendReset(email)
      toast(result.message)
      setAction(null); setPassword(''); setRepeat('')
    } catch (e) { setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'The request could not be completed.') }
    finally { setBusy(false) }
  }
  return <>
    <IconButton icon="lock" label="Set user password" onClick={() => setAction('password')} />
    <IconButton icon="mail" label="Send password-reset email" onClick={() => setAction('reset')} />
    {action && <Modal title={action === 'password' ? 'Set user password' : 'Send password-reset email'} subtitle={email} onClose={close} dismissible={!busy}>
      <form className="form-stack" onSubmit={e => { e.preventDefault(); void submit() }}>
        {error && <div className="banner error" role="alert">{error}</div>}
        {action === 'password' ? <>
          <p className="muted">Choose a new password for this user. Their active sessions will end immediately.</p>
          <label className="field"><span>New password</span><input type="password" autoComplete="new-password" required minLength={10} maxLength={128} value={password} onChange={e => setPassword(e.target.value)} disabled={busy} /></label>
          <label className="field"><span>Confirm password</span><input type="password" autoComplete="new-password" required value={repeat} onChange={e => setRepeat(e.target.value)} disabled={busy} /></label>
        </> : <p>A link will be sent to this user's email address. It expires in one hour and can be used once. Configure outgoing email in Email Account first.</p>}
        <div className="row"><span className="spacer" /><button type="button" className="btn" onClick={close} disabled={busy}>Cancel</button><button className="btn primary" type="submit" disabled={busy}>{busy ? 'Working…' : action === 'password' ? 'Set password' : 'Send reset link'}</button></div>
      </form>
    </Modal>}
  </>
}

export function TestEmailAction({ disabled }: { disabled: boolean }) {
  const [open, setOpen] = useState(false)
  const [to, setTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const toast = useToast()
  return <>
    <IconButton icon="mail" label={disabled ? 'Save Email Account before testing' : 'Send test email'} disabled={disabled} onClick={() => { setError(null); setOpen(true) }} />
    {open && <Modal title="Test outgoing email" onClose={() => setOpen(false)} dismissible={!busy}>
      <form className="form-stack" onSubmit={async e => {
        e.preventDefault(); setBusy(true); setError(null)
        try { const result = await api.userSecurity.testEmail(to); toast(result.message); setOpen(false) }
        catch (err) { setError(err instanceof ApiError ? err.message : 'Could not send the test email.') }
        finally { setBusy(false) }
      }}>
        <p className="muted">Send a test using the saved SMTP account.</p>
        {error && <div className="banner error" role="alert">{error}</div>}
        <label className="field"><span>Recipient email</span><input type="email" required value={to} onChange={e => setTo(e.target.value)} disabled={busy} /></label>
        <div className="row"><span className="spacer" /><button className="btn primary" type="submit" disabled={busy}>{busy ? 'Sending…' : 'Send test email'}</button></div>
      </form>
    </Modal>}
  </>
}
