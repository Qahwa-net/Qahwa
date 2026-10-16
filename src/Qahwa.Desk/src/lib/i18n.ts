export const DEFAULT_LANGUAGE = 'en'
export const STORAGE_KEY = 'qahwa.language'

const RTL = new Set(['ar', 'he', 'fa', 'ur', 'ps', 'sd', 'yi'])

/** The words of the Desk's own chrome that ship with it. Translations made in the Desk (Translation documents) come on top of these. */
export const BUILT_IN: Record<string, Record<string, string>> = {
  ar: {
    'My work': 'عملي', Dashboards: 'لوحات المعلومات', 'Auto Repeat': 'التكرار التلقائي', Tools: 'الأدوات',
    'DocType Designer': 'مصمم أنواع المستندات', 'Customize Form': 'تخصيص النموذج', 'Role Permissions': 'صلاحيات الأدوار',
    'Workflow Builder': 'منشئ سير العمل', Webhooks: 'الخطافات', 'Export & Import': 'التصدير والاستيراد', Workspaces: 'مساحات العمل',
    'Print Formats': 'تنسيقات الطباعة', 'Notification Rules': 'قواعد الإشعارات', 'Assignment Rules': 'قواعد الإسناد',
    'Naming Series': 'سلاسل التسمية', 'Sign out': 'تسجيل الخروج', 'Loading…': 'جارٍ التحميل…', Save: 'حفظ', Cancel: 'إلغاء',
    Delete: 'حذف', Language: 'اللغة',
  },
}

export const isValidLanguage = (code: string | null | undefined): code is string => !!code && /^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$/.test(code) && code.length <= 12

export const isRtl = (code: string): boolean => RTL.has(code.split('-')[0] ?? '')

/** The words in a language: the built-in ones, then the ones made in the Desk on top. */
export function mergeMessages(language: string, fromServer: Record<string, string> | undefined): Record<string, string> {
  return { ...(BUILT_IN[language.split('-')[0] ?? ''] ?? {}), ...(fromServer ?? {}) }
}

/** The translated text, or the text itself when there is no translation. Own properties only, so "constructor" is just a word. */
export function translate(messages: Record<string, string>, text: string): string {
  return Object.prototype.hasOwnProperty.call(messages, text) ? messages[text]! : text
}

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
