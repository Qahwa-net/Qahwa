# ADR-033: Translations and right-to-left layout

**Status:** accepted (L12, part 3)

## Context
People should be able to use the Desk in their own language, including languages written right to left (Arabic, Hebrew), and a System Manager should be able to add a language without a developer.

## Decision
- **Translations are data.** The Core DocType `Translation` (language code, source text in English, translated text) is edited like any document by System Managers only. Translating means "this exact English text shows as that text".
- **One read endpoint.** `GET /api/translations` lists the languages that have translations; `GET /api/translations/{language}` returns `{language, rtl, messages}`. Any signed-in user may read it (it is interface text, never document data); guests get 401. The language code is validated (`^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$`, 12 characters at most) before it reaches the query, which is parameterised anyway. At most 5000 entries are returned and a later translation of the same text wins.
- **The choice is per person and per browser** (`localStorage`, validated when read), not a server setting, so it is a convenience that can never break a session. `I18nProvider` loads the catalog, sets `<html lang>` and `dir` (right to left for ar, he, fa, ur, ps, sd, yi) and offers `t(text)`.
- **Built-in words.** The Desk's own chrome ships with Arabic; translations made in the Desk are laid over them. A missing translation shows the English text. Lookups use own properties only, so a word like "constructor" is just a word.
- **Safe by construction.** Translations are rendered as React text, never as HTML, so markup in a translation is shown, not run. Right-to-left works because the stylesheet uses logical properties (start/end, inline-start/inline-end) instead of left/right.
- Translated now: the sidebar and top bar, DocType and module names, field labels.

## Consequences
- Adding a language needs no deployment: add Translation documents, pick the language in the top bar.
- Not yet: translating every button and message in the Desk (only the chrome and field labels so far), plural forms and placeholders, per-user language stored on the user, import/export of translations as files, translating print formats and emails, mirrored arrow icons.
