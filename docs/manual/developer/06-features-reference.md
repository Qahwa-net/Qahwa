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

## Assign, follow and notifications ([ADR-015](../../adr/015-assign-follow-notifications.md))
`AssignmentService`, `FollowService`, `NotificationService` and `CollaborationHooks` (called by `DocumentService` after a save and by `ActivityService` after a comment; they reach back to `DocumentService` through `Lazy<DocumentService>` to check read access). Creating, commenting on or being assigned a document makes you a follower; followers are notified of others' changes and comments; @mentions (`Mentions.Find`) notify with an excerpt. Deleting removes the rows; rename and merge repoint them. Endpoints in `CollaborationEndpoints.cs`; Desk: `NotificationBell`, `AssignDialog`, `FollowButton`, `MyAssignments`. Limits: in-app only; no preferences yet (assignment rules, auto repeat and notification rules, with due-date reminders, are below).

## List settings, saved filters and bulk actions ([ADR-016](../../adr/016-list-settings-saved-filters-bulk.md))
`ListSettingsService` (per-person `List Setting` rows, shared or private `Saved Filter` rows, validation against the person's readable columns) and `BulkService` (per-document calls into `DocumentService`, max 100, per-document results). Endpoints in `ListEndpoints.cs`; Desk: `ColumnsPanel`, `SavedFilters`, `BulkBar`, and the selection column in `ListView`. Limits: no group-by sidebar, row indicators, comment counts, default views per DocType, or bulk assign/export yet.

## Group-by, status colours and comment counts ([ADR-017](../../adr/017-list-sidebar-status-comment-counts.md))
`DocumentService.GroupByAsync` (via `IDocumentRepository.GroupCountAsync`, one `GROUP BY` with the list's own filters), `statusField` and `indicators` on the DocType (validated in `DocTypeLoader`), `ActivityService.CommentCountsAsync`. Desk: `GroupBySidebar`, `RowStatus`/`StatusPill`, the 💬 count in `ListView`, and status settings in the designer. Limits: no assignments/tags group-by, no condition-based colours, not in the Customize Form.

## Document form UX ([ADR-019](../../adr/019-document-form-ux.md))

`controls/Modal.tsx` (`Modal`, `ConfirmDialog`: accessible dialog with focus trap), `controls/Icon.tsx` (`Icon`, `IconButton` with tooltip and count, `Avatar`), `pages/AssignPanel.tsx` (`AssignDialog`), `SharePanel.tsx` (`ShareDialog`), `RenamePanel.tsx` (`RenameDialog`), `DocRail.tsx` (side rail), the sticky `doc-bar` in `FormView`. To add an action to the form, add an `IconButton` to `doc-actions`; to ask before something irreversible, use `ConfirmDialog` rather than `window.confirm`. `lib/layout.ts` arranges fields into tabs and sections (tested in `layout.test.ts`); `controls/Toast.tsx` gives `useToast()`; `controls/Skeleton.tsx` is the loading outline. Add new icons to the `PATHS` map in `Icon.tsx` (24x24 line paths).

## Workflow engine ([ADR-018](../../adr/018-workflow-engine.md))

`Workflow.cs` (records, `WorkflowCatalog`, `WorkflowManager`: list, get, save, delete, load, static `Validate(definition, meta)`), `DocumentService.Workflow.cs` (status, apply action, inbox), the `WorkflowStep` hook in `DocumentService.UpdateCoreAsync` (sets the state and docstatus through the ordinary update), `WorkflowStore` (`__workflow`), `WorkflowEndpoints.cs`. A workflow is JSON (`DocTypeLoader.SerializeWorkflow/ParseWorkflow`):

```json
{ "docType": "Expense", "stateField": "workflow_state", "active": true,
  "states": [ { "name": "Draft" }, { "name": "Approved", "color": "green", "docStatus": 1, "editRoles": [] } ],
  "transitions": [ { "from": "Draft", "action": "Approve", "to": "Approved", "roles": ["Boss"], "condition": "amount < 500", "allowSelfApproval": false } ] }
```

Desk: `WorkflowBuilder`, `WorkflowBar` (on the form), `WorkflowInbox` (home), pure helpers in `lib/workflow.ts`. Tests: `WorkflowTests` (Core), `WorkflowApiTests`, `PostgresWorkflowTests`.

## Assignment rules ([ADR-020](../../adr/020-assignment-rules.md))

`AssignmentRules.cs`: `AssignmentRule` / `AssignmentRuleSet` records, `AssignmentRuleCatalog`, `AssignmentRuleManager` (list, get, save, delete, load, static `ValidateShape`) and `AssignmentRuleEngine` (picks a person and calls `AssignmentService.AssignByRuleAsync` / `WithdrawForRuleAsync`). `DocumentService.AfterSaveAsync` calls the engine through `Lazy<AssignmentRuleEngine>` (to break the DI cycle) with a delegate that evaluates conditions on the saved document. Store: `AssignmentRuleStore` (`__assignment_rule`). Endpoints: `AssignmentRuleEndpoints.cs`. A rule set is JSON:

```json
{ "docType": "Ticket", "rules": [
  { "name": "Urgent", "active": true, "strategy": "LoadBalancing", "users": ["a@x.com", "b@x.com"],
    "condition": "urgency == 'High'", "closeCondition": "status == 'Resolved'",
    "description": "Look at this first", "dueInDays": 1, "priority": "High" },
  { "name": "Owner", "strategy": "Field", "userField": "handler" } ] }
```

Desk: `AssignmentRules.tsx`, helpers in `lib/assignmentRules.ts`. Tests: `AssignmentRuleTests` (Core), `AssignmentRuleApiTests`, `PostgresAssignmentRuleTests`.

## Auto Repeat and the scheduler ([ADR-021](../../adr/021-auto-repeat.md))

`AutoRepeat.cs`: `AutoRepeatSchedule` (date maths), `AutoRepeatService` (create, update, delete, run now, `RunDueAsync`), and the Core DocType `CoreDocTypes/auto_repeat.json`. `Qahwa.Server/SchedulerService.cs` is a `BackgroundService` that calls `RunDueAsync` every `Qahwa:Scheduler:IntervalSeconds` (default 300; `Qahwa:Scheduler:Enabled = false` turns it off, for example on all but one server, though several are safe). To add another scheduled job, call it from that loop (or add a second hosted service) and make it idempotent: claim the work with a compare-and-swap before doing it, as `RunRowAsync` does. Endpoints: `AutoRepeatEndpoints.cs`. Desk: `RepeatDialog.tsx`, `AutoRepeatPage.tsx`, `lib/autoRepeat.ts`. Tests: `AutoRepeatTests` (a fake clock drives due dates, including four parallel runs), `AutoRepeatApiTests`, `PostgresAutoRepeatTests`.

## Notification rules ([ADR-022](../../adr/022-notification-rules.md))

`NotificationRules.cs`: `NotificationRule` / `NotificationRuleSet`, `MessageTemplate` (placeholder filling), `NotificationRuleCatalog`, `NotificationRuleManager` (list, get, save, delete, load, static `ValidateShape`) and `NotificationRuleEngine`. `DocumentService.AfterSaveAsync` calls `AfterSaveAsync(meta, name, isNew, before, after, actor)` through `Lazy<NotificationRuleEngine>`; `before` is the stored document before an update, so the engine can tell which columns really changed and whether docstatus moved. `SchedulerService` calls `RunScheduledAsync` about once an hour for the DaysBefore and DaysAfter events. `DocumentService.EvaluateCondition` evaluates a rule's condition. Store: `NotificationRuleStore`. Endpoints: `NotificationRuleEndpoints.cs`. A rule set is JSON:

```json
{ "docType": "Claim", "rules": [
  { "name": "Big claim", "event": "Submit", "condition": "amount > 1000", "roles": ["Accounts Manager"], "notifyOwner": true,
    "message": "{{user}} submitted {{name}} for {{amount}}" },
  { "name": "Ending soon", "event": "DaysBefore", "dateField": "ends_on", "days": 7, "userFields": ["handler"],
    "message": "{{title}} ends on {{ends_on}}" } ] }
```

Desk: `NotificationRules.tsx`, `controls/ChipPicker.tsx`, `lib/notificationRules.ts`. Tests: `NotificationRuleTests`, `NotificationRuleApiTests`, `PostgresNotificationRuleTests`.

## Authentication ([ADR-002](../../adr/002-authentication.md))
Cookie sessions, ASP.NET Identity password hashing, lockout, login rate limit; `User`, `Role`, `Has Role` DocTypes. Roles are cached per session for `SessionCacheSeconds`.

## Planned next
Email channels, email templates and the email queue (L8), reports and dashboards. The plan lists everything with its status.
