# ADR-128: Importing documents with their table rows

**Status:** accepted (L10, follows ADR-027)

## Context
An order or an invoice is a document and its lines. Importing them as two files, one per table, meant working out the link between them by hand and made it easy to attach lines to the wrong document.

## Decision
- A column named `table_field.child_field` (for example `lines.item`, matched by name or by label, in any case) fills a row of that table. The template lists these columns after the document's own.
- **One file, one layout.** A row that fills any column of the document itself starts a new document. A row that leaves **all** of the document's own columns blank (and, for an update, the ID) belongs to the document above it and only adds a row to its tables. A document with no child cells has an empty table. The first row of a document may carry its first table row. Without child columns in the file nothing changes: every row is a document, as before.
- **Same rules as typing.** Each document goes through the ordinary insert or update with its rows attached, so child validation, formulas, required fields, the row limit, workflow and everything else apply. In *Update* mode, a file with child columns **replaces** the table with the rows listed for that document (a document with no rows listed gets none); a file without child columns leaves tables alone.
- **Errors at the right line.** A bad cell in a table row (`lines.qty = many`) and a rule broken by a row (`lines[1].item` is required) are reported at the line of that row in the file, with the field as `lines.qty`. The preview shows the table columns and checks the first rows the same way.
- Excel files take the same path as CSV, so the same columns work there.

## Consequences
- Several tables in one file work, but their rows share lines: row N of the document is the Nth line of the file for each table, so they are best used one table at a time.
- Not yet: nested tables (a table inside a table), mapping columns by hand, and a background import for very large files (next).
