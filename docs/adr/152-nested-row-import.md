# ADR-152: Importing a table inside a table

**Status:** accepted

## Context
ADR-128 let one file carry a document and the rows of its child tables (`lines.item`), with a row that leaves the document columns blank adding a line to the document above. ADR-147 added tables inside child rows, which a file could not fill.

## Decision
- A column may be named `table.inner.field` (`lines.parts.qty`), by field name or label like any other. The column description gains `Inner` (the inner Table field); `Table` stays the document's Table field.
- **Grouping.** For each table of the document a file row either starts a new row (any of the row's own columns filled, e.g. `lines.title`), or, with those blank and inner columns filled, adds an inner row to the row above. A file row can also start a row *and* give its first inner row. Inner rows with no row above are refused with a message on that line.
- Everything after parsing is the ordinary save: the document goes through `InsertAsync`/`UpdateAsync` with its rows and their inner rows, so formulas, rules, permissions and history apply exactly as for typing it. Errors from the save (`lines[0].parts[1].qty`) are mapped back to the file line that holds that part, and reported as `lines.parts.qty`.
- Duplicate-column and unknown-column checks, the preview, background imports and the limits are unchanged; the 10,000-row ceiling of one save (ADR-147) applies to a document's rows and inner rows together.

## Consequences
- Tested in the engine: lines with parts in one file and a second document, the preview's per-line problem, a rule failing on one part (reported on its own line, nothing saved), and parts without a line.
- Not exercised against a real browser or PostgreSQL in this change (the import path is the same document service the PostgreSQL nested-row tests already cover).
- **Export is the mirror image** (added with this ADR): `GET .../export?rows=true`, or `fields` naming `lines.item` / `lines.parts.qty`, writes one output row per row of the tables (per part when parts are asked for), the document's own columns on the first only and the line's columns on the first part only, so the file imports back as it is; a line with no parts, or a document with no lines, still gets its row. The same rules as listing apply (export right, table fields and row fields readable by permission level, no passwords), at most 100,000 output rows (then `X-Export-Truncated`). Verified on real PostgreSQL and by exporting and re-importing in the engine tests. The list has an **Export CSV with table rows** button (type-checked, not exercised in a browser).
- Not yet: reports over rows of tables. Update-mode behaviour for inner rows is whatever the ordinary update does with the rows a file sends; it was not tested separately.
