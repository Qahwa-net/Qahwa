import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from './api/client.ts'
import { DEFAULT_LANGUAGE, STORAGE_KEY, isRtl, isValidLanguage, mergeMessages, readLanguage, setValueTranslator, translate, type Vars } from './lib/i18n.ts'

interface I18n {
  language: string
  setLanguage: (code: string) => void
  /** The site's default language: used only until the person has chosen one themselves. */
  applyDefault: (code: string) => void
  /** The language the person chose on their account (any device): it wins over this browser's own choice. */
  applyUser: (code: string) => void
  /** The text in the person's language, with {name} values and plural forms filled in (see format()). */
  t: (text: string, vars?: Vars) => string
}

const Context = createContext<I18n>({ language: DEFAULT_LANGUAGE, setLanguage: () => {}, applyDefault: () => {}, applyUser: () => {}, t: (text, vars) => translate({}, text, vars) })

const hasChosen = (): boolean => {
  try {
    return localStorage.getItem(STORAGE_KEY) !== null
  } catch {
    return false
  }
}

/** The language of the Desk's words (a per-person choice kept in this browser) and the right-to-left layout that goes with it. */
export function I18nProvider({ children }: { children: ReactNode }) {
  const [language, setLanguageState] = useState(() => readLanguage(typeof localStorage === 'undefined' ? undefined : localStorage))
  const catalog = useQuery({
    queryKey: ['translations', language],
    queryFn: () => api.translations.catalog(language),
    enabled: language !== DEFAULT_LANGUAGE,
    retry: false,
    staleTime: 60_000,
  })

  useEffect(() => {
    document.documentElement.lang = language
    document.documentElement.dir = isRtl(language) ? 'rtl' : 'ltr'
  }, [language])

  const value = useMemo<I18n>(() => {
    const messages = mergeMessages(language, catalog.data?.messages)
    return {
      language,
      t: (text, vars) => translate(messages, text, vars, language),
      applyDefault: (code) => {
        if (isValidLanguage(code) && !hasChosen()) setLanguageState(code)
      },
      applyUser: (code) => {
        if (!isValidLanguage(code)) return
        setLanguageState(code)
        try {
          localStorage.setItem(STORAGE_KEY, code)
        } catch {
          // Not remembered in this browser; the account still has it.
        }
      },
      setLanguage: (code) => {
        if (!isValidLanguage(code)) return
        setLanguageState(code)
        try {
          localStorage.setItem(STORAGE_KEY, code)
        } catch {
          // The choice just is not remembered.
        }
        void api.account.setLanguage(code).catch(() => { /* not signed in yet, or offline: the choice stays in this browser */ })
      },
    }
  }, [language, catalog.data])

  // Values of translatable fields are shown through the same words (lists and previews call this without a hook).
  useEffect(() => { setValueTranslator(value.t) }, [value])

  return <Context.Provider value={value}>{children}</Context.Provider>
}

export const useI18n = (): I18n => useContext(Context)
export const useT = (): ((text: string, vars?: Vars) => string) => useContext(Context).t
