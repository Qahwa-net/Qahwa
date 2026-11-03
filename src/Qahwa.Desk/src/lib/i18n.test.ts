import { describe, expect, it } from 'vitest'
import { BUILT_IN, DEFAULT_LANGUAGE, format, isRtl, isValidLanguage, mergeMessages, readLanguage, translate } from './i18n.ts'

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

describe('placeholders and plural forms', () => {
  const rows = 'one: {count} row | other: {count} rows'

  it('puts values into {name} places and leaves unknown names and look-alike words alone', () => {
    expect(format('Hello {name}, you have {n} messages', { name: 'Ann', n: 3 })).toBe('Hello Ann, you have 3 messages')
    expect(format('{missing} stays', { x: 1 })).toBe('{missing} stays')
    expect(format('{constructor} is only a word', {})).toBe('{constructor} is only a word') // own properties only
    expect(format('no placeholders', { name: 'x' })).toBe('no placeholders')
  })

  it('chooses the English plural form from the count', () => {
    expect(format(rows, { count: 1 }, 'en')).toBe('1 row')
    expect(format(rows, { count: 0 }, 'en')).toBe('0 rows')
    expect(format(rows, { count: 5 }, 'en')).toBe('5 rows')
  })

  it('uses the six Arabic forms from the language rules', () => {
    const ar = BUILT_IN.ar![rows]!
    const say = (count: number) => format(ar, { count }, 'ar')
    expect(say(0)).toBe('لا صفوف')
    expect(say(1)).toBe('صف واحد')
    expect(say(2)).toBe('صفان')
    expect(say(3)).toBe('3 صفوف')
    expect(say(11)).toBe('11 صفًا')
    expect(say(100)).toBe('100 صف')
  })

  it('falls back sensibly: the "other" form for a missing category, the first form without a count, plain text for plain text', () => {
    expect(format('other: {count} things', { count: 1 }, 'en')).toBe('1 things') // no "one" form given
    expect(format(rows, {}, 'en')).toBe('{count} row')
    expect(format('a | b', { count: 2 }, 'en')).toBe('a | b') // a bar that is not a plural list
    expect(format(rows, { count: 2 }, 'not a language!')).toBe('2 rows')
  })

  it('translate() fills in the translated message, or the English one when there is no translation', () => {
    const messages = { [rows]: BUILT_IN.ar![rows]! }
    expect(translate(messages, rows, { count: 2 }, 'ar')).toBe('صفان')
    expect(translate({}, rows, { count: 2 }, 'ar')).toBe('2 rows') // English text, whatever the language
    expect(translate({}, 'Save')).toBe('Save') // no values: untouched, as before
  })
})

