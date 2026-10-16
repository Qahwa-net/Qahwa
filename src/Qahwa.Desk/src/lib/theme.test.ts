import { describe, expect, it } from 'vitest'
import {
  brandVariables, contrastRatio, lightenForDark, logoFileProblem, mix, parseBrand, readPref, readableOn, resolveScheme, safeLogo, writePref,
} from './theme.ts'

describe('theme choice', () => {
  it('uses the person\'s choice, then the organisation\'s default, and "auto" follows the system', () => {
    expect(resolveScheme('light', 'dark', true)).toBe('light')
    expect(resolveScheme('dark', 'light', false)).toBe('dark')
    expect(resolveScheme(null, 'dark', false)).toBe('dark')
    expect(resolveScheme(null, 'auto', true)).toBe('dark')
    expect(resolveScheme('auto', 'light', true)).toBe('dark')
    expect(resolveScheme(null, 'auto', false)).toBe('light')
  })

  it('remembers the choice and ignores anything else in storage', () => {
    const data = new Map<string, string>()
    const storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v), removeItem: (k: string) => void data.delete(k) }
    expect(readPref(storage)).toBeNull()
    writePref('dark', storage)
    expect(readPref(storage)).toBe('dark')
    data.set('qahwa.theme', '<script>')
    expect(readPref(storage)).toBeNull()
    writePref('light', storage)
    writePref(null, storage)
    expect(readPref(storage)).toBeNull()
  })

  it('survives storage that throws', () => {
    const broken = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') }, removeItem: () => { throw new Error('blocked') } }
    expect(readPref(broken)).toBeNull()
    expect(() => writePref('dark', broken)).not.toThrow()
  })
})

describe('brand colours', () => {
  it('picks readable text for any brand colour', () => {
    expect(readableOn('#ffffff')).toBe('#111111')
    expect(readableOn('#000000')).toBe('#ffffff')
    expect(readableOn('#6f4e37')).toBe('#ffffff')
    expect(readableOn('#ffd84d')).toBe('#111111')
    for (const c of ['#a11d33', '#0a7d5a', '#f5a623', '#2255cc', '#cccccc', '#333333']) expect(contrastRatio(c, readableOn(c))).toBeGreaterThanOrEqual(4.5)
  })

  it('mixes colours', () => {
    expect(mix('#000000', '#ffffff', 0.5)).toBe('#808080')
    expect(mix('#102030', '#ffffff', 0)).toBe('#102030')
    expect(mix('#102030', '#ffffff', 1)).toBe('#ffffff')
  })

  it('makes variables for the light and dark pages and nothing for a missing or bad colour', () => {
    const light = brandVariables({ color: '#A11D33', color_dark: null }, 'light')
    expect(light['--brand']).toBe('#a11d33')
    expect(light['--brand-text']).toBe('#ffffff')
    expect(light['--accent-soft']).toMatch(/^#[0-9a-f]{6}$/)
    expect(brandVariables({ color: null, color_dark: null }, 'light')).toEqual({})
    expect(brandVariables({ color: 'red', color_dark: 'url(x)' }, 'dark')).toEqual({})
  })

  it('never leaves a dark brand colour unreadable on the dark page', () => {
    const dark = brandVariables({ color: '#1a237e', color_dark: null }, 'dark')
    expect(contrastRatio(dark['--brand']!, '#211d1a')).toBeGreaterThanOrEqual(4.5)
    expect(lightenForDark('#ffffff')).toBe('#ffffff')
    expect(brandVariables({ color: '#1a237e', color_dark: '#ff8899' }, 'dark')['--brand']).toBe('#ff8899') // an explicit dark colour wins
  })
})

describe('logo and brand data from the server', () => {
  it('accepts only embedded pictures for an image tag', () => {
    expect(safeLogo('data:image/png;base64,iVBORw0KGgo=')).not.toBeNull()
    expect(safeLogo('https://evil.example/x.png')).toBeNull()
    expect(safeLogo('javascript:alert(1)')).toBeNull()
    expect(safeLogo('data:text/html;base64,PHNjcmlwdD4=')).toBeNull()
    expect(safeLogo('data:image/png;base64,abc" onerror="alert(1)')).toBeNull()
    expect(safeLogo(null)).toBeNull()
  })

  it('checks a chosen file before sending it', () => {
    expect(logoFileProblem({ type: 'image/png', size: 1000 })).toBeNull()
    expect(logoFileProblem({ type: 'image/svg+xml', size: 1000 })).toBeNull()
    expect(logoFileProblem({ type: 'text/html', size: 10 })).toContain('Choose')
    expect(logoFileProblem({ type: 'image/png', size: 200 * 1024 })).toContain('too large')
  })

  it('falls back to the default for anything unusable', () => {
    expect(parseBrand(null)).toEqual({ name: 'Qahwa', logo: null, color: null, color_dark: null, theme: 'auto', login_message: null })
    const brand = parseBrand({ name: 'Acme', logo: 'http://x/y.png', color: '#A11D33', color_dark: 'nope', theme: 'dark', login_message: 'Hi' })
    expect(brand).toEqual({ name: 'Acme', logo: null, color: '#A11D33', color_dark: null, theme: 'dark', login_message: 'Hi' })
    expect(parseBrand({ name: 'x'.repeat(61), theme: 'neon' })).toMatchObject({ name: 'Qahwa', theme: 'auto' })
  })
})
