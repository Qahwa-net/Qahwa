# ADR-076: Download and upload the rows of a table as CSV

Status: accepted

## Context
Order lines, price lists and similar tables can run to hundreds of rows. Typing them in, or fixing many at once, is slow; spreadsheets are where people already keep such data.

## Decision
- **In the browser only.** The table grid of a form gets **Download CSV** (when there are rows) and **Upload CSV** (when the form is editable). Nothing new is sent to the server: uploaded rows become ordinary new rows in the grid, and the server checks them when the document is saved, exactly like rows typed in (types, required fields, links, permissions, row limit).
- **Download** writes a header of field labels and one line per row for every field a person can type into, with a byte order mark so Excel reads it as UTF-8. Yes/no fields are "Yes"/"No". Text that starts with `=`, `+`, `-` or `@` (that is not a plain number) gets a leading quote so a spreadsheet cannot run it as a formula; upload takes that quote away again.
- **Upload** adds the rows to the end of the table (it never replaces what is there). Columns are matched by header, by field label or field name, in any order; columns that match nothing are ignored and listed; the file must match at least one column. Limits: 1 MB, 1,000 rows in all (like the server), CSV or text files only. The reader handles quoted cells, doubled quotes, line breaks inside quotes, CRLF or LF, a leading byte order mark and blank lines.

## Consequences
- Row order is the file order. Existing rows are not matched or updated by upload; to replace a table, delete its rows first.
- Excel (.xlsx) files are not read here; save as CSV.
- Values are strings until the form is saved, so a bad number or date is reported by the server on save, with the row and column.
