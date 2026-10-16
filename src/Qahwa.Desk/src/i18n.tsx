import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from './api/client.ts'
import { DEFAULT_LANGUAGE, STORAGE_KEY, isRtl, isValidLanguage, mergeMessages, readLanguage, translate } from './lib/i18n.ts'

interface I18n {
  language: string
  setLanguage: (code: string) => void
  t: (text: string) => string
}

const Context = createContext<I18n>({ language: DEFAULT_LANGUAGE, setLanguage: () => {}, t: (text) => text })

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
      t: (text) => translate(messages, text),
      setLanguage: (code) => {
        if (!isValidLanguage(code)) return
        setLanguageState(code)
        try {
          localStorage.setItem(STORAGE_KEY, code)
        } catch {
          // The choice just is not remembered.
        }
      },
    }
  }, [language, catalog.data])

  return <Context.Provider value={value}>{children}</Context.Provider>
}

export const useI18n = (): I18n => useContext(Context)
export const useT = (): ((text: string) => string) => useContext(Context).t
