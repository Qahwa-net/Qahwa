# ADR-060: More filter operators and dynamic values

**Status:** accepted (L7)

## Context
Filters knew `=`, `!=`, `>`, `<`, `>=`, `<=`, `like`, `in` and `is set / not set`. People also need "does not contain", "not in", ranges, "this month", and saved filters that mean the same for everyone ("assigned to me", "due today").

## Decision
- **New operators:** `not like`, `not in`, `between` (two values, ends included) and `timespan` (a named period). `not like` and `not in` also match rows where the field is empty (a missing value is not in the list and does not contain the text), which is what people expect and is the same on PostgreSQL and SQL Server.
- **`between` and `timespan` are expanded before a query is built** (`FilterExpansion`) into a lower and an upper bound, so the data layer only learned two new operators (`NotLike`, `NotIn`). A moment filtered *up to a date* means to the end of that day (`<` the next midnight); a timespan on a moment covers whole days. Spans: today, yesterday, tomorrow, this/last/next week (Monday first), month, quarter and year, and the last 7, 30 and 90 and next 7 and 30 days; all relative to today in UTC.
- **Dynamic words:** the exact values `__user` (the person asking), `__today` and `__now` are replaced before conversion, in single values and in lists; nothing else is interpreted (`__user ` or `__username` are ordinary text).
- **Safe by construction:** the operator is looked up in a fixed table (anything else is a validation error on that field); every value is converted for the field's type and bound as a parameter; `between`/`timespan` only on dates, moments and numbers (timespan only dates and moments) with a clear message otherwise; the number of values in one statement is limited per database.
- **LIKE escapes are now the same everywhere:** the Desk writes `\_` and `\%` for a literal underscore or percent; PostgreSQL honours the backslash by default and SQL Server needed `ESCAPE '\'`, which the dialect now adds (this fixed a latent difference between the two products).
- **Desk:** the filter bar offers "does not contain" for text, "between" for numbers, dates and moments, and "in the period" for dates and moments; the address and saved filters carry them (malformed ones are dropped on reading).

## Consequences
- Weeks always start on Monday in filters (the Desk's calendar setting is a display matter); timespans use UTC dates.
- Not yet: relative offsets such as "3 days ago" and a "not between" operator.
