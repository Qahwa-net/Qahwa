import { useState, type ChangeEvent, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { DEFAULT_BRAND, brandVariables, isHexColor, logoFileProblem, safeLogo, type ThemePref } from '../lib/theme.ts'
import { useTheme } from '../theme.tsx'

const fileToDataUrl = (file: File): Promise<string> =>
  new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result))
    reader.onerror = () => reject(new Error('The file could not be read.'))
    reader.readAsDataURL(file)
  })

/** Where a System Manager sets the name, logo, colours and default theme of this installation. */
export function BrandPage() {
  const { brand, scheme } = useTheme()
  const queryClient = useQueryClient()
  const [name, setName] = useState(brand.name === DEFAULT_BRAND.name ? '' : brand.name)
  const [logo, setLogo] = useState<string | null>(brand.logo)
  const [color, setColor] = useState(brand.color ?? '')
  const [colorDark, setColorDark] = useState(brand.color_dark ?? '')
  const [theme, setTheme] = useState<ThemePref>(brand.theme)
  const [message, setMessage] = useState(brand.login_message ?? '')
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)

  async function chooseLogo(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return
    const problem = logoFileProblem(file)
    if (problem) {
      setNote({ kind: 'error', text: problem })
      return
    }

    setNote(null)
    setLogo(await fileToDataUrl(file))
  }

  async function save(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setNote(null)
    try {
      await api.update('Brand Settings', 'Brand Settings', {
        brand_name: name.trim(), brand_logo: logo ?? '', brand_color: color, brand_color_dark: colorDark,
        default_theme: theme === 'auto' ? 'Auto' : theme === 'light' ? 'Light' : 'Dark', login_message: message.trim(),
      })
      await queryClient.invalidateQueries({ queryKey: ['brand'] })
      setNote({ kind: 'ok', text: 'Saved. Everyone sees the new look the next time a page loads.' })
    } catch (e) {
      setNote({ kind: 'error', text: e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not save.' })
    } finally {
      setBusy(false)
    }
  }

  const preview = brandVariables({ color: isHexColor(color) ? color : null, color_dark: isHexColor(colorDark) ? colorDark : null }, scheme)
  const previewLogo = safeLogo(logo)

  return (
    <>
      <h1>Brand</h1>
      <p className="muted">The name, logo and colours people see, on the sign-in page and in the header.</p>
      <form className="card narrow" onSubmit={save}>
        {note && <div className={`banner ${note.kind === 'ok' ? 'info' : 'error'}`} role={note.kind === 'error' ? 'alert' : 'status'}>{note.text}</div>}
        <label className="field"><span>Name (empty means Qahwa)</span>
          <input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} /></label>
        <div className="field">
          <span>Logo (PNG, JPEG, GIF, WebP or SVG, up to 100 KB)</span>
          <div className="row">
            {previewLogo && <img src={previewLogo} alt="Logo" style={{ maxBlockSize: '3rem', maxInlineSize: '10rem' }} />}
            <input type="file" accept="image/png,image/jpeg,image/gif,image/webp,image/svg+xml" onChange={(e) => void chooseLogo(e)} aria-label="Choose a logo" />
            {logo && <button type="button" className="btn" onClick={() => setLogo(null)}>Remove the logo</button>}
          </div>
        </div>
        <div className="field"><span>Brand colour</span>
          <div className="row">
            <input type="color" value={isHexColor(color) ? color : '#6f4e37'} onChange={(e) => setColor(e.target.value)} aria-label="Brand colour" />
            <input value={color} placeholder="#6f4e37" maxLength={7} onChange={(e) => setColor(e.target.value)} aria-label="Brand colour code" />
            {color && <button type="button" className="btn" onClick={() => setColor('')}>Use the default</button>}
          </div>
        </div>
        <div className="field"><span>Brand colour for the dark theme (empty: a lighter version of the brand colour)</span>
          <div className="row">
            <input type="color" value={isHexColor(colorDark) ? colorDark : '#c69c7b'} onChange={(e) => setColorDark(e.target.value)} aria-label="Dark theme colour" />
            <input value={colorDark} placeholder="#c69c7b" maxLength={7} onChange={(e) => setColorDark(e.target.value)} aria-label="Dark theme colour code" />
            {colorDark && <button type="button" className="btn" onClick={() => setColorDark('')}>Use the default</button>}
          </div>
        </div>
        <label className="field"><span>Theme for people who have not chosen one</span>
          <select value={theme} onChange={(e) => setTheme(e.target.value as ThemePref)}>
            <option value="auto">Automatic (follows their device)</option><option value="light">Light</option><option value="dark">Dark</option>
          </select></label>
        <label className="field"><span>Message on the sign-in page</span>
          <textarea rows={3} maxLength={300} value={message} onChange={(e) => setMessage(e.target.value)} /></label>

        <div className="card" style={{ ...preview, background: 'var(--surface-2)' }} aria-label="Preview">
          <strong style={{ color: preview['--brand'] ?? 'var(--brand)' }}>{name.trim() || DEFAULT_BRAND.name}</strong>
          <p style={{ background: preview['--accent-soft'] ?? 'var(--accent-soft)', padding: '.4rem' }}>A highlighted line</p>
          <span className="btn primary" style={{ background: preview['--brand'] ?? 'var(--brand)', color: preview['--brand-text'] ?? 'var(--brand-text)' }}>A button</span>
        </div>
        <button className="btn primary" type="submit" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
      </form>
    </>
  )
}
