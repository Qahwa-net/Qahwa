# ADR-012: Searchable Link fields with friendly names

**Status:** accepted

## Context
A Link field stored and showed the other document's ID ("CUST-00041"), with a browser datalist of IDs as suggestions. People think in names, not IDs.

## Decision
- **One select everywhere.** `LinkSelect` is a type-ahead combobox (mouse and keyboard, ARIA combobox/listbox). Choices show the friendly name with the ID as a smaller hint; the chosen value is shown by its friendly name while the ID stays the stored value. It is used for Link fields in forms and table grids, and for the person picker in the Share panel.
- **Friendly name = the DocType's title field**, else the ID. Search matches the ID or the title, case-insensitively (`ILIKE` on PostgreSQL; the dialect decides), and returns at most 20 choices; with nothing typed it offers the most recently changed documents.
- **Server endpoints** `GET /api/link/{doctype}/search?q=` and `/titles?names=[...]` go through the ordinary list rules, so people only see documents they may read, User Permissions and module blocks apply, and a person who cannot read the target gets no choices. Search text is length-limited and only ever a SQL parameter.
- **The Share panel's picker** (`GET /api/share/{doctype}/{name}/users?q=`) lists enabled users by name or email, excluding the caller, and only to people who may share that document, so it cannot be used to browse all users. The share list shows names too.
- **List views** show friendly names in Link columns, resolved in one request per column.

## Consequences
- A person must pick from the list (or type an exact ID or name and leave the box); free text that matches nothing is not kept. Dynamic Link fields are still plain text.
- Filters on Link fields in the filter bar still take typed values.
