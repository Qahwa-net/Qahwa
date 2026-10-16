import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '../api/client.ts'
import { fileProblem } from '../lib/files.ts'
import { useDocScope } from './DocContext.tsx'
import { ICON_NAMES, Icon } from './Icon.tsx'
import { MAX_STARS, formatDuration, isHexColor, isSignature, mapUrl, parseDuration, parsePlace, placeText, starsLabel } from '../lib/fieldtypes.ts'

interface Common {
  id?: string
  ariaLabel?: string
  readOnly?: boolean
  invalid?: boolean
  value: string | boolean
  onChange: (value: string) => void
}

/** Stars from 0 to 5. Choosing the star that is already chosen clears it. */
export function RatingInput({ value, onChange, readOnly, ariaLabel, id }: Common) {
  const current = Number(value) || 0
  return (
    <div id={id} role="radiogroup" aria-label={ariaLabel ?? 'Rating'} className="rating">
      {Array.from({ length: MAX_STARS }, (_, i) => i + 1).map((n) => (
        <button key={n} type="button" role="radio" aria-checked={current === n} aria-label={`${n} star${n === 1 ? '' : 's'}`} disabled={readOnly}
          className={n <= current ? 'star on' : 'star'} onClick={() => onChange(current === n ? '' : String(n))}>★</button>
      ))}
      <span className="muted small-text" aria-hidden="true">{starsLabel(current)}</span>
    </div>
  )
}

/** A length of time. Type "1h 30m", "1:30" or seconds; it is shown as "1h 30m" and kept as seconds. */
export function DurationInput({ value, onChange, readOnly, invalid, id, ariaLabel }: Common) {
  const seconds = value === '' ? null : Number(value)
  const [text, setText] = useState(seconds === null || Number.isNaN(seconds) ? '' : formatDuration(seconds))
  const [bad, setBad] = useState(false)
  useEffect(() => {
    setText(seconds === null || Number.isNaN(seconds) ? '' : formatDuration(seconds))
    setBad(false)
  }, [seconds])

  const commit = () => {
    if (text.trim() === '') { setBad(false); onChange(''); return }
    const parsed = parseDuration(text)
    setBad(parsed === null)
    if (parsed !== null) onChange(String(parsed))
  }

  return <input id={id} aria-label={ariaLabel} aria-invalid={invalid || bad} value={text} readOnly={readOnly} placeholder="1h 30m" inputMode="text" autoComplete="off"
    onChange={(e) => setText(e.target.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit() }} title={bad ? 'Try 1h 30m, 1:30 or a number of seconds.' : undefined} />
}

/** An icon chosen from the Desk's own set. A name the Desk does not know is kept as it is but shown as nothing. */
export function IconInput({ value, onChange, readOnly, id, ariaLabel }: Common) {
  const current = String(value)
  return (
    <div id={id} role="radiogroup" aria-label={ariaLabel ?? 'Icon'} className="icon-picker">
      {ICON_NAMES.map((name) => (
        <button key={name} type="button" role="radio" aria-checked={current === name} aria-label={name} title={name} disabled={readOnly}
          className={current === name ? 'icon-choice on' : 'icon-choice'} onClick={() => onChange(current === name ? '' : name)}><Icon name={name} /></button>
      ))}
    </div>
  )
}

/** A colour: the browser's picker and the hex text. */
export function ColorInput({ value, onChange, readOnly, invalid, id, ariaLabel }: Common) {
  const text = String(value)
  return (
    <span className="color-input">
      <input type="color" aria-label={`${ariaLabel ?? 'Colour'} picker`} value={isHexColor(text) ? text : '#000000'} disabled={readOnly} onChange={(e) => onChange(e.target.value)} />
      <input id={id} aria-label={ariaLabel} aria-invalid={invalid} className="mono" value={text} readOnly={readOnly} maxLength={7} placeholder="#rrggbb" onChange={(e) => onChange(e.target.value.trim())} />
    </span>
  )
}

/** A latitude and a longitude, with a button to fill them in from this device and a link to see the place on a map. */
export function GeolocationInput({ value, onChange, readOnly, invalid, id, ariaLabel }: Common) {
  const place = parsePlace(value)
  const [lat, setLat] = useState(place ? String(place.lat) : '')
  const [lng, setLng] = useState(place ? String(place.lng) : '')
  const [problem, setProblem] = useState<string | null>(null)
  useEffect(() => { const p = parsePlace(value); setLat(p ? String(p.lat) : ''); setLng(p ? String(p.lng) : '') }, [value])

  const commit = (la: string, lo: string) => {
    if (la.trim() === '' && lo.trim() === '') { onChange(''); setProblem(null); return }
    const next = { lat: Number(la), lng: Number(lo) }
    if (la.trim() === '' || lo.trim() === '' || Math.abs(next.lat) > 90 || Math.abs(next.lng) > 180 || Number.isNaN(next.lat) || Number.isNaN(next.lng)) { setProblem('Latitude is within 90 and longitude within 180.'); return }
    setProblem(null)
    onChange(placeText(next))
  }

  const locate = () => navigator.geolocation?.getCurrentPosition(
    (p) => { setLat(String(p.coords.latitude)); setLng(String(p.coords.longitude)); commit(String(p.coords.latitude), String(p.coords.longitude)) },
    () => setProblem('This device did not share its position.'),
  )

  return (
    <span className="geo-input" id={id} role="group" aria-label={ariaLabel ?? 'Place'}>
      <input aria-label="Latitude" inputMode="decimal" placeholder="Latitude" value={lat} readOnly={readOnly} aria-invalid={invalid || Boolean(problem)} onChange={(e) => setLat(e.target.value)} onBlur={() => commit(lat, lng)} />
      <input aria-label="Longitude" inputMode="decimal" placeholder="Longitude" value={lng} readOnly={readOnly} aria-invalid={invalid || Boolean(problem)} onChange={(e) => setLng(e.target.value)} onBlur={() => commit(lat, lng)} />
      {!readOnly && 'geolocation' in navigator && <button type="button" className="btn small" onClick={locate}>Use my location</button>}
      {place && <a className="btn small" href={mapUrl(place)} target="_blank" rel="noopener noreferrer">View on map</a>}
      {problem && <small className="error-text" role="alert">{problem}</small>}
    </span>
  )
}

