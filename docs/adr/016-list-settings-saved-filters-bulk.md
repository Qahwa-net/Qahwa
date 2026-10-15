# ADR-016: List settings, saved filters and bulk actions

**Status:** accepted (L7, part 3)

## Context
People use the same lists every day: they want their own columns and page size, to keep the filters they set up, to share useful views, and to change or delete many documents at once instead of opening each one.

## Decision
- **List settings are per person and per DocType.** One `List Setting` row holds `{ columns, pageSize, sortField, sortDescending }`. They are validated against the DocType **for that person**: columns and the sort field must be real columns they may read (standard columns and fields at a level they can see; never passwords), page size is one of 10, 20, 50, 100, at most 15 columns. A setting that names a field the person can no longer read quietly loses it when read. "Back to the usual view" deletes the row. The title column and "Last Modified" always show.
- **Saved filters** are `Saved Filter` rows: a name, the DocType, the filters `[[field, operator, value], ...]` and whether they are shared. Filters are checked for shape (1 to 20, operators `= != > < >= <= like is in`, simple values or a short list, 500 characters per value, 8,000 in all), and every field must exist and be readable by the person saving. A saved filter is only data: it is applied through the ordinary list request, so it can reach nothing a list could not. The same name (any case) replaces the old one; a person keeps at most 50 per DocType.
- **Sharing a filter with everyone** needs the right to edit the DocType's documents. Others see shared filters (with the author's name) but cannot change or delete them; an id from another DocType's filters, or someone else's private filter, is "not found". An Administrator can remove a shared one.
- **Bulk actions** work on up to 100 documents ticked in a list: set one field to a value, or delete. Each document goes through the normal engine on its own, so permissions, own-only rules, field levels, validation, submitted/cancelled locks, the updates log and notifications all apply; one that fails is reported with the reason and the rest still go ahead (it is not all-or-nothing). Calculated fields, passwords, standard columns and unknown fields cannot be set. Someone with no write (or delete) right on the DocType, or not signed in, is refused once with 403/401 instead of getting a list of failures.
- **Desk.** The list has a Columns panel (add, remove, reorder, rows per page, sort), a Saved filters picker with Save filters… (and "Share with everyone" when allowed), a checkbox column with select-all for the page, and a bulk bar (Set a field, Delete, Clear). The selection belongs to what is on screen: a new page, filter or sort clears it. The outcome of a bulk action stays visible after the selection clears.

## Security
Settings and filters are validated server-side against what the person may read; nothing is stored that could name a hidden field or a password. Bulk actions cannot bypass any rule because they use the engine per document, are capped at 100, and report per document without revealing documents the person cannot see (they appear as failures with the same messages as a single request).

## Consequences
- Not yet: a group-by sidebar with counts, row colours and indicators, comment counts in lists, bulk assign or bulk export of the selection, list settings that apply to everyone (a default per DocType), and a "my last view" memory per filter.
