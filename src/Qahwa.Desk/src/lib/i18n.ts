export const DEFAULT_LANGUAGE = 'en'
export const STORAGE_KEY = 'qahwa.language'

const RTL = new Set(['ar', 'he', 'fa', 'ur', 'ps', 'sd', 'yi'])

/** The words of the Desk's own chrome that ship with it. Translations made in the Desk (Translation documents) come on top of these. */
export const BUILT_IN: Record<string, Record<string, string>> = {
  ar: {
    'My work': 'عملي', Dashboards: 'لوحات المعلومات', Reports: 'التقارير', 'Auto Repeat': 'التكرار التلقائي', Tools: 'الأدوات', Settings: 'الإعدادات',
    'DocType Designer': 'مصمم أنواع المستندات', 'Customize Form': 'تخصيص النموذج', 'Role Permissions': 'صلاحيات الأدوار',
    'Workflow Builder': 'منشئ سير العمل', Webhooks: 'الخطافات', 'Export & Import': 'التصدير والاستيراد', Workspaces: 'مساحات العمل',
    'Print Formats': 'تنسيقات الطباعة', 'Notification Rules': 'قواعد الإشعارات', 'Assignment Rules': 'قواعد الإسناد',
    'Naming Series': 'سلاسل التسمية', 'Sign out': 'تسجيل الخروج', 'Loading…': 'جارٍ التحميل…', Save: 'حفظ', Cancel: 'إلغاء',
    Delete: 'حذف', Language: 'اللغة',
    'one: {count} row | other: {count} rows': 'zero: لا صفوف | one: صف واحد | two: صفان | few: {count} صفوف | many: {count} صفًا | other: {count} صف',
  },
}

export const isValidLanguage = (code: string | null | undefined): code is string => !!code && /^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$/.test(code) && code.length <= 12

export const isRtl = (code: string): boolean => RTL.has(code.split('-')[0] ?? '')

/** The words in a language: the built-in ones, then the ones made in the Desk on top. */
export function mergeMessages(language: string, fromServer: Record<string, string> | undefined): Record<string, string> {
  return { ...(BUILT_IN[language.split('-')[0] ?? ''] ?? {}), ...(fromServer ?? {}) }
}

/** The translated text, or the text itself when there is no translation. Own properties only, so "constructor" is just a word. */
export function translate(messages: Record<string, string>, text: string, vars?: Vars, language = DEFAULT_LANGUAGE): string {
  const found = Object.prototype.hasOwnProperty.call(messages, text) ? messages[text]! : text
  return vars === undefined ? found : format(found, vars, language)
}

/** Values to put into a message: {name} becomes the value, and a number called count picks the plural form. */
export type Vars = Record<string, string | number>

const CATEGORIES = new Set(['zero', 'one', 'two', 'few', 'many', 'other'])

/** "one: {count} row | other: {count} rows" as a list of [category, text], or null when the message has no plural forms. */
function pluralForms(text: string): [string, string][] | null {
  const parts = text.split('|').map((p) => p.trim())
  const forms: [string, string][] = []
  for (const part of parts) {
    const colon = part.indexOf(':')
    const category = colon > 0 ? part.slice(0, colon).trim() : ''
    if (!CATEGORIES.has(category)) return null
    forms.push([category, part.slice(colon + 1).trim()])
  }
  return forms.length > 0 ? forms : null
}

/**
 * Fills a message in: with plural forms and a `count`, the form the language's plural rules choose (Arabic has six, English two); then each
 * `{name}` is replaced by the matching value (own properties only; an unknown name is left as it is). The result is plain text.
 */
export function format(text: string, vars: Vars, language = DEFAULT_LANGUAGE): string {
  let chosen = text
  const forms = typeof vars.count === 'number' ? pluralForms(text) : null
  if (forms) {
    let category = 'other'
    try {
      category = new Intl.PluralRules(language).select(vars.count as number)
    } catch {
      // an unknown language code: the "other" form
    }
    chosen = (forms.find(([c]) => c === category) ?? forms.find(([c]) => c === 'other') ?? forms[0]!)[1]
  } else if (pluralForms(text)) {
    chosen = pluralForms(text)![0]![1] // forms but no count: the first one
  }

  return chosen.replace(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (whole, name: string) => (Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : whole))
}

let valueTranslator: (text: string) => string = (text) => text

/** Set by the language provider, so values of translatable fields can be shown in the person's language anywhere (lists, previews). */
export function setValueTranslator(fn: (text: string) => string): void {
  valueTranslator = fn
}

export const translateValue = (text: string): string => valueTranslator(text)

export function readLanguage(storage: Pick<Storage, 'getItem'> | undefined): string {
  try {
    const saved = storage?.getItem(STORAGE_KEY)
    return isValidLanguage(saved) ? saved : DEFAULT_LANGUAGE
  } catch {
    return DEFAULT_LANGUAGE
  }
}

export function languageName(code: string): string {
  try {
    return new Intl.DisplayNames([code], { type: 'language' }).of(code) ?? code
  } catch {
    return code
  }
}
