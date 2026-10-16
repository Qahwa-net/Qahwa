# ADR-064: Excel (.xlsx) import and export

**Status:** accepted (L10)

## Context
People live in spreadsheets. CSV works but loses types (dates and numbers become text), breaks on encodings and is not what most people reach for. Excel libraries are large, some are commercial, and reading a file someone sends you is a classic way to attack a server.

## Decision
- **No library: a small writer and reader** for the Office Open XML format (`XlsxWriter`, `XlsxReader` in Core, using `System.IO.Compression` and `XmlReader`). The writer makes a one-sheet workbook with a bold header and real numbers, yes/no values, dates and moments (as date serials with a date format); text is always written as a text cell, so a value such as `=1+1` is shown and never run; characters XML cannot carry are dropped and a cell is cut to Excel's 32,767 characters; the sheet name loses the characters a sheet cannot have.
- **The reader treats the file as hostile.** At most 10 MB; at most 100 parts; part names must be unique; the first sheet is found through the workbook's relationships and the path it names is normalised and must be a part of the package (`../` can never climb out); what is read from each part is *counted* and limited to 40 MB (the size a zip declares is not believed, so a zip bomb stops early); XML is read with DTDs prohibited and no resolver (no entity expansion, no external entities); formulas are never evaluated (the stored value is read); at most 50,000 rows, 256 columns and 500,000 cells; cell addresses are validated; a shared-string index out of range is an empty cell; dates are recognised by the cell's number format (built-in and custom) and written as `yyyy-MM-dd` or with a time. Anything wrong is one plain "not a spreadsheet" or "too large" error, never a stack trace.
- **Export:** `GET /api/resource/{doctype}/export?format=xlsx` and `GET /api/reports/{id}/xlsx` beside the CSV ones, with the same permission checks, the same columns and the same "truncated" header.
- **Import:** the import and its preview accept the spreadsheet as base64 (`xlsx`) instead of CSV text; it is turned into CSV rows and goes through exactly the same checking, permissions and row-by-row results as a CSV file, so there is one import to trust. The Desk accepts `.xlsx` (up to 10 MB) next to CSV and offers "Export Excel" on lists and reports.

## Consequences
- Only the first sheet is imported; merged cells, pictures, charts and styles are ignored; the old `.xls` format is not supported (save as .xlsx).
- A date typed as text in Excel stays text and must be in the format the DocType expects, as in a CSV.
- The whole file is held in memory (10 MB packed, up to 40 MB unpacked per part).
