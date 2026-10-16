# ADR-083: Client scripts for the list view

Status: accepted

## Context
Client scripts (ADR-057) could change a form but not the list, so rules such as "mark overdue orders", "add a button for the ticked rows" or "show a note above the list" needed a developer.

## Decision
- The same scripts and the same sandbox (a Web Worker with no network, storage or page access) can now register list handlers: `qahwa.ui.list.on('Order', { onload(list) {…}, refresh(list) {…} })`.
- **`list`** offers copies of the rows on the page (`list.rows`) and the names ticked (`list.selected`), and these operations: `add_custom_button(label, handler)` (the handler gets the rows and ticked names), `set_intro(text)` (a note above the list), `msgprint(text)`, `set_row_indicator(name, color)` (a coloured mark on a row: gray, blue, green, orange, red or purple) and `set_filter(field, value)`.
- **Same trust rules as the form.** A script only returns operations; the page checks each one against the DocType (a filter must name a real, non-password column, values must be plain, colours come from the fixed list, limits on count and text) before doing anything. A script that throws shows a message; one that loops is stopped after a second and the list's scripts stay off. The server still checks everything the list asks for as usual, so a script cannot see more than the person may.
- `onload` runs once; `refresh` runs each time the rows on the page change. **A filter set from `refresh` is ignored**, so a script cannot make the list reload itself in a loop; use `onload` or a button.

## Consequences
- Scripts see only the page of rows already shown (not the whole list); a script that needs more must filter.
- Row marks last until the next refresh.
