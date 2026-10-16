export type ThemePref = 'auto' | 'light' | 'dark'
export type Scheme = 'light' | 'dark'

export interface Brand {
  name: string
  logo: string | null
  color: string | null
  color_dark: string | null
  theme: ThemePref
  login_message: string | null
}

export const DEFAULT_BRAND: Brand = { name: 'Qahwa', logo: null, color: null, color_dark: null, theme: 'auto', login_message: null }

const STORAGE_KEY = 'qahwa.theme'

export const isThemePref = (value: unknown): value is ThemePref => value === 'auto' || value === 'light' || value === 'dark'

/** The person's own choice, kept in this browser; null when they have not chosen (storage can be unavailable or hold anything). */
export function readPref(storage: Pick<Storage, 'getItem'> | undefined = safeStorage()): ThemePref | null {
  try {
    const value = storage?.getItem(STORAGE_KEY)
    return isThemePref(value) ? value : null
  } catch {
    return null
  }
}

export function writePref(pref: ThemePref | null, storage: Pick<Storage, 'setItem' | 'removeItem'> | undefined = safeStorage()): void {
  try {
    if (pref === null) storage?.removeItem(STORAGE_KEY)
    else storage?.setItem(STORAGE_KEY, pref)
  } catch {
    // Not being able to remember the choice is not worth an error.
  }
}

function safeStorage(): Storage | undefined {
  try {
    return typeof localStorage === 'undefined' ? undefined : localStorage
  } catch {
    return undefined
  }
}

/** What to show: the person's choice, else the organisation's default; "auto" follows the operating system. */
export function resolveScheme(pref: ThemePref | null, brandDefault: ThemePref, systemDark: boolean): Scheme {
  const effective = pref ?? brandDefault
  return effective === 'auto' ? (systemDark ? 'dark' : 'light') : effective
}

export const isHexColor = (value: unknown): value is string => typeof value === 'string' && /^#[0-9a-fA-F]{6}$/.test(value)

function channels(hex: string): [number, number, number] {
  return [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)]
}

const toHex = (c: number[]): string => '#' + c.map((v) => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, '0')).join('')

/** Relative luminance as defined for contrast ratios (WCAG 2). */
export function luminance(hex: string): number {
  const [r, g, b] = channels(hex).map((v) => {
    const s = v / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  }) as [number, number, number]
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

export function contrastRatio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number]
  return (hi + 0.05) / (lo + 0.05)
}

/** Text that can be read on this colour: whichever of near-white and near-black has more contrast. */
export function readableOn(hex: string): string {
  return contrastRatio(hex, '#ffffff') >= contrastRatio(hex, '#111111') ? '#ffffff' : '#111111'
}

/** `amount` of `other` mixed into `hex` (0 keeps hex, 1 gives other). */
export function mix(hex: string, other: string, amount: number): string {
  const a = channels(hex)
  const b = channels(other)
  return toHex(a.map((v, i) => v + (b[i]! - v) * amount))
}

const SURFACE: Record<Scheme, string> = { light: '#ffffff', dark: '#211d1a' }

/**
 * The colour variables for a brand colour: the brand colour itself, text that reads on it, and a soft tint for highlights.
 * In the dark scheme the dark colour is used, or a lightened version of the brand colour when none was given, so a dark brand colour
 * never disappears into a dark page. Returns nothing for a missing or invalid colour (the stylesheet's own colours stay).
 */
export function brandVariables(brand: Pick<Brand, 'color' | 'color_dark'>, scheme: Scheme): Record<string, string> {
  const base = scheme === 'dark' ? (isHexColor(brand.color_dark) ? brand.color_dark : isHexColor(brand.color) ? lightenForDark(brand.color) : null) : isHexColor(brand.color) ? brand.color : null
  if (!base) return {}
  const color = base.toLowerCase()
  return { '--brand': color, '--brand-text': readableOn(color), '--accent-soft': mix(color, SURFACE[scheme], scheme === 'dark' ? 0.78 : 0.86) }
}

/** A dark colour on a dark page is unreadable: bring its lightness up until it has enough contrast with the dark surface. */
export function lightenForDark(hex: string): string {
  let color = hex.toLowerCase()
  for (let i = 0; i < 10 && contrastRatio(color, SURFACE.dark) < 4.5; i++) color = mix(color, '#ffffff', 0.15)
  return color
}

export const BRAND_VARIABLES = ['--brand', '--brand-text', '--accent-soft'] as const

/** Applies the colours to the page (and removes any earlier ones). */
export function applyBrandVariables(root: HTMLElement, vars: Record<string, string>): void {
  for (const name of BRAND_VARIABLES) {
    const value = vars[name]
    if (value) root.style.setProperty(name, value)
    else root.style.removeProperty(name)
  }
}

const LOGO_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/svg+xml']
export const MAX_LOGO_BYTES = 100 * 1024

/** Why a chosen logo file cannot be used, or null. The server checks the picture again; this only saves a round trip. */
export function logoFileProblem(file: { type: string; size: number }): string | null {
  if (!LOGO_TYPES.includes(file.type)) return 'Choose a PNG, JPEG, GIF, WebP or SVG picture.'
  if (file.size > MAX_LOGO_BYTES) return `The picture is too large (at most ${MAX_LOGO_BYTES / 1024} KB).`
  return null
}

/** Only an embedded picture is ever put in an image tag. */
export const safeLogo = (logo: string | null | undefined): string | null =>
  typeof logo === 'string' && /^data:image\/(png|jpeg|gif|webp|svg\+xml);base64,[A-Za-z0-9+/]+={0,2}$/.test(logo) ? logo : null

export function parseBrand(raw: unknown): Brand {
  const o = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>
  return {
    name: typeof o.name === 'string' && o.name.trim() !== '' && o.name.length <= 60 ? o.name : DEFAULT_BRAND.name,
    logo: safeLogo(o.logo as string | null),
    color: isHexColor(o.color) ? o.color : null,
    color_dark: isHexColor(o.color_dark) ? o.color_dark : null,
    theme: isThemePref(o.theme) ? o.theme : 'auto',
    login_message: typeof o.login_message === 'string' && o.login_message !== '' && o.login_message.length <= 300 ? o.login_message : null,
  }
}
