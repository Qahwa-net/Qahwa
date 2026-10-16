export interface DialCountry {
  name: string
  /** Digits only, for example "966". */
  dial: string
}

/** The countries that have a dialling code, from the Country list (rows without one are left out), by name. */
export function dialCountries(rows: Record<string, unknown>[]): DialCountry[] {
  return rows
    .map((r) => ({ name: String(r.name ?? ''), dial: String(r.dial_code ?? '').replace(/\D/g, '') }))
    .filter((c) => c.name !== '' && c.dial !== '')
    .sort((a, b) => a.name.localeCompare(b.name))
}

/**
 * A stored phone number as a country and the rest: "+966 50 123 4567" with the longest dialling code that starts it
 * (Saudi Arabia, not a country with code "9"); a number that does not start with "+" has no country.
 */
export function splitPhone(value: string, countries: DialCountry[]): { country: DialCountry | null; rest: string } {
  const text = value.trim()
  if (!text.startsWith('+')) return { country: null, rest: value }
  const digits = text.slice(1).replace(/^\s+/, '')
  let best: DialCountry | null = null
  for (const c of countries) {
    if (digits.startsWith(c.dial) && (best === null || c.dial.length > best.dial.length)) best = c
  }
  return best ? { country: best, rest: digits.slice(best.dial.length).trim() } : { country: null, rest: value }
}

/** The stored form: "+966 50 123 4567" (or just the number when no country is chosen). */
export function joinPhone(country: DialCountry | null, rest: string): string {
  const number = rest.trim()
  if (!country) return number
  return number === '' ? '' : `+${country.dial} ${number}`
}
