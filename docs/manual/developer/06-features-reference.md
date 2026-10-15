# Features reference

One section per feature: what it does, how it is configured, what the API and engine do, and the limits. Each links to its decision record.

## DocType Designer ([ADR-003](../../adr/003-doctype-designer.md))
Create and edit DocTypes in the Desk (Tools → DocType Designer): fields and all their properties, naming, title field, list columns, permissions, rules. Saving shows the SQL that will run, applies additive schema changes (new tables, columns, indexes) in a safe order, and the DocType works at once. Limits: column types of saved fields cannot change; a DocType with documents cannot be deleted; DocTypes cannot be renamed yet. Only System Managers; Core and App DocTypes are read-only here.

## Child tables ([ADR-004](../../adr/004-child-tables.md))
Add a `Table` field whose `options` is a child DocType. Rows are written with the parent in one transaction, keyed by row name (insert, update, delete), shown as an editable grid, and cannot be reached except through the parent.

## Field logic and expressions ([ADR-005](../../adr/005-expression-language.md), [expressions](../../expressions.md))
`dependsOn`, `mandatoryDependsOn`, `readOnlyDependsOn`, `formula`, expression defaults, `fetchFrom` and validation rules, in one safe language evaluated identically on server and in the Desk. Formulas are recalculated on every save and override what a client sends.

## Permissions, levels, user permissions, sharing
See [Permissions and access](05-permissions-and-access.md) ([ADR-006](../../adr/006-permission-levels.md), [007](../../adr/007-user-permissions.md), [008](../../adr/008-sharing.md)).

## Submittable documents ([ADR-009](../../adr/009-submittable-documents.md))
`isSubmittable`. `docstatus` 0 draft, 1 submitted, 2 cancelled. Submit re-validates; cancel does not. After submit only `allowOnSubmit` fields (and tables) change; cancelled documents are locked; submitted documents cannot be deleted. `POST .../submit` and `.../cancel`. Not yet: amend.

## Profiles, export and report ([ADR-010](../../adr/010-profiles-export-report.md))
Role Profiles, Module Profiles; `export` (CSV, formula-safe) and `report` (grouped count/sum/avg/min/max) rights.

## Single, tree, series, rename and merge ([ADR-011](../../adr/011-special-doctypes.md))
- **Single** (`isSingle`): one document named after the DocType. `GET`/`PUT /resource/X/X`.
- **Tree** (`isTree`): `parent_node` and `is_group`; the engine enforces group parents, no cycles, no delete with children. `GET /tree/X?parent=`.
- **Naming series manager** (Tools → Naming Series, `/naming/series`): view and set the counter of each `PREFIX-.#####`.
- **Rename and merge** (`allowRename`, `POST .../rename`): one transaction updates the name, child rows, every Link field pointing at it, shares and user permissions. Plain-text fields that merely contain a name are not updated.

## Link fields ([ADR-012](../../adr/012-link-fields.md))
`GET /link/{doctype}/search` and `/titles`. Friendly name = the DocType's `titleField`, else the ID. Search matches ID and title case-insensitively, max 20 results, through the list rules. In the Desk `LinkSelect` is the one combobox (forms, grids, the Share picker).

## Customize Form ([ADR-013](../../adr/013-customize-form.md))
Tools → Customize Form. Adds fields to Core/App DocTypes (names `custom_...`, real columns, never dropped) and overrides label, help text, hidden, read-only, required, default, list, permission level, Select choices and the three conditions on existing fields; reorders fields; sets the title field. Stored per DocType in `__customize` as JSON `{ customFields, overrides, order, titleField }`; the registry holds the effective DocType and remembers the definition so Reset is exact. Applied at start-up before the schema sync. Administrator-only for Core DocTypes; not for designer DocTypes (edit those directly) or child tables.

## Updates log and comments ([ADR-014](../../adr/014-updates-log-and-comments.md))
`Version` rows (field-level diffs, table row changes, delete snapshots) are written for **every** DocType unless it sets `trackChanges: false`, **in the same transaction** as the change: `HistoryRecorder` builds the rows and `DocumentService` hands them to the repository as side writes (`ChildChanges` for another table; `DeleteAsync`/`RenameAsync` accept them too). `ActivityService` is the read side and the comments: `TimelineAsync`, `AddCommentAsync`, `UpdateCommentAsync`, `DeleteCommentAsync`. Sharing adds system notes (`Info` comments); rename and merge repoint the log and comments. Endpoints in `ActivityEndpoints.cs`; Desk `ActivityPanel`. Limits: no retention or restore yet; mentions are highlighted but not notified.

## Authentication ([ADR-002](../../adr/002-authentication.md))
Cookie sessions, ASP.NET Identity password hashing, lockout, login rate limit; `User`, `Role`, `Has Role` DocTypes. Roles are cached per session for `SessionCacheSeconds`.

## Planned next
Assign and follow, notifications (L7/L8), saved filters, workflow, reports and dashboards. The plan lists everything with its status.
