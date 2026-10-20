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
`ListSettingsService` (per-person `List Setting` rows, shared or private `Saved Filter` rows, validation against the person's readable columns) and `BulkService` (per-document calls into `DocumentService`, max 100, per-document results). Endpoints in `ListEndpoints.cs`; Desk: `ColumnsPanel`, `SavedFilters`, `BulkBar`, and the selection column in `ListView`. Bulk rename ([ADR-065](../../adr/065-bulk-rename.md)): `BulkService.RenameAsync` runs each pair through the ordinary rename (links updated, per-document results); Desk `lib/bulkRename.ts` works out the new names (plain text, never a pattern) for the "Rename…" control in `BulkBar`. Limits: no default views per DocType or bulk assign yet.

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

## Email ([ADR-023](../../adr/023-email-queue-and-templates.md))

`Email.cs`: `IEmailSender`, `EmailService` (`EnqueueAsync`, `TemplatesAsync`, `PreviewAsync`, `SendFromDocumentAsync`, `SendPendingAsync`, static `ValidAddress`) and the Core DocTypes `email_template.json` and `email_queue.json`. `Qahwa.Server/EmailSenders.cs` has `SmtpEmailSender` (System.Net.Mail) and the logging `LogEmailSender`, chosen in `Program.cs` by whether `Qahwa:Email:Host` is set; to use another provider (an API, SendGrid, a cloud mailer) implement `IEmailSender` and register it instead. `SchedulerService` calls `SendPendingAsync` every tick. `NotificationRule.Email` enqueues mail from `NotificationRuleEngine`. A claim must change `modified` (see `SendPendingAsync`) because it is the version other servers compare with. Endpoints: `EmailEndpoints.cs`. Desk: `EmailDialog.tsx`, `lib/email.ts`. Tests: `EmailTests` (with `FakeEmailSender`), `EmailApiTests`, `PostgresEmailTests`.

## Dashboards ([ADR-024](../../adr/024-dashboards.md))

`Dashboards.cs`: `DashboardItem`, `DashboardService` (list, get, create, update, delete, `DataAsync`, `PreviewAsync`, `Validate`); `DocumentService.Analytics.cs`: `AggregateAsync` (one number) and `SeriesAsync` (an aggregate per group or per day, month or year), both reading rows through `ListAsync`; the Core DocType `dashboard.json`; `DashboardEndpoints.cs`. Desk: `Dashboards.tsx` (list, view, builder, `Tile`), `controls/Charts.tsx` (SVG bar, line and pie), `lib/charts.ts` (axis maths, tested), `lib/dashboards.ts`. To add a tile type: extend `DashboardItem.Types`, `ValidateItem`, `EvaluateAsync`, then `TileBody` and the editor. Tests: `DashboardTests`, `DashboardApiTests`, `PostgresDashboardTests`.

## Printing ([ADR-025](../../adr/025-print-formats-and-letter-heads.md))

`Print.cs`: `PrintTemplate` (parser, `Validate`, `Render`, `UnsafeReason`); `PrintService.cs`: choices, rendering (`RenderAsync`), preview, formats and letter heads CRUD, the default layout and the page wrapper with its nonce; Core DocTypes `print_format.json` and `letter_head.json`; `PrintEndpoints.cs` (sets the CSP headers). Desk: `PrintDialog.tsx` (sandboxed preview, opens the page in a tab), `PrintFormats.tsx` (builder). To extend the language add a node type in `PrintTemplate`'s `Parse`, `Validate` and `Render`, and test it against hostile values. Tests: `PrintTests`, `PrintApiTests`, `PostgresPrintTests`.

## Workspaces ([ADR-026](../../adr/026-workspaces.md))

`Workspaces.cs`: `WorkspaceBlock`, `WorkspaceShortcut`, `WorkspaceService` (`MineAsync`, list, get, create, update, delete; `Pages` is the whitelist of Desk pages), the Core DocType `workspace.json`, `WorkspaceEndpoints.cs`. Desk: `WorkspaceHome.tsx` (tabs and blocks on the home page), `Workspaces.tsx` (builder), `Dashboards.tsx` exports `DashboardTiles` for embedding, `lib/workspaces.ts` (its `PAGES` must match the server's whitelist). To add a Desk page as a shortcut target, add it to both. Tests: `WorkspaceTests`, `WorkspaceApiTests`, `PostgresWorkspaceTests`.

## Data import ([ADR-027](../../adr/027-data-import.md))

`DataImport.cs`: `CsvReader` (RFC 4180 reader with limits) and `DataImportService` (`Template`, `PreviewAsync`, `RunAsync`, `ImportableFields`); `ImportEndpoints.cs`. Desk: `ImportDialog.tsx` on the list view, `lib/importFile.ts`. Rows go through `DocumentService.InsertAsync/UpdateAsync`, so new rules apply to imports for free. Tests: `DataImportTests`, `ImportApiTests`, `PostgresImportTests`.

## API keys ([ADR-028](../../adr/028-api-keys.md))

`ApiKeys.cs`: `ApiKeyService` (`CreateAsync`, `ListAsync`, `RevokeAsync`, `AuthenticateAsync`) and the Core DocType `api_key.json`. `CookieUserContextResolver` (in `ApiInfrastructure.cs`) checks the Bearer key first, sets `ViaApiKey` for the request and enforces read-only; `ApiKeyEndpoints.cs` refuses key-authenticated callers. Desk: `ApiKeysSection.tsx` on the Account page. Tests: `ApiKeyTests`, `ApiKeyApiTests` (real cookie login then the key on its own), `PostgresApiKeyTests`.

## Webhooks ([ADR-029](../../adr/029-webhooks.md))

`Webhooks.cs`: `Webhook` / `WebhookSet`, `WebhookCatalog`, `WebhookManager` (validation, secret handling, SSRF checks on addresses), `NetworkGuard` (`IsPublic`, `UrlProblem`), `WebhookService` (`AfterSaveAsync`/`AfterDeleteAsync` queue deliveries; `DeliverPendingAsync` sends with claims, retries and backoff; static `Sign`) and `IWebhookSender`. `Qahwa.Server/WebhookSender.cs` is the real sender: it resolves the name and connects only to addresses that pass `NetworkGuard` (in `ConnectCallback`), never follows redirects and has short timeouts. `DocumentService` calls the service through `Lazy<WebhookService>` after saves and deletes; `SchedulerService` delivers each tick. Core DocType `webhook_delivery.json`; endpoints `WebhookEndpoints.cs`; Desk `Webhooks.tsx`, `lib/webhooks.ts`. To use another transport implement `IWebhookSender` but keep the guard. Tests: `WebhookTests` (with `FakeWebhookSender`; includes address and IP tables), `WebhookApiTests`, `WebhookSenderTests` (the real sender against internal addresses and a local receiver), `PostgresWebhookTests`.

## Attachments ([ADR-030](../../adr/030-attachments.md))

`Files.cs`: `IFileStorage`, `FileService` (`UploadAsync`, `OpenAsync`, `ListAsync`, `DeleteAsync`, `DeleteForDocumentAsync`; static `CleanName` and `Sniff`), `AttachmentOptions`, the Core DocType `file.json`; `FileEndpoints.cs` (sets the download headers); `Qahwa.Server/DiskFileStorage.cs` (random validated keys). `DocumentService.DeleteAsync` calls `DeleteForDocumentAsync` through `Lazy<FileService>`; rename and merge move the references through `CollaborationHooks`. To store files elsewhere (S3, Azure Blob) implement `IFileStorage` and register it. Desk: `Attachments.tsx` in the document side panel, `lib/files.ts`. Tests: `FileTests` (with `InMemoryFileStorage`), `FileApiTests`, `PostgresFileTests`.

## Authentication ([ADR-002](../../adr/002-authentication.md))
Cookie sessions, ASP.NET Identity password hashing, lockout, login rate limit; `User`, `Role`, `Has Role` DocTypes. Roles are cached per session for `SessionCacheSeconds`.

## Planned next
Incoming mail, HTML mail, Slack and SMS channels (L8), reports and dashboards. The plan lists everything with its status.

## Board and calendar views ([ADR-031](../../adr/031-board-and-calendar-views.md))

Desk only. `pages/BoardView.tsx` and `pages/CalendarView.tsx`, routes `app/:doctype/board` and `app/:doctype/calendar` in `App.tsx`, toolbar links in `ListView.tsx`. Pure helpers: `lib/board.ts` (`columnsOf`, `groupCards`, `withMoved`, `boardFields`) and `lib/calendar.ts` (`monthGrid`, `monthRange`, `eventsByDay`). A move is an ordinary `api.update` with an optimistic cache change and rollback. Tests: `lib/views.test.ts`.

## App bundles ([ADR-032](../../adr/032-app-bundles.md))

`Bundle.cs`: `BundleKind`, `AppBundle`, `BundleService` (`ExportAsync`, `ImportAsync(bundle, dryRun, user)`, static `ToJson` / `FromJson`). Import calls `DocTypeDesigner`, `CustomizationManager`, `WorkflowManager`, `AssignmentRuleManager` and `NotificationRuleManager` so all their validation and permission checks apply; each item succeeds or fails alone. To carry a new kind of definition: add a `BundleKind`, an export block and a case in `ApplyAsync`. `BundleEndpoints.cs`: `GET /api/bundle/export?name=&kinds=`, `POST /api/bundle/import?dryRun=` (JSON body, 5 MB). Desk: `pages/Bundle.tsx`, `lib/bundle.ts`. Tests: `BundleTests`, `BundleApiTests`, `PostgresBundleTests`.

## Translations and RTL ([ADR-033](../../adr/033-translations-and-rtl.md))

Core DocType `CoreDocTypes/translation.json`; `Translations.cs` (`TranslationService.CatalogAsync`, `LanguagesAsync`, `IsLanguage`, `IsRightToLeft`); `TranslationEndpoints.cs`. Desk: `i18n.tsx` (`I18nProvider`, `useI18n`, `useT`), `lib/i18n.ts` (`BUILT_IN` words, `mergeMessages`, `translate`), `shell/LanguageSwitcher.tsx`. To translate a new place, use `const t = useT()` and `t('Text')`; add built-in words to `BUILT_IN`. Write CSS with logical properties (`margin-inline-start`, `inset-inline-end`, `text-align: start`), never left/right. Tests: `TranslationTests`, `TranslationApiTests`, `lib/i18n.test.ts`.

## Web forms ([ADR-034](../../adr/034-web-forms.md))

Core DocType `CoreDocTypes/web_form.json`; `WebForms.cs`: `WebFormService` (`GetAsync`, `SubmitAsync`; re-validates the form on every call and fails closed), `HitCounter` (in-memory sliding window), `RateLimitedException`. `WebFormEndpoints.cs`: `GET`/`POST /api/public/forms/{route}` (anonymous, never reads the cookie or API key, 32 KB JSON, policy `qahwa-public-form` in `ApiInfrastructure`). Desk: `pages/PublicFormPage.tsx` at `/forms/:route` (outside `RequireAuth`), `lib/webform.ts`. To allow another field type edit `WebFormService.Allowed`; think about what a stranger could do with it first. Tests: `WebFormTests`, `WebFormApiTests`, `lib/webform.test.ts`.

## Document actions ([ADR-035](../../adr/035-document-actions.md))

Core DocType `CoreDocTypes/document_action.json`; `DocumentActions.cs`: `DocumentActionEngine` (`AfterSaveAsync`, `Check`, `Invalidate`). `DocumentService` calls it through `Lazy<DocumentActionEngine>` after the other hooks and invalidates its cache when a Document Action is saved or deleted. Runs as the acting user; an ambient `AsyncLocal` flag stops actions starting actions. Status (`last_error`, `run_count`) is written through the repository because those fields are read-only for people. Active rows are filtered with a real `true` (Check columns are booleans). To add an action type, add a case in `RunAsync` and `Check`, and an option to the `action` Select. Tests: `DocumentActionTests`, `DocumentActionApiTests`, `PostgresDocumentActionTests`.

## Error log ([ADR-036](../../adr/036-error-log.md))

Core DocType `CoreDocTypes/error_log.json`; `ErrorLog.cs`: `ErrorLogService.RecordAsync(source, method, path, user, exception)` (never throws, rate limited with `HitCounter`, path stripped of query). `UseQahwaErrors` calls it for every 500; `SchedulerService` calls it with source "Scheduler". Call it from your own background code the same way. Tests: `ErrorLogApiTests` (a TestServer host with a throwing endpoint).

## OpenAPI ([ADR-037](../../adr/037-openapi.md))

`Qahwa.Api/OpenApi.cs`: `OpenApiBuilder.Build(views, version)` is a pure function of the `MetaView`s a person may see (so permissions are already applied); `OpenApiEndpoints.cs`: `GET /api/openapi.json`. To describe a new resource endpoint add it to `Build`; keep it driven by `MetaView.Permissions`. Tests: `OpenApiTests`.

## DocType and document pickers ([ADR-038](../../adr/038-document-references-are-pickers.md))

Use `{ "type": "Link", "options": "DocType" }` for a field that names a DocType and `{ "type": "DynamicLink", "options": "<that field>" }` for the document. `DocumentService.DocTypeTarget` handles search and titles for the virtual target; `MetaRegistry.CheckReferences` allows it; the save check requires a real DocType. Desk: `FieldControl` passes the row or document values as `siblings` so `LinkInput` can read the DocType; `FormView` clears dependent Dynamic Links. Tests: `DocTypeLinkTests`.

## Two-factor sign-in ([ADR-039](../../adr/039-two-factor-sign-in.md))

`Core/Auth/TwoFactor.cs`: `Totp` (codes, Base32, verify with replay protection), `TwoFactorService` (`BeginSetupAsync`, `EnableAsync`, `DisableAsync`, `AdminResetAsync`, `CreateChallenge`/`RedeemChallenge`, `VerifyLoginAsync`), `ITotpStore`, `ISecretProtector`. `AuthService` is split into `VerifyPasswordAsync` (counts failures only) and `CompleteLoginAsync` (clears them, loads the session); `LoginAsync` is both. `Qahwa.Data/TotpStore.cs` (table `__totp`, created by `SchemaSync`), `Qahwa.Api/DataProtectionSecretProtector.cs`, endpoints in `AuthEndpoints.cs` (`/api/auth/login/2fa`, `/api/auth/2fa/...`). **Deployment:** persist and share the Data Protection key ring when running several servers (`services.AddDataProtection().PersistKeysToFileSystem(...)`). Desk: `LoginPage` (code step), `pages/TwoFactorSection.tsx`, `lib/twofactor.ts`. Tests: `TotpTests`, `TwoFactorTests`, `TwoFactorApiTests`, `PostgresTwoFactorTests`.

## Sessions ([ADR-040](../../adr/040-sessions.md))

`Core/Auth/Sessions.cs`: `SessionService` (`StartAsync`, `IsValidAsync`, `ListAsync`, `RevokeAsync`, `RevokeOthersAsync`, `RevokeAllForAsync`), `ISessionStore`. `Qahwa.Data/SessionStore.cs` (table `__sessions`). The cookie claim `qahwa:sid` is checked in `CookieUserContextResolver` (cached like the user check). Endpoints in `AuthEndpoints.cs` (`/api/auth/sessions...`). `AuthOptions.AbsoluteSessionDays`. Desk: `pages/SessionsSection.tsx`, `lib/sessions.ts`. Tests: `SessionTests`, `SessionApiTests`, `PostgresSessionTests`.

## Password reset and rules ([ADR-041](../../adr/041-password-reset-and-rules.md))

`Core/Auth/PasswordReset.cs`: `PasswordPolicy.Problem` (used by `AuthService.ValidatePassword`), `PasswordResetService` (`PrepareAsync`, `SendInBackground`, `RedeemAsync`, `ResetAsync`), `InvalidResetLinkException`. Endpoints `POST /api/auth/forgot` and `/api/auth/reset-password` in `AuthEndpoints.cs`. Configuration: `Qahwa:Auth:PublicUrl` (required in production for mail links) and `Qahwa:Email` (SMTP). Desk: `pages/PasswordResetPages.tsx`. The token must never be put in the Email Queue or any document. Tests: `PasswordPolicyTests`, `PasswordResetTests`, `PasswordResetApiTests` (the factory exposes sent mail as `Mail`).

## System Settings and network restrictions ([ADR-042](../../adr/042-system-settings-and-network-restrictions.md))

`CoreDocTypes/system_settings.json` (single); `SystemSettings.cs`: `SystemSettingsService`, `IpAllowList` (`Parse`, `Allows`). `SettingsEndpoints.cs` (`GET /api/settings`). `UseQahwaNetworkRestriction` in `ApiInfrastructure.cs` (part of `UseQahwa`); `Program.cs` resolves `IpAllowList` at start so a bad `Qahwa:Auth:AllowedIps` stops the server. Desk: `lib/display.ts` (`formatDate`, `formatDateTime`, `weekStartIndex`; `formatCell` uses them), `Shell` loads and applies the settings, `I18nProvider.applyDefault`. Tests: `SystemSettingsTests`, `IpAllowListTests`, `NetworkRestrictionTests` (a startup filter sets the fake client address), `SettingsApiTests`, `lib/display.test.ts`.

## Patches ([ADR-043](../../adr/043-patches.md))

`Patches.cs`: `IPatch`, `PatchRunner` (`Validate`, `RunPendingAsync`), `PatchFailedException`; Core DocType `patch_log.json`. Register with `services.AddQahwaPatch<MyPatch>()` (`PatchRegistration` in `HostingExtensions.cs`). `Program.cs` runs pending patches after all loaders. Example:

```csharp
public sealed class FillCustomerGroup : IPatch
{
    public string Name => "2026_03_01_fill_customer_group";
    public async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var documents = services.GetRequiredService<DocumentService>();
        // read rows, change them, save: safe to run twice
    }
}
```

Tests: `PatchTests`, `PostgresPatchTests`.

## Backups ([ADR-044](../../adr/044-backups.md))

`Backup.cs`: `BackupService` (`WriteAsync`, `RestoreAsync(stream, dryRun, user, ct, maxBytes)`, `RunScheduledAsync`), `BackupOptions`, `BackupService.Skipped` (DocTypes never backed up). `BackupEndpoints.cs`: `GET /api/backup/download` (temp file, then sent), `POST /api/backup/restore?dryRun=` (`application/zip`). `SchedulerService` writes the daily file when `Qahwa:Backup:Path` is set. A zip cannot be written to the response stream (ASP.NET Core forbids synchronous writes), hence the temporary file. Desk: `pages/BackupPage.tsx`, `lib/backup.ts`. Configuration: `Qahwa:Backup:Path`, `Keep`, `MaxRestoreMegabytes`. Tests: `BackupTests`, `BackupApiTests`, `PostgresBackupTests`.

## Report Builder ([ADR-045](../../adr/045-report-builder.md))

`Reports.cs`: `ReportDefinition`, `ReportService` (`ListAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`, `RunAsync`, `ExportAsync`, `Validate`); Core DocType `report.json`; `DocumentService.Reports.cs` (`GroupedAsync`, `AggregateKey`). `ReportEndpoints.cs`: `/api/reports` (list, create, `/preview`, `/{id}`, `/{id}/run`, `/{id}/csv`). Desk: `pages/Reports.tsx` (list, viewer, builder), `lib/reports.ts` (draft to definition). To allow another aggregate or operator, change `ReportDefinition.Operators`, `DocumentService.Aggregates` and `Validate` together. Tests: `ReportTests`, `ReportApiTests`, `PostgresReportTests`, `lib/reports.test.ts`.

## Master data and the "All" role ([ADR-046](../../adr/046-master-data.md))

`CoreDocTypes/{currency,country,language,salutation,gender}.json`; `MasterData.cs` (`MasterDataPatch`, registered as an `IPatch` in `HostingExtensions`), data files `MasterData/countries.json` and `currencies.json` (embedded). `UserContext.AllRole` is appended in `AuthService.LoadSessionAsync` (so sessions and API keys have it); the test header resolver adds it too. To add reference data of your own, use a Core-style DocType with `{ "role": "All", "read": true }` and a patch like `MasterDataPatch`. Tests: `MasterDataTests`.

## More field types ([ADR-047](../../adr/047-more-field-types.md))

`FieldType` (Qahwa.Abstractions) gained `Rating`, `Duration`, `Color`, `Barcode`, `Geolocation`, `Signature`. Validation is in `FieldValueConverter.Convert` (the only place values from clients become stored values); column types in `PostgresDialect.ColumnType`; `FieldTypes.IsShortText` includes Color and Barcode; numeric sets in `DocumentService.Special`, `Dashboards`, `Reports` include Rating and Duration; printing in `PrintTemplate.Show`; `FieldTypeCatalog` makes them available in the Designer. Desk: `controls/SpecialInputs.tsx`, `lib/fieldtypes.ts` (duration parsing, place helpers), cases in `FieldInput` and `formatCell`. To add another type touch all of these places. Tests: `NewFieldTypeTests`, `PostgresFieldTypeTests`, `lib/fieldtypes.test.ts`.

## Attachment and image fields ([ADR-048](../../adr/048-attach-and-image-fields.md))

Validation in `DocumentService.ValidateAsync` calls `FileService.CheckFieldFileAsync`; `FileService.DeleteAsync` empties fields naming the removed file (`ClearFieldsNamingAsync`); `DocTypeLoader.CheckField` refuses them in child tables. Desk: `AttachInput` in `controls/SpecialInputs.tsx`, which reads the document from `DocContext` (provided by `FormView`) and uploads with `api.files.upload`. Tests: `AttachFieldTests`.

## Gallery, map and timeline views ([ADR-049](../../adr/049-gallery-map-and-timeline-views.md))

Desk only: `pages/MoreViews.tsx` (`GalleryView`, `MapView`, `GanttView`), routes `app/:doctype/gallery`, `/map`, `/timeline` in `App.tsx`, toolbar links in `ListView.tsx`. Pure layout in `lib/gantt.ts` (`layout`, `dayNumber`) and `lib/map.ts` (`layoutMap`, `gridStep`). Tests: `lib/views2.test.ts`.

## PDF ([ADR-050](../../adr/050-server-side-pdf.md))

`Pdf.cs`: `IPdfRenderer`, `PdfService` (`Prepare`, `RenderAsync`), `NoPdfRenderer`, `PdfNotAvailableException` (501); `Qahwa.Server/BrowserPdfRenderer.cs` (`PdfOptions`: `BrowserPath`, `TimeoutSeconds`, `MaxMegabytes`, `Concurrency`, `NoSandbox`), registered in `Program.cs` with `Replace`. `PdfEndpoints.cs`: `/api/pdf/status` and `/api/pdf/{doctype}/{name}`. To use another engine implement `IPdfRenderer` and register it. Desk: "Download PDF" in `PrintDialog`. Tests: `PdfTests` (the page is made inert), `PdfApiTests` and `BrowserPdfRendererTests` (a real browser; skipped where none is installed), with `FakePdfRenderer` in `Qahwa.Testing`.

## Single sign-on ([ADR-051](../../adr/051-openid-connect-sign-in.md))

`Auth/Sso.cs`: `SsoProvider`, `SsoOptions`, `IOidcBackchannel`, `IIdentityStore`, `SsoService` (`Providers`, `StartAsync`, `CompleteAsync`, `SafeNext`), `SsoException` (the short `Code` goes to the browser, the message only to the log); `AuthService.AccountStateAsync`. `Qahwa.Data/IdentityStore.cs` (`__identities`, primary key issuer + subject). `Qahwa.Api/HttpOidcBackchannel.cs` (the real HTTP client with its limits) and `SsoEndpoints.cs`. Configuration `Qahwa:Auth:Sso:Providers:<name>` (bound in `Program.cs`). To support another way of talking to providers, implement `IOidcBackchannel`. Desk: provider buttons and `sso_error` text in `LoginPage.tsx`, `lib/sso.ts`. Tests: `SsoTests` (with `FakeIdentityProvider` in `Qahwa.Testing`, which can issue wrong-issuer, wrong-audience, expired, unsigned and foreign-key tokens), `SsoApiTests`, `PostgresIdentityTests`, `lib/sso.test.ts`.

## Self sign-up ([ADR-052](../../adr/052-sign-up-with-email-verification.md))

`Auth/SignUp.cs`: `SignUpOptions` (`Qahwa:Auth:SignUp`), `SignUpService` (`PrepareAsync`, `SendInBackground`, `RedeemAsync`, `ConfirmAsync`, `CleanName`), `SignUpMail`, `InvalidSignUpLinkException`, `SignUpDisabledException`; `AuthService.CreateUserAsync` (strictly new accounts) with the shared `GrantRolesAsync`. Endpoints in `AuthEndpoints.cs` (`/api/auth/signup`, `/signup/confirm`). Desk: `pages/SignUpPages.tsx`, link on `LoginPage.tsx`. Tests: `SignUpTests`, `SignUpApiTests` (the factory takes `signUp: true`).

## Server Scripts ([ADR-053](../../adr/053-server-scripts.md))

`ServerScripts.cs`: `ScriptSandbox` (one Jint run: limits are constants on the class, host API `qahwa.*`, `SyntaxProblem`), `ScriptInput`, `ScriptOutcome`, `ServerScriptEngine` (`ValidateAsync`, `AfterSaveAsync`, `TestAsync`, `CheckScript`, `Invalidate`); Core DocType `server_script.json`. `DocumentService.RunScriptsAsync` calls Validate scripts after formulas in both insert and update (script-set values are converted by `Coerce`), and After Save scripts and cache invalidation in `AfterSaveAsync`/delete; the engine is a `Lazy<ServerScriptEngine>` like Document Actions. `ServerScriptEndpoints.cs`: `POST /api/server-scripts/test`. Events are listed in `ServerScriptEngine.Events` (`MayChange` says which may alter the document); `DocumentService` passes the submit/cancel `transition` to `RunScriptsAsync` and calls `BeforeDeleteAsync` in `DeleteAsync`. To add a host function, add it in `ScriptSandbox.Execute` (count it against a limit, go through `DocumentService`, and never return .NET objects: only JSON). Tests: `ServerScriptTests` (including runaway, escape and reflection attempts), `ServerScriptApiTests`, `PostgresServerScriptTests`.

## LDAP sign-in ([ADR-054](../../adr/054-ldap-sign-in.md))

`Auth/Ldap.cs`: `LdapOptions` (`Qahwa:Auth:Ldap`), `ILdapDirectory` (`SearchAsync`, `BindAsync`), `LdapService` (`SignInAsync`, `IsDirectoryUserAsync`, `EscapeFilter`, `Problem`), `LdapEntry`. `Qahwa.Api/LdapDirectory.cs` is the real client; `Program.cs` refuses to start on `LdapService.Problem`. The login endpoint in `AuthEndpoints.cs` tries the directory first for linked people and after a failed local check for others; directory sign-ins skip password expiry. Links are rows in `__identities` with issuer `ldap://<server>` and the DN as subject. Tests: `LdapTests`, `LdapApiTests` (factory `ldap: true`), with `FakeLdapDirectory` in `Qahwa.Testing`.

## Several sites ([ADR-055](../../adr/055-multi-site.md))

`Qahwa.Hosting/Sites.cs`: `SiteDefinition`, `SiteCatalog` (`Load`, `Check`, `NormalizeHost`, `ValidName`), `SiteResolver`, `SiteConfigException`. `Qahwa.Server/SiteServices.cs`: `AddQahwaSite` (all registrations for one site; used for the main site and each extra site), `SiteStartup.RunAsync` (migrate, loaders, patches), `ISiteServicesCustomizer` (lets a host or test change a site's services). `Qahwa.Server/SiteHost.cs`: `SiteHost` (builds a provider per site, `SiteConfiguration` layers the site file and sets per-site defaults, runs each site's `SchedulerService`), `UseQahwaSites` middleware (sets `HttpContext.RequestServices` to a scope of the site's provider). `Qahwa.Data.Postgres/PostgresSites.cs` creates a site's database. CLI: `new-site`, `list-sites`, `--site`. Everything the framework registers is a singleton, so a site's container is a full, separate copy. In tests `QahwaFactory(sitesPath:, mainSite:)` runs sites on in-memory stores (`SiteMail` holds each site's fake mail); values read by `Program` before the host is built must be given with `UseSetting`. Tests: `SiteCatalogTests`, `MultiSiteApiTests`, `PostgresSiteTests`.

## Themes and brand ([ADR-056](../../adr/056-brand-and-theme.md))

`Brand.cs`: `Brand`, `BrandService` (`GetAsync`, `From`, `Check`, `LogoProblem`); Core DocType `brand_settings.json`; `DocumentService.RunScriptsAsync` calls `BrandService.Check` when a Brand Settings document is saved. `GET /api/brand` (public) in `SettingsEndpoints.cs`. Desk: `lib/theme.ts` (pure: scheme resolution, contrast, colour mixing, `brandVariables`, `parseBrand`, `safeLogo`), `theme.tsx` (`ThemeProvider`, `useTheme`: sets `data-theme` on `<html>`, applies the `--brand`, `--brand-text`, `--accent-soft` variables, title, brand cache), `shell/ThemeToggle.tsx`, `pages/BrandPage.tsx`, Appearance section in `AccountPage.tsx`, `public/theme-init.js`, the two dark palettes in `styles.css` (keep them identical when editing). Tests: `BrandTests`, `BrandApiTests`, `lib/theme.test.ts`.

## Client Scripts ([ADR-057](../../adr/057-client-scripts.md))

Server: `ClientScripts.cs` (`ClientScriptService.ForAsync`, `Check`; Core DocType `client_script.json`; `DocumentService.RunScriptsAsync` checks the source on save), `GET /api/client-scripts/{doctype}` in `ServerScriptEndpoints.cs`, and the special content-security policy of `/client-script-worker.js` in `Program.cs`. Desk: `public/client-script-worker.js` (the sandbox: removes network and storage globals, runs scripts, collects operations), `lib/clientScripts.ts` (`sanitizeOps`, `scriptDoc`, `ClientScriptRunner` with the one-second limit), `lib/useClientScripts.ts` (the hook: events, overrides, intro, buttons, validate), integration in `pages/FormView.tsx` (`stateOf` applies overrides, `save` awaits `beforeSave`). To add an operation: the worker's `makeFrm`, the `ScriptOp` type and `sanitizeOps`, and `apply` in the hook. Tests: `ClientScriptTests`, `ClientScriptApiTests`, `lib/clientScripts.test.ts`.

## SAML sign-in ([ADR-058](../../adr/058-saml-sign-in.md))

`Auth/Saml.cs`: `SamlProvider`, `SamlOptions` (`Qahwa:Auth:Saml`), `SamlService` (`Start`, `CompleteAsync`, `Problem`, `EntityId`; the signature and assertion checks are in `Validate` and `CheckSignature`), `SamlStart`. User resolution is shared with OpenID Connect: `ExternalIdentityResolver` and `ExternalPolicy` in `Auth/Sso.cs`. `Qahwa.Api/SamlEndpoints.cs` (login, acs, metadata). `Program`/`SiteStartup` refuse to start on `SamlService.Problem`. Desk: SAML buttons in `LoginPage.tsx`, `samlLoginUrl` in `lib/sso.ts`. Tests: `SamlTests` (wrapping, tampering, foreign key, SHA-1, replay, XXE, audience/recipient/destination/time), `SamlApiTests`, with `FakeSamlIdp` in `Qahwa.Testing` (makes and signs responses with a generated certificate; options produce each kind of bad response).

## SQL Server ([ADR-059](../../adr/059-sql-server.md))

`Qahwa.Data.SqlServer`: `SqlServerDialect`, `SqlServerConnectionFactory`, `SqlServerSchemaInspector`, `AddQahwaSqlServer`, `SqlServerSites`. Chosen per site by `Qahwa:Database:Provider` in `QahwaSiteServices.AddQahwaSite`. The interface changes behind it: `ISqlDialect.AddColumnSql`, `DropIndexSql(index, table)`, `MaxParameters`; `DbCommands.Create` declares `datetime2`/`date`/`time` for SQL Server commands; `SqlBuilder` refuses over-long value lists; every query that pages is ordered. To run the data suite on SQL Server: `QAHWA_TEST_DB=sqlserver dotnet test tests/Qahwa.Data.Tests` (needs Docker; the first run pulls the 1.5 GB image). Write new database tests with the `PgStack` helpers (`TableExistsAsync`, `ColumnCountAsync`, `IndexCountAsync`, `ChangeColumnToTextSql`, `IsPostgres`) rather than raw catalog SQL. Tests: `SqlServerDialectTests`, `DatabaseBehaviourTests`, and every `Postgres*Tests` class on both products.

## More filter operators ([ADR-060](../../adr/060-more-filter-operators.md))

`FilterExpansion.cs` (`Expand`, `Dynamic`, `Span`, `Timespans`); `FilterOp.NotLike`/`NotIn` in `Querying.cs`, built in `SqlBuilder` (null-inclusive) and `InMemoryDocumentRepository`; `DocumentService.Normalize` expands and converts each filter; `ISqlDialect.LikeEscape`. Desk: `lib/filters.ts` (`TIMESPANS`, encode/decode), `lib/fields.ts` (`operatorsFor`), `controls/FilterBar.tsx`. Tests: `FilterOperatorTests`, `DatabaseBehaviourTests` (both databases), `lib/filters.test.ts`.

## The search box ([ADR-061](../../adr/061-global-search.md))

`GlobalSearch.cs` (`GlobalSearchService.SearchAsync`, `Literal`); `GET /api/search` in `QahwaEndpoints.cs`. The in-memory test repository now reads LIKE patterns with backslash escapes like the real databases. Desk: `lib/search.ts` (`pageItems`, `paletteItems`, `score`), `shell/CommandPalette.tsx`, Ctrl+K in `Shell.tsx`. To make a page appear in the box, add it to `pageItems`. Tests: `GlobalSearchTests`, `SearchApiTests`, `DatabaseBehaviourTests` (literal patterns on both databases), `lib/search.test.ts`.

## Apps, hooks, methods and developer mode ([ADR-062](../../adr/062-apps-hooks-methods-developer-mode.md))

See [Writing an app](08-writing-an-app.md). Code: `Apps.cs` (`IQahwaApp`, `IAppBuilder`, `AppCatalog`, `AppHookRunner`, `MethodService`, `BuiltInMethods`, `AppInstaller`, `AppJobRunner`, `AddQahwaApp`), `Designer/DeveloperMode.cs` (`DeveloperExport`, `IDeveloperExport`), `DocumentService.RunAppHooksAsync` and the After/Delete hook calls, `MethodEndpoints.cs`, `DeveloperEndpoints.cs`, `SiteStartup` (installs apps), `SchedulerService` (runs jobs), Core DocType `installed_app.json`. Tests: `AppTests`, `DeveloperModeTests`, `MethodApiTests`, `DeveloperApiTests`, `PostgresAppTests`.

## Naming formats and Duplicate ([ADR-063](../../adr/063-naming-formats-and-duplicate.md))

`NamingService` (`Resolve`, `FieldTokens`, `PrefixRegex`; takes the `TimeProvider`), `DocTypeLoader.CheckAutoname` (pattern and field checks), `NamingSeriesManager.ListAsync` (counters under their pattern). Desk: `lib/duplicate.ts` (`copyOf`), the `copy` query parameter and `template` prop in `pages/FormView.tsx`. Tests: `NamingFormatTests`, `lib/duplicate.test.ts`.

## Excel ([ADR-064](../../adr/064-excel-import-and-export.md))

`Xlsx.cs` (`XlsxWriter.Write`, `XlsxReader.Read`/`ToCsv`; every limit is a public constant). Endpoints: `format=xlsx` on `SpecialEndpoints` export, `/api/reports/{id}/xlsx`, `ImportRequest.Xlsx` (base64) in `ImportEndpoints.cs`. Desk: `lib/importFile.ts` (`isSpreadsheet`, `toBase64`, `ImportSource`), `pages/ImportDialog.tsx`, Export Excel links in `ListView.tsx` and `Reports.tsx`. Tests: `XlsxTests` (round trips, other programs' files, zip bombs, DTDs, path tricks), `ExcelApiTests`, `lib/importFile.test.ts`.

## Password fields ([ADR-066](../../adr/066-password-fields.md))

`DocumentService.Passwords.cs`: `ProtectPasswords` (encrypts with `ISecretProtector` just before insert/update; empty clears), `MaskPasswords` (called from `Strip`, so every API read shows `PasswordMask` = `********`), `GetPasswordAsync` (server-side only; needs write right plus field read/write; not exposed over REST). `Coerce` ignores the mask sent back; `Normalize` treats Password columns like fields above the permission level (no listing, filtering, sorting). `FieldValueConverter` accepts text up to `MaxPasswordCharacters` (1000); `DocTypeLoader` refuses Password in child tables; Custom Fields still cannot be Password. `DocumentService` takes an optional `ISecretProtector` (registered by the API; the test engine uses `FakeSecretProtector`). Desk: `FieldControl` renders `type="password"` with `autoComplete="new-password"`.

## Deleted documents ([ADR-067](../../adr/067-deleted-documents.md))

`DeletedDocuments.cs`: `DeletedDocumentService.Capture` (static; builds the `Deleted Document` write that `DocumentService.DeleteAsync` adds to the delete's side writes, so it is atomic), `RestoreAsync` (System Manager + create right on the target; rows are rebuilt with `BackupService.RowFromJson`, so values are converted and checked like a backup restore; refuses when the name exists; adds a `Restore` Version row), `PurgeAsync` (used hourly by `SchedulerService`; `Qahwa:DeletedDocuments:RetentionDays`, default 30). Core DocType `deleted_document.json` (System Manager: read, delete). Endpoint `POST /api/deleted-documents/{id}/restore` in `DeletedDocumentEndpoints.cs`. Desk: a Restore button on the Deleted Document form (`FormView`). Limits: no restore of comments/files/shares; no validation on restore; copies over 1,000,000 characters are not kept.

## Tags and likes ([ADR-068](../../adr/068-tags-and-likes.md))

`Labels.cs`: `LabelService` (`AddTagAsync`/`RemoveTagAsync`/`TagsAsync`/`PopularTagsAsync`, `SetLikeAsync`/`LikeStateAsync`, static `NormalizeTag`, `IsLabelFilter`, `ToQueryFilter`). Core DocTypes `document_tag.json`, `document_like.json`; both are listed in `CollaborationHooks.Tables` so they are deleted with the document and renamed with it. List filters `_tag` and `_liked_by` are split off in `DocumentService.PrepareListQueryAsync` and become `FilterOp.Marked` with a `MarkedBy` value (a `name IN (SELECT ref_docname FROM <table> WHERE ref_doctype = ? AND <field> = ?)` in `SqlBuilder`; the in-memory repository mirrors it). Endpoints in `CollaborationEndpoints.cs`. Desk: `TagsPanel` (in `DocRail`), `LikeButton`, tag chips in `GroupBySidebar`, `describeFilter` words for the two filters.

## Text Editor, Small Text, Autocomplete ([ADR-069](../../adr/069-text-editor-small-text-autocomplete.md))

`HtmlSanitizer.cs` (`Clean`: allow-list rebuild, tested against script/style/handler/scheme/entity vectors in `HtmlSanitizerTests`; `ToPlainText`), `FieldValueConverter` (`TextEditor` is cleaned in `ToStorage`; `SmallText` ≤ 5,000), `FieldTypes.IsShortText` includes `Autocomplete` (so it is `varchar(140)`), `DocTypeLoader` limits suggestions, `Print.Show` prints plain text. Desk: `RichTextInput`, `lib/richText.ts` (`sanitizeHtml`, `safeHref`, `htmlToText`; tests run under jsdom via a per-file `// @vitest-environment jsdom`), `FieldControl` cases, `formatCell`.

## Live updates ([ADR-070](../../adr/070-live-updates-server-sent-events.md))

`Realtime.cs`: `RealtimeEvent`, `IRealtimePublisher`, `RealtimeHub` (per-person and per-document subscriptions, bounded drop-oldest queues, connection limits). `DocumentService.Announce` (insert/update/transition/delete) and `NotificationService.NotifyAsync` publish; both take an optional publisher (registered by `HostingExtensions`). Endpoint `GET /api/realtime[?doctype=&name=]` in `RealtimeEndpoints.cs` (cookie sessions only; read permission checked on connect; keep-alive comment every 20 s). Desk: `lib/realtime.ts` (`realtimeUrl`, `parseEvent`, `liveAction`), `lib/useLive.ts` (EventSource hook), `NotificationBell` and `FormView`. To add a backplane for several servers, implement `IRealtimePublisher` over Redis/the database and feed `RealtimeHub.Publish` from it.

## Emailed reports ([ADR-071](../../adr/071-auto-email-reports.md))

`AutoEmailReports.cs`: `AutoEmailReportService` (`CreateAsync`/`UpdateAsync`/`DeleteAsync`/`ListAsync`/`SendNowAsync`, all System Manager only; `RunDueAsync` claims each due row with a compare-and-swap on `modified`, moves `next_send` past now, then runs `ReportService.ExportAsync` as `run_as` (resolved with `NotificationService.ContextOfAsync`) and queues emails with `EmailService.EnqueueAsync`; `Format` builds the text table; `NextSlot` computes the schedule). Core DocType `auto_email_report.json`. `SchedulerService` calls `RunDueAsync` every tick. Endpoints in `AutoEmailReportEndpoints.cs`. Desk: `pages/EmailedReports.tsx`, `lib/emailedReports.ts` (address parsing and checks mirrored from the server), nav link under Tools.

## Email digest ([ADR-072](../../adr/072-email-digest.md))

`EmailDigest.cs`: `EmailDigestService` (`GetAsync`/`SetAsync` for the caller only, `RunDueAsync` claims each due row with a compare-and-swap, builds the text with the person's own `NotificationService.ListAsync` and `AssignmentService.MineAsync`, queues one email with `EmailService.EnqueueAsync`; reuses `AutoEmailReportService.NextSlot`). Core DocType `email_digest.json` (named after the person's email). `SchedulerService` calls `RunDueAsync` every tick. Endpoints `GET/PUT /api/email-digest` in `AutoEmailReportEndpoints.cs`. Desk: `EmailDigestSection` on the account page.

## Naming rules and expressions ([ADR-073](../../adr/073-naming-rules-and-expressions.md))

`NamingService` (`ExpressionPrefix`, `RuleForAsync` picks the first matching enabled `Document Naming Rule` of the DocType, `NextNameAsync(..., user)` evaluates `expression:` names; `CheckName` rejects control characters), `DocTypeLoader.CheckNamingRule` (shared by the DocType's own rule and rule rows), `NamingRuleCheck.Check` (called from `DocumentService.RunScriptsAsync` when a Document Naming Rule is saved), `NamingSeriesManager.IsSeries` skips expression rules. Core DocType `document_naming_rule.json`. `NamingService` takes the meta registry (optional) to find the rules table. Desk: `AutonameKind` has `expression` in `lib/designer.ts`, with the input in `DesignerEditor`.

## App permission hooks ([ADR-074](../../adr/074-app-permission-hooks.md))

`Apps.cs`: `IAppBuilder.HasPermission`/`PermissionQuery`, `PermissionContext`, `PermissionFilter`, `AppHookRunner.AllowsAsync` (false on refusal or exception) and `FiltersAsync` (fail closed). `DocumentService.Passwords.cs` (`RequireAppPermissionAsync`, skipped for Administrators, Core DocTypes and DocTypes without rules) is called next to every `RequireRestrictions` (get, update/submit/cancel, delete, rename) and for create; `PrepareListQueryAsync` appends the query filters. Tests: `AppPermissionTests`.

## Scheduled Job Log ([ADR-075](../../adr/075-scheduled-job-log.md))

`JobLog.cs`: `JobLogService` (`RecordAsync` never throws, `RecentAsync`, `PurgeAsync`). `SchedulerService.StepAsync` wraps every scheduler step; `AppJobRunner` logs each app job run. Core DocType `scheduled_job_log.json`. Retention `Qahwa:JobLog:RetentionDays` (default 30, purged hourly with the deleted-document copies).

## Table rows as CSV ([ADR-076](../../adr/076-table-csv.md))

Desk only: `lib/tableCsv.ts` (`rowsToCsv`, `rowsFromCsv`, `parseCsv`, `csvCell`; limits `MAX_TABLE_ROWS`, `MAX_TABLE_CSV_BYTES`) with `tableCsv.test.ts`, and the buttons in `controls/TableControl.tsx`. No API change: uploaded rows are validated by the server when the document is saved.

## Contacts and addresses ([ADR-077](../../adr/077-contacts-and-addresses.md))

Core DocTypes `contact.json`, `address.json`, `dynamic_link.json` (module "Contacts"). `DocumentService.ValidateAsync` now checks `DynamicLink` fields (type field filled, DocType exists and is not a child table, document exists). Names are reserved in `DocTypeDesigner.FrameworkNames`. Tests: `ContactAddressTests`, `DocTypeLinkTests`.

## Table multi-select ([ADR-078](../../adr/078-table-multiselect.md))

`DocFieldMeta.MultiSelect`; `DocTypeMetaExtensions.MultiSelectLink` (the one-Link rule), `MetaRegistry.CheckReferences` (reports a child that does not fit), `DocumentService.ValidateTableAsync` (each value once), `DocTypeLoader` (only Table fields). Desk: `chipField`/`chipValues`/`addChip`/`removeChip` in `lib/tables.ts`, the chip view in `TableControl`, a checkbox in the designer's `FieldCard`. Tests: `MultiSelectTests`, `tablesChips.test.ts`.

## Phone validation ([ADR-079](../../adr/079-phone-validation.md))

`FieldValueConverter.Convert`, case `FieldType.Phone` (optional `+`, digits and ` -.()`, 4 to 15 digits, at most 25 characters). Tests: `PhoneFieldTests`.

## View Log ([ADR-080](../../adr/080-view-log.md))

`ViewLog.cs`: `ViewLogService` (`RecordAsync` throttled and never throwing, called by `DocumentService.GetAsync`; `ViewersAsync` needs write). `DocTypeMeta.TrackViews`, Core DocType `view_log.json`, listed in `CollaborationHooks.Tables`. Endpoint `GET .../viewers` in `CollaborationEndpoints.cs`. Desk: designer checkbox, "Seen by" card in `DocRail`. Tests: `ViewLogTests`.

## Access Log ([ADR-081](../../adr/081-access-log.md))

`AccessLog.cs`: `AccessLogService` (`RecordAsync` never throws; `PurgeAsync`). Called by `DocumentService.ExportAsync` (after the read) and `PrintService.RenderAsync`. Core DocType `access_log.json`; retention `Qahwa:AccessLog:RetentionDays` in `SchedulerService`'s clean-up. Tests: `AccessLogTests`.

## Import/share/print/email rights ([ADR-082](../../adr/082-import-share-print-email-rights.md))

`DocPermMeta.Import/Share/Print/Email` (default true), `PermFlag`, `PermissionService.Allows/Require(…, PermFlag)`; enforced in `DataImportService.Require`, `ShareManager.RequireSharerAsync`, `PrintService.RenderAsync`, `EmailService.SendFromDocumentAsync`; `EffectivePermissions` carries the values to the Desk (designer table in `FieldCard`, buttons in `FormView`/`ListView`). Tests: `ExtraRightsTests`.

## List client scripts ([ADR-083](../../adr/083-list-client-scripts.md))

`public/client-script-worker.js` (`qahwa.ui.list.on`, `makeList`, `list_event`/`list_button` messages), `lib/clientScripts.ts` (`set_row_indicator`/`set_filter` ops in `sanitizeOps`, `ClientScriptRunner.runList`/`runListButton`), `lib/useListScripts.ts` (the hook; filters from `refresh` are ignored), `pages/ListView.tsx` (buttons, note, row marks). Tests: `listScripts.test.ts` runs the real worker file against a stand-in scope.

## Boot hooks ([ADR-084](../../adr/084-boot-hooks.md))

`Apps.cs`: `IAppBuilder.Boot`, `BootHook`, `BootContext`, `BootService.CollectAsync` (16 KB per app, failures logged). Endpoint `GET /api/boot` in `MethodEndpoints.cs`. Desk: `api.boot`, `lib/useBoot.ts`. Tests: `BootHookTests`, `The_desk_gets_what_installed_apps_hand_it_at_start_up`.

## Live-event backplane ([ADR-086](../../adr/086-realtime-backplane.md))

`RealtimeBackplane.cs` (`Publish` delivers locally and queues; `FlushAsync` writes `Realtime Event` rows with series names; `PollAsync` delivers other servers' rows, skips its own origin, purges after `Retention`). `HostingExtensions` registers it as the `IRealtimePublisher` when `Qahwa:Realtime:Backplane` is `database`; `RealtimeBackplaneService` (per site, started by `SiteHost` and `Program`) flushes and polls. Tests: `RealtimeBackplaneTests`, `PostgresRealtimeBackplaneTests` (both databases).

## Phone country picker ([ADR-087](../../adr/087-phone-country-picker.md))

`Country.dial_code` (`country.json`), `MasterData/countries.json` (`dialCode`), `MasterDataPatch`, `CountryDialCodePatch`. Desk: `lib/phone.ts` (`dialCountries`, `splitPhone` longest-prefix, `joinPhone`), `controls/PhoneInput.tsx`, the `Phone` case in `FieldControl`. Tests: `MasterDataTests`, `phone.test.ts`.

## Script reports ([ADR-088](../../adr/088-script-reports.md))

`ScriptReports.cs`: `ScriptReportService` (`List`, `RunAsync` with limits, role check, filter whitelist, Error Log on failure, Access Log line), `IAppBuilder.ScriptReport`, `ScriptReportContext/Data/Options`. Endpoints in `MethodEndpoints.cs`. Desk: `pages/ScriptReports.tsx`. Tests: `ScriptReportTests`, `Reports_written_by_apps_are_listed_and_run_for_the_people_who_may`.

## Personal Desk ([ADR-089](../../adr/089-personal-desk.md))

`DeskSettings.cs` (`DeskSettingsService`: effective layout = standard, then first configured role, then the person's own; permission-checked writes, optimistic concurrency), Core DocType `desk_setting.json` (rows keyed by a SHA-256 of scope and target), `DeskEndpoints.cs` (`/api/desk-settings`, `/api/desk-settings/roles/{role}`). Desk: `lib/desk.ts`, `pages/DeskEditor.tsx`, sidebar in `Shell`. Tests: `DeskSettingsApiTests`, `desk.test.ts`.

## Email Account and user password actions ([ADR-090](../../adr/090-email-account-and-user-password-actions.md))

`EmailAccounts.cs` (`EmailAccountService.GetAsync` per send, `IsConfiguredAsync`, `PublicUrlAsync`; `EmailSettings` is the host fallback), Core single DocType `email_account.json` (skipped by `BackupService`), `SmtpEmailSender` (fails when unconfigured; the log-only sender was removed), `UserSecurityEndpoints.cs` (`/api/users/{email}/password`, `/reset-password`, `/api/email/test`; Access Log actions `Set Password`, `Send Password Reset`), `AuthService.ResetIdentityAsync`/`IsAdministratorUserAsync`, `PasswordResetService` (five-part token: user, expiry, password version, user creation). Desk: `UserPasswordActions.tsx`, `TestEmailAction`. Tests: `UserSecurityApiTests`.

## Searchable fields, create in place, previews ([ADR-091](../../adr/091-searchable-fields-quick-create-previews.md))

Desk: `controls/SearchSelect.tsx`, `LinkSelect.tsx` ("Create new"), `QuickCreate.tsx` + `QuickCreateContext.ts` (no nesting), `FilePreview.tsx` (`pdfjs-dist`, bundled worker via `?url`), `lib/files.ts` (`filePreviewKind`). Tests: `DocumentControls.test.tsx`, `files.test.ts`.

## Document Action guidance ([ADR-092](../../adr/092-document-action-authoring-guidance.md))

`DocumentActionEngine` (Submit/Cancel need a submittable DocType; mapping lines `field = value`, each target once). Desk: `pages/DocumentActionFields.tsx` (shown by `FormView` for the Document Action DocType). Tests: `DocumentActionTests`, `DocumentControls.test.tsx`.

## Samples

`samples/employee-operations/` is a worked example built only through the REST API on a running site (DocTypes with workflows, a dashboard and a workspace). `README.md` there says how to install and verify it; it stores no credentials (`QAHWA_API_TOKEN` is read from the environment). It is a sample, not part of the framework or its tests.
