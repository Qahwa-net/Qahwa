import { useTheme } from '../theme.tsx'
import type { ThemePref } from '../lib/theme.ts'

const NEXT: Record<ThemePref, ThemePref> = { auto: 'light', light: 'dark', dark: 'auto' }
const ICON: Record<ThemePref, string> = { auto: '◐', light: '☀', dark: '☾' }
const LABEL: Record<ThemePref, string> = { auto: 'Theme: automatic', light: 'Theme: light', dark: 'Theme: dark' }

/** One button that goes round automatic, light and dark. */
export function ThemeToggle() {
  const { pref, setPref, brand } = useTheme()
  const current: ThemePref = pref ?? brand.theme
  return (
    <button type="button" className="btn icon" aria-label={`${LABEL[current]}. Press to change.`} title={LABEL[current]} onClick={() => setPref(NEXT[current])}>
      <span aria-hidden="true">{ICON[current]}</span>
    </button>
  )
}