const WIDTH = 400
const HEIGHT = 150

/** A box to sign in with the mouse or a finger. The picture is kept as a small PNG. */
export function SignatureInput({ value, onChange, readOnly, id, ariaLabel }: Common) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const drawing = useRef(false)
  // A saved signature is shown as a picture; as soon as the person starts to sign (or signs again) the box is back, and stays, so several strokes work.
  const [editing, setEditing] = useState(false)
  const signed = isSignature(value) && !editing

  const point = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const box = e.currentTarget.getBoundingClientRect()
    return { x: ((e.clientX - box.left) / box.width) * WIDTH, y: ((e.clientY - box.top) / box.height) * HEIGHT }
  }

  const start = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (readOnly) return
    setEditing(true)
    const ctx = canvas.current!.getContext('2d')!
    ctx.lineWidth = 2.5
    ctx.lineCap = 'round'
    ctx.strokeStyle = '#111'
    const { x, y } = point(e)
    ctx.beginPath()
    ctx.moveTo(x, y)
    drawing.current = true
    e.currentTarget.setPointerCapture(e.pointerId)
  }
  const move = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!drawing.current) return
    const ctx = canvas.current!.getContext('2d')!
    const { x, y } = point(e)
    ctx.lineTo(x, y)
    ctx.stroke()
  }
  const end = () => {
    if (!drawing.current) return
    drawing.current = false
    onChange(canvas.current!.toDataURL('image/png'))
  }
  const clear = () => {
    canvas.current?.getContext('2d')?.clearRect(0, 0, WIDTH, HEIGHT)
    setEditing(true)
    onChange('')
  }

  return (
    <div id={id} className="signature">
      {signed
        ? <img src={String(value)} alt="Signature" width={WIDTH / 2} height={HEIGHT / 2} />
        : <canvas ref={canvas} width={WIDTH} height={HEIGHT} aria-label={ariaLabel ?? 'Signature box'} role="img" style={{ touchAction: 'none' }}
            onPointerDown={start} onPointerMove={move} onPointerUp={end} onPointerCancel={end} />}
      {!readOnly && <button type="button" className="btn small" onClick={clear}>{signed ? 'Sign again' : 'Clear'}</button>}
    </div>
  )
}

/**
 * A file field. The file is uploaded to the saved document (the same place as the side panel) and the field keeps its id, so a new
 * document must be saved first. A picture is shown on the form; any other file is a download link.
 */
export function AttachInput({ value, onChange, readOnly, id, ariaLabel, image }: Common & { image: boolean }) {
  const scope = useDocScope()
  const queryClient = useQueryClient()
  const fileId = typeof value === 'string' && value !== '' ? value : null
  const files = useQuery({
    queryKey: ['files', scope?.doctype, scope?.name],
    queryFn: () => api.files.list(scope!.doctype, scope!.name!),
    enabled: Boolean(scope?.name) && fileId !== null,
    retry: false,
  })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const chosen = files.data?.find((f) => f.id === fileId)

  if (!scope?.name) return <small className="muted">Save the document first, then attach the file.</small>

  const choose = async (file: File | undefined) => {
    if (!file) return
    const problem = fileProblem(file)
    if (problem) { setError(problem); return }
    if (image && !file.type.startsWith('image/')) { setError('Choose a picture (PNG, JPEG, GIF or WebP).'); return }
    setBusy(true)
    setError(null)
    try {
      const uploaded = await api.files.upload(scope.doctype, scope.name!, file)
      onChange(uploaded.id)
      await queryClient.invalidateQueries({ queryKey: ['files', scope.doctype, scope.name] })
    } catch (e) {
      setError(e instanceof ApiError ? (e.details[0]?.message ?? e.message) : 'Could not upload the file.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div id={id} className="attach-input" role="group" aria-label={ariaLabel}>
      {fileId && chosen?.image && <img src={api.files.url(fileId)} alt={chosen.fileName} className="attach-preview" />}
      {fileId && <a href={api.files.url(fileId)} download>{chosen?.fileName ?? 'Download the file'}</a>}
      {!readOnly && (
        <>
          <input type="file" aria-label={fileId ? 'Replace the file' : 'Choose a file'} accept={image ? 'image/png,image/jpeg,image/gif,image/webp' : undefined} disabled={busy} onChange={(e) => { void choose(e.target.files?.[0]); e.target.value = '' }} />
          {fileId && <button type="button" className="btn small" onClick={() => onChange('')}>Remove from this field</button>}
        </>
      )}
      {busy && <small className="muted" role="status">Uploading…</small>}
      {error && <small className="error-text" role="alert">{error}</small>}
    </div>
  )
}
