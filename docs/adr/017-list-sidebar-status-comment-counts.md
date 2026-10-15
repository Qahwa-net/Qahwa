# ADR-017: Group-by counts, status colours and comment counts in lists

**Status:** accepted (L7, part 4)

## Context
A list should tell you at a glance what is in it (how many are Open, Done, Blocked), which rows need attention (a coloured status), and where people are talking (comments).

## Decision
- **Group-by counts** (`GET /api/resource/{doctype}/group-by?field=&filters=`). One `GROUP BY` query counts documents per value of a field, most common first (at most 50 groups, default 20), empty values as `null`. It starts from exactly the query the list would run (`PrepareListQueryAsync`: filters, own-only roles, shares, User Permissions, blocked modules), so a count never includes a document the person could not list, and the counts follow the list's filters and search. Only sensible fields can be used: Select, Link, Check, Data, Email, Phone and Int fields the person may read, plus `owner`, `modified_by` and `docstatus`; long text, passwords, hidden (permission-level) fields and unknown names are refused with a validation error. The column name is checked against the DocType's whitelist; values are parameters.
- **Status colours.** A DocType may name a `statusField` (a Select, Data or Link field) and `indicators` (`[{ value, color }]`, colours gray, blue, green, orange, red, purple; unlisted values are grey). They are checked when the DocType loads and edited in the designer (a status field picker and a colour per choice). Lists show the status as a coloured pill, and submittable DocTypes also show Draft, Submitted or Cancelled. Colours are fixed classes, text is plain text.
- **Comment counts** (`GET /api/resource/{doctype}/comment-counts?names=[...]`, at most 100 names). The names are first filtered through the ordinary list, so only documents the person may read are counted, and the answer cannot reveal that an unreadable document has comments; system notes are not counted.
- **Desk.** A "Group by" panel in the list (choose a field; click a value to filter by it, click again to remove it; the choice is remembered in the browser only as a convenience), status pills beside each title, and a 💬 count. The status and `docstatus` columns are requested only when the DocType uses them.

## Security
Counts are computed through the list's own permission layers, fields are whitelisted and level-checked, comment counts are gated by read access, and all values stay parameters. Tests cover own-only roles, shares, hidden fields, injection-shaped field names, unknown DocTypes and anonymous callers.

## Consequences
- Not yet: group-by on assignments or tags, saving a favourite group-by with the list settings, indicators computed from conditions (only value to colour), and colours in the Customize Form.
