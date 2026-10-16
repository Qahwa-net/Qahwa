import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from './api/client.ts'
import {
  DEFAULT_BRAND, applyBrandVariables, brandVariables, parseBrand, readPref, resolveScheme, writePref, type Brand, type Scheme, type ThemePref,
} from './lib/theme.ts'

const BRAND_CACHE = 'qahwa.brand'

interface ThemeValue {
  brand: Brand
  /** The person's own choice, or null when they follow the organisation's default. */
  pref: ThemePref | null
  scheme: Scheme
  setPref: (pref: ThemePref | null) => void
}

const ThemeContext = createContext<ThemeValue>({ brand: DEFAULT_BRAND, pref: null, scheme: 'light', setPref: () => {} })

export const useTheme = () => useContext(ThemeContext)

/** The last brand seen, so the page does not flash its default name and colours while the brand loads. */
function cachedBrand(): Brand {
  try {
    const text = localStorage.getItem(BRAND_CACHE)
    return text ? parseBrand(JSON.parse(text)) : DEFAULT_BRAND
  } catch {
    return DEFAULT_BRAND
  }
}

const systemDark = (): boolean => typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches

/**
 * Light, dark or automatic appearance, and the organisation's brand (name, logo, colours, default theme). The person's choice is kept
 * in this browser; the brand comes from the server (it is public: the sign-in page uses it too) and every value is checked again here
 * before it is used.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const query = useQuery({ queryKey: ['brand'], queryFn: api.brand, staleTime: 60_000, retry: false })
  const [cached] = useState(cachedBrand)
  const brand = useMemo(() => (query.data ? parseBrand(query.data) : cached), [query.data, cached])
  const [pref, setPrefState] = useState<ThemePref | null>(() => readPref())
  const [dark, setDark] = useState(systemDark)

  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const listener = () => setDark(media.matches)
    media.addEventListener('change', listener)
    return () => media.removeEventListener('change', listener)
  }, [])

  const scheme = resolveScheme(pref, brand.theme, dark)

  useEffect(() => {
    const root = document.documentElement
    if ((pref ?? brand.theme) === 'auto') root.removeAttribute('data-theme')
    else root.setAttribute('data-theme', scheme)
    applyBrandVariables(root, brandVariables(brand, scheme))
  }, [brand, pref, scheme])

  useEffect(() => {
    document.title = brand.name
  }, [brand.name])

  useEffect(() => {
    if (!query.data) return
    try {
      localStorage.setItem(BRAND_CACHE, JSON.stringify(brand))
    } catch {
      // The cache only avoids a flash.
    }
  }, [query.data, brand])

  const setPref = useCallback((next: ThemePref | null) => {
    writePref(next)
    setPrefState(next)
  }, [])

  const value = useMemo(() => ({ brand, pref, scheme, setPref }), [brand, pref, scheme, setPref])
  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
}
