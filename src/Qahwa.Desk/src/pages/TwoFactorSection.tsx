import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import type { TwoFactorSetup } from '../api/types.ts'
import { useAuth } from '../auth.tsx'
import { cleanCode, groupKey, qrImage, recoveryFile } from '../lib/twofactor.ts'

/** Turn two-factor sign-in on or off. The QR code is made in the browser and shown as an image, never as markup from the server. */
export function TwoFactorSection() {
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const status = useQuery({ queryKey: ['two-factor'], queryFn: api.twoFactor.status, retry: false })
  const [setup, setSetup] = useState<TwoFactorSetup | null>(null)
  const [code, setCode] = useState('')
  const [password, setPassword] = useState('')
  const [recovery, setRecovery] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['two-factor'] })
  const fail = (e: unknown) => setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Something went wrong.')

  const start = useMutation({ mutationFn: api.twoFactor.setup, onSuccess: (s) => { setError(null); setSetup(s) }, onError: fail })
  const enable = useMutation({
    mutationFn: () => api.twoFactor.enable(code),
    onSuccess: (codes) => { setError(null); setSetup(null); setCode(''); setRecovery(codes); void refresh() },
    onError: fail,
  })
  const disable = useMutation({
    mutationFn: () => api.twoFactor.disable(password, code),
    onSuccess: () => { setError(null); setCode(''); setPassword(''); void refresh() },
    onError: fail,
  })

  if (status.isPending || status.isError) return null

  const download = () => {
    const blob = new Blob([recoveryFile(user?.user ?? '', recovery ?? [])], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = 'qahwa-recovery-codes.txt'
    link.click()
    URL.revokeObjectURL(url)
  }

  return (
    <section className="card narrow" aria-labelledby="two-factor-title">
      <h2 id="two-factor-title">Two-factor sign-in</h2>
      {error && <div className="banner error" role="alert">{error}</div>}

      {recovery && (
        <div role="status">
          <div className="banner info">Two-factor sign-in is on. Save these recovery codes now: each works once, and they are not shown again.</div>
          <pre className="mono">{recovery.join('\n')}</pre>
          <button className="btn" onClick={download}>Download</button>{' '}
          <button className="btn primary" onClick={() => setRecovery(null)}>I have saved them</button>
        </div>
      )}

      {!recovery && !status.data.enabled && !setup && (
        <>
          <p className="muted">Ask for a code from an authenticator app (Google Authenticator, Microsoft Authenticator, Authy, 1Password …) in addition to your password.</p>
          <button className="btn primary" onClick={() => start.mutate()} disabled={start.isPending}>Set up</button>
        </>
      )}

      {setup && (
        <form onSubmit={(e) => { e.preventDefault(); enable.mutate() }}>
          <p>1. Scan this with your authenticator app, or type the key.</p>
          <img className="qr" alt="Setup QR code" width={180} height={180} src={qrImage(setup.uri)} />
          <p className="mono" aria-label="Setup key">{groupKey(setup.secret)}</p>
          <label className="field"><span>2. Enter the 6-digit code it shows</span>
            <input inputMode="numeric" autoComplete="one-time-code" required value={code} onChange={(e) => setCode(cleanCode(e.target.value))} /></label>
          <button className="btn primary" type="submit" disabled={enable.isPending || code.length !== 6}>Turn on</button>{' '}
          <button className="btn" type="button" onClick={() => { setSetup(null); setCode('') }}>Cancel</button>
        </form>
      )}

      {!recovery && status.data.enabled && (
        <form onSubmit={(e) => { e.preventDefault(); disable.mutate() }}>
          <p>Two-factor sign-in is <strong>on</strong>. Recovery codes left: {status.data.recoveryCodesLeft}.</p>
          <label className="field"><span>Password</span>
            <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          <label className="field"><span>Code from the app (or a recovery code)</span>
            <input required maxLength={20} autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value)} /></label>
          <button className="btn danger" type="submit" disabled={disable.isPending}>Turn off</button>
        </form>
      )}
    </section>
  )
}
