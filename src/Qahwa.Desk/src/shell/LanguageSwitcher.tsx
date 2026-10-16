import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client.ts'
import { useI18n } from '../i18n.tsx'
import { BUILT_IN, DEFAULT_LANGUAGE, languageName } from '../lib/i18n.ts'

/** Chooses the language of the Desk's words: English, the built-in languages and any language that has translations. */
export function LanguageSwitcher() {
  const { language, setLanguage, t } = useI18n()
  const served = useQuery({ queryKey: ['translations', 'languages'], queryFn: api.translations.list, retry: false, staleTime: 60_000 })
  const codes = [...new Set([DEFAULT_LANGUAGE, ...Object.keys(BUILT_IN), ...(served.data ?? []), language])]
  if (codes.length < 2) return null

  return (
    <select className="lang-select" aria-label={t('Language')} value={language} onChange={(e) => setLanguage(e.target.value)}>
      {codes.map((code) => <option key={code} value={code}>{languageName(code)}</option>)}
    </select>
  )
}
