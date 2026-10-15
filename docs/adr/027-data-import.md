# ADR-027: Data import from CSV

**Status:** accepted (L10, part 1)

## Context
Moving to a new system or loading a spreadsheet of customers should not need a developer. The framework already exports CSV and has strict document rules; import should reuse them, not bypass them.

## Decision
- **One document per row, through the ordinary insert or update.** Every row of the file is created with `DocumentService.InsertAsync` (or changed with `UpdateAsync` in *Update* mode) as the person importing. So permissions (create, or write for updates), field levels, User Permissions, validation, naming, defaults, formulas, workflow, assignment rules, notification rules and the updates log all apply exactly as if the person had typed it. There is no bulk shortcut that skips any of them.
- **The file.** CSV (RFC 4180: quotes, doubled quotes, line breaks inside quotes, CRLF or LF, a byte-order mark), at most 5 MB, 5,000 rows and 500,000 cells. The first row names the columns, each matched to a field by name or by label, in any case; unknown columns are ignored and shown as such; a column named twice is refused. *Update* mode needs a `name` column with each document's ID, and a blank cell then clears the field; when creating, a blank cell means "use the default". Passwords, files, calculated and read-only fields and the framework's own columns (`owner`, `creation`...) cannot be set by a file. For DocTypes named by typing, a `name` column gives the name. Text the exporter protected with a leading apostrophe (`'=SUM(A1)`) is restored, so export and import round-trip.
- **Preview before importing.** `POST /api/import/{doctype}/preview` changes nothing: it shows how columns match, the first rows, and the problems found in the first 200 rows (bad numbers, dates and yes/no values, values outside a Select's choices, missing required fields, missing IDs). The import checks every row again.
- **Row by row, with line numbers.** A row that fails is reported with its line in the file, the field and the reason (at most 100 are listed) and does not stop the others, unless *stop at the first failure* is chosen. Rows already imported stay imported; the summary says created, updated and failed. A permission problem on the very first row stops the import at once with a plain error.
- **A template** (`GET /api/import/{doctype}/template`) gives a file with the columns a person may fill in.
- **Desk.** An **Import** button on the list (for people who may create): choose a file, see the column matching and the first rows, then import, with the results and the failed rows listed.

## Security
Importing needs read and create rights (read and write to update); single DocTypes and child tables cannot be imported. Cell values are never interpreted (they go through the same coercion and parameterised writes as typed values, so SQL-shaped and markup-shaped text is stored as plain data), the file's size, row and cell counts are limited before any work is done, and nothing in a file can set ownership, timestamps, passwords or calculated values. Tests cover hostile cells, unknown and duplicate columns, permissions for each mode, limits, partial failures and stop-on-error.

## Consequences
- Not yet: child table rows (a file per table), importing from Excel, a background import for very large files with progress, mapping columns by hand, and "insert or update" in one pass.
- Import is not atomic: a file is processed row by row, so a half-imported file is possible; the report says exactly which rows were done.
