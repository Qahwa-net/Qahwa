const fs = require('fs')
const D = 'D:/Me/Qahwa/src/Qahwa.Desk/src/'
function ed(f, a, b) {
  let s = fs.readFileSync(D + f, 'utf8')
  if (!s.includes(a)) {
    s = s.split('\r\n').join('\n')
    if (!s.includes(a)) throw new Error(f + ' missing ' + a)
  }
  fs.writeFileSync(D + f, s.replace(a, b))
}
ed('api/types.ts', `export interface TwoFactorStatus {`, `export interface ExpiredPasswordChallenge {
  password_expired: true
  token: string
}

export interface TwoFactorStatus {`)
ed('api/client.ts', `async login(email: string, password: string): Promise<SessionUser | TwoFactorChallenge> {
    return (await request<{ data: SessionUser | TwoFactorChallenge }>('POST', '/api/auth/login', { email, password })).data
  },`, `async login(email: string, password: string): Promise<SessionUser | TwoFactorChallenge | ExpiredPasswordChallenge> {
    return (await request<{ data: SessionUser | TwoFactorChallenge | ExpiredPasswordChallenge }>('POST', '/api/auth/login', { email, password })).data
  },

  changeExpiredPassword: (token: string, oldPassword: string, newPassword: string) =>
    request<unknown>('POST', '/api/auth/expired-password', { token, old_password: oldPassword, new_password: newPassword }),`)
ed('api/client.ts', 'ListResponse, SignIn,', 'ListResponse, ExpiredPasswordChallenge, SignIn,')
ed('auth.tsx', `  login: (email: string, password: string) => Promise<{ token: string } | null>`, `  login: (email: string, password: string) => Promise<{ token: string; kind: 'code' | 'expired' } | null>`)
ed('auth.tsx', `onSuccess: (result) => { if (!('two_factor' in result)) signedIn(result) },`, `onSuccess: (result) => { if (!('two_factor' in result) && !('password_expired' in result)) signedIn(result) },`)
ed('auth.tsx', `      return 'two_factor' in result ? { token: result.token } : null`, `      return 'two_factor' in result ? { token: result.token, kind: 'code' } : 'password_expired' in result ? { token: result.token, kind: 'expired' } : null`)

let page = fs.readFileSync(D + 'pages/LoginPage.tsx', 'utf8').split('\r\n').join('\n')
page = page.replace(`import { ApiError } from '../api/client.ts'`, `import { ApiError, api } from '../api/client.ts'`)
page = page.replace(`  const [challenge, setChallenge] = useState<string | null>(null)
  const [code, setCode] = useState('')`, `  const [challenge, setChallenge] = useState<string | null>(null)
  const [expired, setExpired] = useState(false)
  const [code, setCode] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [notice, setNotice] = useState<string | null>(null)`)
page = page.replace(`      if (challenge) {
        await loginWithCode(challenge, code)
      } else {
        const next = await login(email, password)
        if (next) {
          setChallenge(next.token)
          setPassword('')
        }
      }`, `      if (challenge && expired) {
        if (newPassword !== repeat) throw new Error('mismatch')
        await api.changeExpiredPassword(challenge, password, newPassword)
        setChallenge(null)
        setExpired(false)
        setNotice('Your password has been changed. Sign in with the new one.')
        setNewPassword('')
        setRepeat('')
      } else if (challenge) {
        await loginWithCode(challenge, code)
      } else {
        const next = await login(email, password)
        if (next) {
          setChallenge(next.token)
          setExpired(next.kind === 'expired')
          if (next.kind === 'code') setPassword('')
        }
      }`)
page = page.replace(`    } catch (e) {
      setCode('')
      setPassword('')
      setError(
        e instanceof ApiError && e.status === 429`, `    } catch (e) {
      setCode('')
      if (!(challenge && expired)) setPassword('')
      setError(
        e instanceof Error && e.message === 'mismatch'
          ? 'The two new passwords are not the same.'
        : e instanceof ApiError && challenge && expired && e.status === 400
          ? (e.details[0]?.message ?? e.message)
        : e instanceof ApiError && e.status === 429`)
page = page.replace(`<p className="muted">{challenge ? 'Enter the code from your authenticator app' : 'Sign in to continue'}</p>`, `<p className="muted">{challenge && expired ? 'Your password has expired. Choose a new one to continue.' : challenge ? 'Enter the code from your authenticator app' : 'Sign in to continue'}</p>
        {notice && !challenge && <div className="banner info" role="status">{notice}</div>}`)
page = page.replace(`        {challenge ? (
          <label className="field">`, `        {challenge && expired ? (<>
          <label className="field"><span>Current password</span>
            <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          <label className="field"><span>New password</span>
            <input type="password" autoComplete="new-password" required minLength={10} autoFocus value={newPassword} onChange={(e) => setNewPassword(e.target.value)} />
            <small className="muted">At least 10 characters, different from the old one.</small></label>
          <label className="field"><span>Repeat the new password</span>
            <input type="password" autoComplete="new-password" required value={repeat} onChange={(e) => setRepeat(e.target.value)} /></label>
        </>) : challenge ? (
          <label className="field">`)
page = page.replace(`{busy ? 'Signing in…' : challenge ? 'Verify' : 'Sign in'}`, `{busy ? 'Working…' : challenge && expired ? 'Change password' : challenge ? 'Verify' : 'Sign in'}`)
page = page.replace(`onClick={() => { setChallenge(null); setCode(''); setError(null) }}>Back</button>}`, `onClick={() => { setChallenge(null); setExpired(false); setCode(''); setError(null) }}>Back</button>}`)
fs.writeFileSync(D + 'pages/LoginPage.tsx', page)
console.log('ok')
