/** Helpers for the richer field types (rating, duration, colour, place). Pure functions, so they are easy to test. */

export const MAX_STARS = 5

/** "1h 30m", "45s", "2d 3h": the two largest units that are not zero. */
export function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return '0s'
  let rest = Math.floor(seconds)
  const parts: string[] = []
  for (const [unit, size] of [['d', 86400], ['h', 3600], ['m', 60], ['s', 1]] as const) {
    const n = Math.floor(rest / size)
    rest -= n * size
    if (n > 0) parts.push(`${n}${unit}`)
  }
  return parts.slice(0, 2).join(' ')
}

const TEN_YEARS = 315_360_000

/**
 * What a person types for a length of time: "90" (seconds), "1h 30m", "1:30" (hours and minutes), "1:30:15", "2d", "45m".
 * Returns whole seconds, or null when it cannot be understood.
 */
export function parseDuration(text: string): number | null {
  const t = text.trim().toLowerCase()
  if (t === '') return null
  if (/^\d+$/.test(t)) return clamp(Number(t))
  const clock = /^(\d+):(\d{1,2})(?::(\d{1,2}))?$/.exec(t)
  if (clock) return clamp(Number(clock[1]) * 3600 + Number(clock[2]) * 60 + Number(clock[3] ?? 0))
  const units = /^(?:\s*(\d+)\s*([dhms])\s*)+$/.test(t) ? [...t.matchAll(/(\d+)\s*([dhms])/g)] : null
  if (!units) return null
  const size = { d: 86400, h: 3600, m: 60, s: 1 } as const
  return clamp(units.reduce((sum, m) => sum + Number(m[1]) * size[m[2] as keyof typeof size], 0))
}

const clamp = (n: number): number | null => (Number.isSafeInteger(n) && n <= TEN_YEARS ? n : null)

export const isHexColor = (text: string): boolean => /^#[0-9a-fA-F]{6}$/.test(text)

export interface Place {
  lat: number
  lng: number
}

export function parsePlace(text: unknown): Place | null {
  if (typeof text !== 'string' || text === '') return null
  try {
    const value = JSON.parse(text) as { lat?: unknown; lng?: unknown }
    return typeof value.lat === 'number' && typeof value.lng === 'number' && Math.abs(value.lat) <= 90 && Math.abs(value.lng) <= 180 ? { lat: value.lat, lng: value.lng } : null
  } catch {
    return null
  }
}

export const placeText = (place: Place): string => JSON.stringify({ lat: Math.round(place.lat * 1e6) / 1e6, lng: Math.round(place.lng * 1e6) / 1e6 })

/** The place as "24.7136, 46.6753" for showing in lists. */
export const placeLabel = (text: unknown): string => {
  const place = parsePlace(text)
  return place ? `${place.lat}, ${place.lng}` : ''
}

/** A map address for the place. Only numbers go into it, so nothing typed by anyone can change where it points. */
export const mapUrl = (place: Place): string => `https://www.openstreetmap.org/?mlat=${place.lat}&mlon=${place.lng}#map=16/${place.lat}/${place.lng}`

export const starsLabel = (value: unknown): string => {
  const n = Math.max(0, Math.min(MAX_STARS, Math.round(Number(value) || 0)))
  return '★'.repeat(n) + '☆'.repeat(MAX_STARS - n)
}

export const isSignature = (value: unknown): value is string => typeof value === 'string' && value.startsWith('data:image/png;base64,')
