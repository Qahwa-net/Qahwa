# ADR-121: Rules across the rows of a table

**Status:** accepted (L4)

## Context
Rules and formulas could add up a column (`sum`) and count rows, but not say "each item only once", "no quantity below 1" or "tax lines under 1000". Those are the common cross-row checks of order, invoice and timesheet forms.

## Decision
Six functions are added to the expression language, in the C# engine and the TypeScript engine alike, and pinned by 26 more shared vectors (246 in all):

- `avg(table, 'field')`, `minOf(table, 'field')`, `maxOf(table, 'field')`: over the rows that have a number in the field; no value when there are none.
- `countIf(table, 'field', value)`: rows where the field equals the value (the language's strict equality, so `4` is not `'4'`, and `null` matches empty).
- `sumIf(table, 'field', 'matchField', value)`: the sum of one field over the rows where another equals the value.
- `distinct(table, 'field')` (or of a plain list): the different non-empty values in first-seen order. `len(distinct(...)) == count(...)` is "no duplicates".

They keep the language's rules: no loops the author can write, nothing outside the document in front of it, bad input gives no value instead of an error, arity is checked when the DocType is saved. They work in rules, formulas and conditions, and appear in the Designer's function help.

## Consequences
- Rows are compared only by field name and value; there is no "any row where" or "all rows where" form yet. `countIf(...) == 0` and `minOf(...) > 0` cover most of what those would be used for.
- Not yet: reading other documents (formulas across documents).
