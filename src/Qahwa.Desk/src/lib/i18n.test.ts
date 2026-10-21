import { describe, expect, it } from 'vitest'
import { DEFAULT_LANGUAGE, isRtl, isValidLanguage, mergeMessages, readLanguage, translate } from './i18n.ts'

describe('i18n helpers', () => {
  it('knows right-to-left languages, with or without a region', () => {
    expect(isRtl('ar')).toBe(true)
    expect(isRtl('ar-SA')).toBe(true)
    expect(isRtl('en')).toBe(false)
    expect(isRtl('fr-CA')).toBe(false)
  })

  it('accepts only plain language codes', () => {
    expect(isValidLanguage('pt-BR')).toBe(true)
    for (const bad of ['', null, undefined, "ar'; --", '../x', 'A', 'toolonglanguagecode', '<b>']) expect(isValidLanguage(bad as string)).toBe(false)
  })

  it('puts translations made in the Desk over the built-in words', () => {
    const messages = mergeMessages('ar', { Save: 'احفظ', Invoice: 'فاتورة' })
    expect(messages.Save).toBe('احفظ')
    expect(messages.Cancel).toBe('إلغاء')
    expect(messages.Invoice).toBe('فاتورة')
    expect(mergeMessages('fr', undefined)).toEqual({})
  })

  it('falls back to the text itself and treats odd words as plain words', () => {
    expect(translate({ Save: 'حفظ' }, 'Save')).toBe('حفظ')
    expect(translate({ Save: 'حفظ' }, 'Open')).toBe('Open')
    expect(translate({}, 'constructor')).toBe('constructor')
    expect(translate({}, '__proto__')).toBe('__proto__')
  })

  it('reads the saved language and survives a broken or hostile store', () => {
    expect(readLanguage({ getItem: () => 'ar' })).toBe('ar')
    expect(readLanguage({ getItem: () => '<script>' })).toBe(DEFAULT_LANGUAGE)
    expect(readLanguage({ getItem: () => null })).toBe(DEFAULT_LANGUAGE)
    expect(readLanguage({ getItem: () => { throw new Error('blocked') } })).toBe(DEFAULT_LANGUAGE)
    expect(readLanguage(undefined)).toBe(DEFAULT_LANGUAGE)
  })
})

describe('values of translatable fields', () => {
  it('are shown in the language of the person, and other fields are left alone', async () => {
    const { setValueTranslator } = await import('./i18n.ts')
    const { formatCell } = await import('./fields.ts')
    setValueTranslator((text) => ({ Open: 'مفتوح' })[text] ?? text)
    const field = (translatable: boolean) => ({ name: 'status', type: 'Select', translatable }) as never
    expect(formatCell(field(true), 'Open')).toBe('مفتوح')
    expect(formatCell(field(true), 'Unknown')).toBe('Unknown')
    expect(formatCell(field(false), 'Open')).toBe('Open')
    setValueTranslator((text) => text)
  })
})
