# ADR-063: Naming formats with dates and fields, and Duplicate

**Status:** accepted (L6, L7)

## Context
Series names (`CUST-.#####`) were fixed text plus a counter. Businesses number invoices per year (`INV-2026-0001`, restarting each January) or per branch (`RIY-0001`), and people often want a new document that starts as a copy of an old one.

## Decision
- **Tokens in the series prefix:** `{YYYY}`, `{YY}`, `{MM}`, `{DD}` (today, UTC) and `{fieldname}` (a field of the document being named), for example `INV-{YYYY}-.####` or `{branch}-{YY}{DD}-.###`. The name is made from the resolved prefix and the counter of *that* prefix, so each year, month or branch counts on its own and a year that has not started has no counter.
- **A field in a name is untrusted text.** It is required; only letters, digits, `-`, `_` and `/` are kept (everything else is dropped), it is cut to 30 characters, and a value with nothing usable is a validation error on that field. The result is also the name of the counter, so nothing outside that character set can ever reach the series table (which is still written through bound parameters).
- **Checked when the DocType is defined:** tokens are matched by a strict pattern; a field token must name a field of the DocType; the rest of the series rules are unchanged (up to 10 `#`). The Designer shows a hint.
- **The Naming Series page** lists the counters that came out of a pattern under the DocType that produced them (matched by a pattern built from the tokens, with a time limit), never the pattern itself; only counters that exist can be set.
- **Duplicate** is a Desk feature: the form's Duplicate button opens a new form (`/app/X/new?copy=NAME`) filled in from the document, leaving out unique fields, passwords and system fields; table rows are copied as new rows. Nothing is saved until Save, and the new document is validated, permission-checked and named like any other.

## Consequences
- Date tokens use UTC, so a document made just after local midnight may carry the previous (or next) day.
- A counter cannot be shared between patterns that produce different prefixes, and renaming a field used in a pattern needs the pattern changed too.
- Not yet: Document Naming Rules (rule tables choosing a pattern by condition) and naming by expression.
