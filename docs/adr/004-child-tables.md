# ADR-004: Child tables

**Status:** accepted (L2)

## Context
Most business documents hold lines: the items of an order, the rows of a timesheet. They must save together with the document, be validated like the document, and follow the document's permissions.

## Decision
- A **child DocType** (`isChildTable`) has its own table with the standard columns plus `parent`, `parenttype`, `parentfield` and `idx` (position). It has no list, form, resource API or permissions of its own: every direct request for it answers "not found". Rows are only reachable through their parent.
- A **Table field** on the parent (`options` = the child DocType) has no column. The parent document shows its rows under the field's name, in order, as `[{ "name": ..., "idx": ..., <fields> }]`. Internal columns are not exposed.
- **Writes are atomic.** `IDocumentRepository` takes the child row changes together with the parent change and applies them in one database transaction. A failure, or a stale parent version, rolls back everything.
- **Updates are "this is the table now".** A client sends the full list of rows for a Table field. Rows carrying a `name` update that row, rows without one are new, rows missing from the list are deleted. A table that is not mentioned is left alone. The engine compares with what is saved and only writes the differences; rows keep their ids across edits and reorders.
- A row can only refer to rows of the *same* parent and field, so one document cannot take over or modify another document's rows. Duplicate row ids in one request are refused.
- **Validation** runs on every row with the child DocType's rules (required, select options, links, types). Errors are keyed by `field[row].column`, e.g. `lines[2].qty`, so the Desk can mark the cell. A required Table field needs at least one row. A table holds at most 1000 rows.
- Changing only the rows still counts as a change to the document: `modified` moves and optimistic concurrency applies.
- Deleting a document deletes its rows in the same transaction. A child DocType cannot be deleted while a parent uses it, and a DocType cannot be turned into a child table (or back) after it exists.
- Extra rules for child DocTypes: no naming rule (rows get random ids), no unique fields (a unique index would span the rows of every parent), no Table field inside a child table (no nested tables yet).
- The Desk receives the child definitions inside the parent's `/api/meta/{doctype}` response, because a user may not be allowed to read the child DocType directly. The designer has a "child table" switch, a Table field type that only offers child DocTypes, and a live grid in the preview.

## Consequences
- Child rows are not searchable or filterable from the parent's list yet.
- Reordering uses up/down buttons; drag and drop comes later.
- A child DocType can be shared by several parent DocTypes (the `parenttype` column keeps the rows apart).
