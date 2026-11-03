# ADR-151: Filtering a list through a table inside a table

**Status:** accepted

## Context
ADR-123 let a list be filtered by a field of a child table (`lines.item`). ADR-147 added tables inside the rows of a table, but their rows could not be used in a filter.

## Decision
- A filter field may go one level deeper: `lines.parts.qty`. It means: the document has a line that has a part where the condition holds. Every operator works, as for any filter.
- **Same row at every level.** Conditions on the line (`lines.title`) and on its parts (`lines.parts.part_name`, `lines.parts.qty`) are gathered into one match: they must hold for the *same* line, and the part conditions for the *same* part. In SQL this is a nested `name IN (SELECT parent ...)`, one per level, every value a parameter and every identifier taken from the DocType columns; the in-memory repository used in tests matches the same way.
- **Same permission rules as ADR-123**, applied at each level: the table fields must be readable, and fields above permission level 0, unknown fields and Password fields are refused.
- The Desk's **+ Filter** lists the inner fields as "Lines › Parts › Qty" and keeps them in the address like any other filter.

## Consequences
- Verified on real PostgreSQL (the nested-row tests include the "same line, same part" cases) and in the engine tests; the Desk list was type-checked but not exercised in a browser in this change.
- Still only for filtering, not for columns, sorting or report grouping, and not for reports, imports or exports of inner rows.
