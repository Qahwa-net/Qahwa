# ADR-133: Plural forms, placeholders and a language kept on the account

**Status:** accepted (L12, follows ADR-033)

## Context
Translations were a flat "this English text shows as that text". Messages with a number ("3 rows") cannot be translated that way (Arabic has six plural forms), and the language choice lived only in one browser, so it was lost on every other device.

## Decision
- **Placeholders and plurals in one message.** A message may hold `{name}` places and, for numbers, plural forms: `one: {count} row | other: {count} rows`. The Desk's `t(text, { count: 3 })` picks the form the language's own rules choose (`Intl.PluralRules`: English has two forms, Arabic six: zero, one, two, few, many, other), falls back to `other` and then to the first form, then fills each `{name}` from the values (own properties only; an unknown name is left as written). The result is plain text, rendered as text, never as markup. A translation is written in the same form (`zero: لا صفوف | one: صف واحد | ...`); with no translation the English message is used in the same way. Messages without values are untouched.
- **The language is kept on the account.** A person's choice is saved on their User (`language`, permission level 9, so it is not visible or editable through the document API) by `PUT /api/auth/language` (a short plain code such as `ar` or `pt-BR`, or empty to clear it; not for API keys). Sign-in and `/api/auth/me` return it, and the Desk applies it when it arrives, so the choice follows the person to every device. Switching language in the top bar saves it; the browser's own copy still shows the right language on the sign-in page. The site's default language is used only until someone chooses.

## Consequences
- Adopted so far in the import dialog; other counts in the Desk still say "row(s)" in English and are moved over as they are touched.
- Not yet: translating every button and message, import and export of translations as files, translating print formats and emails.
