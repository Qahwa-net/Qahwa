# Qahwa — A Metadata-Driven Full-Stack Framework for .NET

> **Qahwa** (قهوة, "coffee"): a nod to Frappe (a coffee drink), with a regional identity.
> Tagline: *"Define your data. Brew your app."*
> Fallback name if rebranding is needed: **DotForge**.

Name availability check (2026-10-14): `qahwa` is free on NuGet and as a GitHub organization name (the GitHub *user* name is taken, so use an org such as `qahwa-framework`). Domains were **not** verified. Check `.dev`/`.io` manually before committing. Also check trademark conflicts if you plan to commercialize.

---

## 1. Vision and goals

Build a **low-code, metadata-driven application framework on .NET** where a developer describes a business entity once (a *DocType*) and the framework generates:

1. The database table(s) and migrations
2. A secured REST API (CRUD, search, bulk, export)
3. A rich web UI (list, form, report, kanban, calendar views)
4. Permissions, audit trail, workflow, naming series, and attachments
5. A plugin ("App") system so business modules (HR, CRM, Accounting) ship as installable packages
6. **Built-in collaboration on every document**: comments, sharing, assignment, and following (see 5.10)
7. **Dynamic filters and per-user list settings** like Frappe's list view: filter bar, saved filters, column picker, sidebar group-by counts (see 5.11)

### Non-goals (v1)
- Not a full ERP (that is an app built *on* Qahwa, like ERPNext on Frappe)
- No visual drag-and-drop DocType designer until the core is stable (it is a Phase 4 feature)
- No microservice-first design; start as a modular monolith

### Success criteria for the MVP
Define a `Customer` DocType in JSON, run one command, and get a working table, API, list view, and form, **with zero additional code**.

---

## 2. Concept mapping: Frappe → Qahwa

| Frappe | Qahwa | Notes |
|---|---|---|
| DocType | `DocType` (JSON/YAML + DB mirror) | The core abstraction |
| DocField | `DocField` | Field types, validation, display rules |
| Document | `Document` | Dictionary-backed dynamic record with lifecycle |
| Child Table | `Table` field → child DocType (`IsChildTable`) | Parent/child relation via `parent`, `parentfield` columns |
| Controller (.py) | `IDocumentController<T>` / `[DocType("X")]` class | DI-resolved, lifecycle hooks |
| hooks.py | `IAppModule` + `HookRegistry` | Event subscriptions, scheduled jobs, overrides |
| Bench | `qahwa` CLI (`dotnet tool`) | init, new-site, new-app, migrate, start |
| Site | Tenant | One DB per tenant |
| App | Module package (NuGet/assembly) | Contains DocTypes, controllers, UI extensions |
| Desk | Qahwa Desk (SPA) | Metadata-driven UI |
| frappe.client / REST | `/api/resource/*`, `/api/method/*` | Same convention, easy migration for Frappe users |
| Role / DocPerm | `Role` / `DocPerm` | Row-level and field-level rules |
| Workflow | `Workflow` DocType | State machine on any DocType |
| Comment / timeline | `Comment` DocType + timeline API | Comments, mentions, and system events on every document |
| ToDo (assign) | `ToDo` DocType (`reference_doctype`, `reference_name`, `allocated_to`) | Assign any document to one or more users |
| DocShare | `DocShare` DocType | Per-document, per-user grants (read/write/share/submit) |
| Document Follow | `DocumentFollow` DocType | Follow a document and get notified of changes |
| List View Settings | `ListViewSettings` DocType (per DocType) | Admin-defined list defaults and toggles |
| User Settings | `UserSettings` store (per user, per DocType) | Remembered columns, sort, filters, page size, view |
| List Filter (saved filters) | `ListFilter` DocType | Named filter sets, private or shared |
| Server Script | Roslyn C# script (sandboxed) | Phase 4 |
| Redis queue + workers | Hangfire / Quartz + `IBackgroundJobClient` | Phase 3 |
| Socket.IO realtime | SignalR | Phase 3 |

---

## 3. Technology decisions

### Decided defaults
| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 9 (LTS path to .NET 10)** | Current, performant, minimal APIs |
| Web | **ASP.NET Core Minimal APIs + endpoint filters** | Generic routes are easier than controllers |
| Data access | **Dapper + custom `SqlBuilder` (dialect-aware)** | Runtime schema changes fit poorly with EF Core's compiled model |
| Primary DB | **PostgreSQL** (JSONB gives a flexible escape hatch) | Also support SQL Server via a dialect abstraction |
| Metadata storage | JSON files in each app (source of truth) + mirrored into `tabDocType` tables at migrate time | Git-friendly, like Frappe |
| Auth | **Cookie sessions + ASP.NET Core Identity password hasher (PBKDF2)**; JWT and API key/secret later | Users are `User` DocTypes, so the full Identity/EF stores do not fit. See ADR-002 |
| Frontend | **React + TypeScript + Vite** (Desk SPA) | Largest ecosystem for form builders and data grids. Blazor is a viable alternative if you want all-C# |
| UI components | TanStack Table/Query, React Hook Form, Radix/shadcn | |
| Background jobs | Hangfire (PostgreSQL storage) | Dashboard included |
| Realtime | SignalR | |
| Caching | `IDistributedCache` (Redis optional) + in-memory meta cache | |
| Search | PostgreSQL full-text first, pluggable provider later | |
| Logging/telemetry | Serilog + OpenTelemetry | |
| Tests | xUnit, Testcontainers (real DBs), Playwright (UI) | |
| Packaging | NuGet for framework and apps, npm for the Desk UI | |

### Key architectural decision: dynamic vs typed documents
**Hybrid approach**:
- **Runtime**: `Document` is a dictionary-backed object (`IDictionary<string, object?>` with typed accessors). This is flexible and needs no recompile when metadata changes.
- **Optional typed layer**: a **source generator** reads DocType JSON and emits strongly typed C# partial classes (`Customer : Document`) for developers who want IntelliSense and compile-time safety in controllers.

---

## 4. Solution structure

```
Qahwa/
├─ src/
│  ├─ Qahwa.Abstractions/        # Interfaces, attributes, contracts (no dependencies)
│  ├─ Qahwa.Core/                # Meta model, Document engine, lifecycle, permissions
│  ├─ Qahwa.Data/                # SqlBuilder, dialects, schema sync, migrations
│  │  ├─ Qahwa.Data.Postgres/
│  │  └─ Qahwa.Data.SqlServer/
│  ├─ Qahwa.Api/                 # Generic REST endpoints, auth middleware, filters
│  ├─ Qahwa.Hosting/             # Site (tenant) resolution, app loader, DI composition
│  ├─ Qahwa.Jobs/                # Background jobs and scheduler
│  ├─ Qahwa.Realtime/            # SignalR hubs
│  ├─ Qahwa.Scripting/           # Roslyn sandbox (Phase 4)
│  ├─ Qahwa.SourceGenerators/    # DocType → typed C# (Phase 3)
│  ├─ Qahwa.Cli/                 # `qahwa` dotnet tool
│  ├─ Qahwa.Server/              # Executable host
│  └─ Qahwa.Desk/                # React SPA (served as static files)
├─ apps/
│  ├─ Qahwa.Apps.Core/           # Built-in DocTypes: User, Role, DocPerm, File, Comment, Version, etc.
│  └─ Qahwa.Apps.Sample/         # Sample "Contacts/CRM-lite" app
├─ tests/
│  ├─ Qahwa.Core.Tests/
│  ├─ Qahwa.Data.Tests/          # Testcontainers
│  ├─ Qahwa.Api.Tests/
│  └─ Qahwa.E2E/                 # Playwright
├─ docs/
├─ samples/
└─ build/                        # CI, Directory.Build.props, versioning
```

Dependency rule: `Abstractions ← Core ← Data/Api/Jobs/Realtime ← Hosting ← Server`. Apps depend only on `Abstractions` (plus `Core` for the base classes).

---

## 5. Core subsystems (detailed design)

### 5.1 Metadata model

**DocType JSON (source of truth, lives in `apps/<app>/doctypes/customer/customer.json`)**

```json
{
  "name": "Customer",
  "module": "Selling",
  "naming": { "rule": "series", "pattern": "CUST-.#####" },
  "titleField": "customer_name",
  "isSubmittable": false,
  "isChildTable": false,
  "trackChanges": true,
  "fields": [
    { "name": "customer_name", "type": "Data", "label": "Customer Name", "required": true, "unique": true, "inListView": true },
    { "name": "customer_type", "type": "Select", "options": ["Company", "Individual"], "default": "Company" },
    { "name": "territory", "type": "Link", "options": "Territory" },
    { "name": "credit_limit", "type": "Currency" },
    { "name": "contacts", "type": "Table", "options": "Customer Contact" }
  ],
  "permissions": [
    { "role": "Sales Manager", "read": true, "write": true, "create": true, "delete": true },
    { "role": "Sales User", "read": true, "write": true, "ifOwner": false }
  ]
}
```

**Field types (v1 set)**: Data, Text, LongText, Int, Float, Decimal, Currency, Percent, Check, Select, Date, Datetime, Time, Link, DynamicLink, Table, Attach, AttachImage, Json, Password (hashed or encrypted), Email, Phone, Url, Code, Markdown, Color, Rating, ReadOnly, Section/Column/Tab break (layout).

**C# model**: `DocTypeMeta`, `DocFieldMeta`, `DocPermMeta`, `NamingRule`, with a `FieldTypeRegistry` mapping each type to: DB column type per dialect, validator, serializer, and UI control hint. New field types register through DI, so they are extensible.

**Meta cache**: loaded at startup per site, invalidated on migrate or DocType edit. Versioned with a hash.

### 5.2 Schema engine (the hardest backend piece)

Responsibilities:
- Diff `DocTypeMeta` against the live DB (information_schema) → produce a **migration plan**
- Operations: create table, add column, widen/alter column, add/drop index, add FK (optional), rename (explicit via `oldName` in the JSON, never inferred)
- **Safety policy**:
  - Additive changes are auto-applied
  - Type narrowing or drop column → requires `--force` or a manual patch, and is logged
  - Always wrap DDL in a transaction (PostgreSQL) or use per-statement safeguards (SQL Server)
  - Dry-run mode (`qahwa migrate --plan`) prints the SQL
- Standard columns on every table: `name` (PK, text), `creation`, `modified`, `modified_by`, `owner`, `docstatus`, `idx`, plus `parent`, `parenttype`, `parentfield` for child tables
- Table naming: `tab{DocType}` like Frappe, or snake_case (configurable)
- **Data patches**: `patches.txt` + `IPatch` classes run once and are tracked in a `PatchLog` table (like Frappe patches)

### 5.3 Document engine

```
Document.Insert()/Save()/Submit()/Cancel()/Delete()
  → permission check
  → set defaults / naming
  → before_validate → validate (field rules, required, unique, link existence, controller.Validate)
  → before_save → DB write (parent + children in one transaction) → after_save
  → on_update / on_submit / on_cancel ...
  → emit events (realtime, webhooks, audit/version row)
```

- **Lifecycle hooks** via `IDocumentController` (per DocType) and `IDocumentEventHandler` (global/cross-app, ordered)
- **Optimistic concurrency** using the `modified` timestamp (mirrors Frappe's TimestampMismatch)
- **Child table handling**: diff by `name`/`idx`, upsert, delete removed rows
- **Naming**: series (atomic counter table with row lock), field-based, prompt (user-supplied), hash, expression
- **Versioning**: `Version` DocType stores field-level diffs for **every** DocType by default (`trackChanges` on unless a DocType opts out), in the same transaction as the change (see 5.10.1)
- **Docstatus**: 0 draft, 1 submitted, 2 cancelled (for submittable docs)
- **Link validation and cascade policy**: restrict delete if linked (with a clear error listing references)

### 5.4 Query layer

- `QueryBuilder` that takes Frappe-style filters: `[["status","=","Open"],["creation",">","2025-01-01"]]`, fields, order_by, group_by, limit/offset
- **Whitelist** all field and table identifiers from metadata. **Never** concatenate user input into SQL. Parameterize all values.
- Permission-aware: automatically adds row-level conditions (owner-only, user-permissions on links, share records)
- Report/aggregation support: `SELECT … GROUP BY` with aggregate functions from a whitelist
- Full-text search endpoint over fields flagged `searchable`

### 5.5 Permission system

Layers (evaluated in this order):
1. **Role permissions** on DocType (read, write, create, delete, submit, cancel, amend, export, share, report)
2. **Permission levels** (`permlevel`) for field groups
3. **Owner-only** (`ifOwner`)
4. **User permissions** (restrict a user to specific Link values, e.g. a Territory)
5. **Sharing** (explicit per-document grants)
6. **Custom hooks** (`IPermissionEvaluator`) for complex rules

Results are cached per user per DocType. Enforcement lives in the **Document engine and query layer**, never only in the API layer.

### 5.6 API surface

```
GET    /api/resource/{doctype}                 list (filters, fields, order, paging)
POST   /api/resource/{doctype}                 create
GET    /api/resource/{doctype}/{name}          read
PUT    /api/resource/{doctype}/{name}          update
DELETE /api/resource/{doctype}/{name}          delete
POST   /api/method/{app}.{path}                whitelisted RPC methods ([Whitelist] attribute)
GET    /api/meta/{doctype}                     DocType metadata for the UI
POST   /api/doc/{doctype}/{name}/submit|cancel workflow-ish actions
POST   /api/files/upload
GET    /api/search?q=
POST   /api/auth/login | logout | refresh
```

- Consistent error envelope (`{ code, message, details }`), RFC 7807 problem details
- OpenAPI generated dynamically from metadata
- Rate limiting, request size limits, CORS configuration per site

### 5.7 Apps and plugin system

- An **App** is a class library with `IAppModule` (name, version, dependencies), embedded DocType JSONs and patches, optional controllers and UI extension bundles
- Loaded via `AssemblyLoadContext` from the apps folder (or referenced as NuGet packages)
- `qahwa install-app <name> --site <site>` registers the app, syncs its DocTypes, runs patches
- Dependency resolution and version constraints between apps
- **Override/extension points**: override controller, extend DocType via `CustomField` and `PropertySetter` (changes stored in the DB, not in app files)

### 5.8 Multi-tenancy (Sites)

- Tenant resolution: by host header (`acme.example.com`) or a path/header
- **Database-per-site** (default, matches Frappe), with a per-site config (`site_config.json`)
- Site-scoped DI services and caches
- CLI: `qahwa new-site acme --db postgres`, `qahwa migrate --site acme`

### 5.9 Desk UI (metadata-driven frontend)

Views:
- **List view** (filters, saved filters, column chooser, bulk actions, export)
- **Form view** (sections/columns/tabs, field controls, child table grid, comments, attachments, timeline, assignment, share)
- **Report view** (group-by, charts, saved reports) and **Dashboard** (number cards, charts)
- **Kanban**, **Calendar**, **Tree** views (Phase 3–4)
- **Global search**, **Notifications**, **Awesomebar** (command palette)
- Form scripting via **client-side hooks** (`qahwa.ui.form.on('Customer', { refresh(frm) {...} })`)

Architecture: a **field control registry** (type → React component) that plugins can extend. Layout is rendered from `fields` ordering and break fields. All data flows through TanStack Query with the meta cache.

### 5.10 Document collaboration: comments, share, assign, follow

Every DocType gets these four features automatically. No per-DocType configuration is needed, though a DocType can opt out with `"collaboration": false`. They are built as ordinary DocTypes on top of the Document engine, so they inherit permissions, audit, API, and UI for free.

| Feature | Storage | Behavior |
|---|---|---|
| **Comments** | `Comment` (`reference_doctype`, `reference_name`, `comment_type`, `content`, `comment_by`) | Rich-text comments, `@mentions` (notify the user), edit/delete own comments. `comment_type` also records system events: Info, Edit, Label, Attachment, Assignment, Share |
| **Assign** | `ToDo` (`reference_doctype`, `reference_name`, `allocated_to`, `assigned_by`, `status`, `priority`, `date`, `description`) | Assign to one or more users. Assignee is notified; status Open → Closed/Cancelled; "Assigned to me" filter and a My ToDos list. Auto-close on document submit/cancel via a rule |
| **Share** | `DocShare` (`user`, `share_doctype`, `share_name`, `read`, `write`, `share`, `submit`) | Grants a user access to one document beyond their role. Evaluated by the permission layer (layer 5 in 5.5). Only users with `share` permission can grant |
| **Follow** | `DocumentFollow` (`ref_doctype`, `ref_docname`, `user`, `frequency`) | Users follow a document manually, or automatically when they create, comment on, assign, or share it. Followers get a notification (in-app, then email digest) on update, comment, and status changes |

**Timeline.** The form shows one merged, chronological timeline from `Version` (field changes), `Comment`, assignments, shares, and attachments.

**API**
```
GET/POST   /api/resource/{doctype}/{name}/comments
POST       /api/method/qahwa.desk.form.assign_to.add | remove | close
POST       /api/method/qahwa.share.add | remove      GET /api/method/qahwa.share.get_users
POST       /api/method/qahwa.follow.follow | unfollow GET /api/method/qahwa.follow.is_following
GET        /api/resource/{doctype}/{name}/timeline
```

**Design rules**
- Collaboration rows are linked by `(reference_doctype, reference_name)` with an index on that pair. They are cascaded or archived when the parent document is deleted.
- Reading comments, assignments, and shares requires **read** permission on the parent document. Writing a comment requires read plus comment right. Sharing requires the `share` permission, so a user can never grant more than they hold.
- All actions raise events on the event bus (`comment_added`, `assigned`, `shared`, `followed`). The notification system (Phase 3) subscribes to those events, so collaboration does not depend on the email subsystem.
- Mentions and assignment are resolved against `User`, and disabled users are skipped.

### 5.10.1 Updates log and comments on every document

Every document, of every DocType (including ones made in the designer and customised ones), has a visible **history** and a **comment thread**, with no setup. This is a hard requirement, not an option.

**Updates log (document history).**
- Each insert, update, submit, cancel, rename, merge and delete writes one `Version` row: `(ref_doctype, ref_docname, event, changed_by, changed_at, data)`. The `data` is a field-level diff `[field, old, new]`, plus added, changed and removed child-table rows (keyed by row name) and the previous document for deletes (so "Deleted Documents" can restore it).
- It is **on by default for all DocTypes** (`trackChanges` is on unless a DocType opts out with `"trackChanges": false`), written in the **same transaction** as the change, so history can never be missing or wrong.
- Only real changes are logged (same rule as the update path: unchanged echoes are not changes). Password fields and fields the framework marks sensitive are logged as "changed" without values.
- The form shows it as an **Updates** timeline: who, when, and "Status: Draft → Submitted", with collapsed long text. Fields the viewer may not read (permission levels) are hidden from the diff.
- Reading it needs read permission on the document; nobody can edit or delete history (only an Administrator can purge by retention policy).
- API: `GET /api/resource/{doctype}/{name}/versions`; the merged timeline (5.10) includes it.

**Comments.**
- Every document has a comment thread: add, edit and delete your own comment, `@mention` people (they are notified), and system events (assignment, share, attachment, rename) appear in the same thread as non-editable entries.
- A comment never changes the document and does not need write permission (read plus comment right), and is not blocked by a submitted or cancelled status.
- The list view shows a comment count; the form sidebar shows who is following and who has commented.
- API: `GET/POST /api/resource/{doctype}/{name}/comments`, `PUT/DELETE` on a comment you wrote.

Both are ordinary DocTypes (`Version`, `Comment`) written by the Document engine, so they inherit permissions, concurrency, backup and API access. They are built in step **L7**, with the backend (engine hook for the version row, comment endpoints) before the Desk timeline.

### 5.11 Dynamic filters and list settings (Frappe-style list view)

**Dynamic filters.** Users build filters at runtime from the UI, and the same filter model is accepted by the API and by saved filters.

- **Filter model**: `[field, operator, value]`, optionally with a child-table field (`[childDoctype, field, operator, value]`, Phase 3).
- **Operators by field type**: text (`=`, `!=`, `like`, `not like`, `in`, `not in`, `is set / not set`), numeric and currency (`=`, `!=`, `>`, `<`, `>=`, `<=`, `between`), date/datetime (`=`, `>`, `<`, `between`, **timespan**: today, yesterday, this week, last 7 days, this month, last quarter, this year, ...), Link (`=`, `!=`, `in`, descendants for tree DocTypes), Check (`=`), Select (`=`, `!=`, `in`).
- **Dynamic values**: tokens resolved on the server, such as `__user`, `__today`, `__me_and_my_team`, so saved filters stay current.
- **Filter bar UI**: a standard filter row (fields marked `inStandardFilter` in the DocType), an "Add filter" popover with a field picker and the operator list for that type, filter chips with a clear button, and a "Filters (n)" counter.
- **Sidebar group-by counts**: "Assigned to", "Created by", "Tags", "Liked by", plus any field marked `inGroupBy`. Clicking a value adds a filter. Counts come from a single aggregate query and can be turned off per DocType.
- **Saved filters** (`ListFilter`: `reference_doctype`, `filter_name`, `filters` JSON, `for_user`, `shared`). Private by default; shared filters need a permission.
- **Quick search**: a search box over the fields marked `inListView`/`searchable`, translated into `like` filters.

**List settings.** Two layers, like Frappe:

1. **DocType-level defaults** (admin): `ListViewSettings` / DocType metadata. Flags per field: `inListView`, `inStandardFilter`, `inPreview`, `bold`, `inGroupBy`, `listColumnWidth`. Toggles per DocType: `disableCount`, `disableSidebarStats`, `disableAutoRefresh`, `allowBulkEdit`, default `sortField` and `sortOrder`, default page size, `titleField`, and status indicators (a field → color mapping, such as `Open` = orange).
2. **User-level settings** (`UserSettings`, keyed by user + DocType): chosen columns and their order and width, sort, page size, last used filters, last used view (List/Report/Kanban/Calendar), and sidebar collapse state. They are restored on the next visit, with a "Reset to default" action.

**Report view** reuses the same filter and settings model, plus column picker, group-by, aggregate rows, and export.

**API**
```
GET  /api/resource/{doctype}?filters=[...]&fields=[...]&order_by=...&limit_start=&limit_page_length=
GET  /api/method/qahwa.desk.reportview.get_count?doctype=&filters=
GET  /api/method/qahwa.desk.listview.get_group_by_count?doctype=&field=&filters=
GET/PUT /api/user-settings/{doctype}
GET/POST/DELETE /api/resource/ListFilter
```

**Engine changes needed (Phase 2)**
- Extend `FilterOp` with `Between`, `NotIn`, `NotLike`, `Timespan`, and map dynamic tokens before validation.
- Keep the security rule: field names whitelisted from metadata, all values parameterized, and filters on fields the user has no `permlevel` access to are rejected.
- Add `inStandardFilter`, `inPreview`, `inGroupBy`, `bold`, and `listColumnWidth` to `DocFieldMeta`. Add `sortField`, `sortOrder`, and `disableCount` style flags to `DocTypeMeta`.
- Add `GroupByCount` and `Count` operations to the repository.

### 5.12 Cross-cutting features (Phase 3+)

Workflow engine, tags and likes, attachments (local/S3/Azure Blob), email (queue plus templates), notifications (in-app/email/webhook), print formats (Razor/Scriban to PDF), data import/export (CSV/Excel with validation), webhooks, audit log, scheduled jobs, localization (i18n with Arabic/RTL support, which is a first-class goal), and custom reports.

---

## 6. Security plan

- Parameterized SQL only; identifier whitelisting from metadata; unit tests with injection payloads
- Password hashing via ASP.NET Identity (PBKDF2/Argon2); `Password` field type encrypted at rest (data-protection API)
- CSRF protection for cookie sessions, secure cookie flags, JWT short expiry + refresh
- Output encoding and HTML sanitizer for rich text fields
- File upload validation (size, type sniffing, antivirus hook) and private-by-default files
- Roslyn scripting sandbox: restricted APIs, timeouts, memory limits, no file/network access by default
- Audit log for auth events and permission changes
- Secrets via environment/KeyVault, never in site config committed to Git
- Dependency scanning (`dotnet list package --vulnerable`), SAST in CI

---

## 7. Phased roadmap

> **Re-prioritised for low-code.** The principle in section 13 (everything an app needs is *data edited in the UI*, code is only an escape hatch) changes the order of work. Section 13.2 is the current roadmap; the phase lists below are kept for history and detail. Full Frappe feature coverage is tracked in Appendix A.

Timeline assumes **1–2 full-time developers**. Scale it up or down accordingly.

### Phase 0: Foundations (Week 1–2)
- Repo, solution layout, `Directory.Build.props`, central package management, EditorConfig, analyzers
- CI (GitHub Actions): build, test, format check; Testcontainers for Postgres
- Architecture Decision Records (ADR) folder; coding standards
- **Deliverable**: empty skeleton that builds and runs tests in CI

### Phase 1: MVP: "Customer in 10 minutes" (Week 3–8)
1. `DocTypeMeta` model + JSON loader + validation
2. Field type registry (10 core types)
3. Postgres dialect + schema sync (create table, add column) + `PatchLog`
4. `Document` class + basic insert/read/update/delete + standard columns
5. Generic CRUD endpoints + `/api/meta`
6. Filters → SQL query builder (whitelisted)
7. Auth (cookie login/logout, lockout, rate limit), `User`, `Role` and `Has Role` DocTypes, basic role permissions: **done**
8. CLI: `new-site`, `migrate`, `start`
9. Desk v0: login, sidebar of DocTypes, generic list + form for simple fields

**Exit criteria**: JSON → working app with persistent data; 70%+ unit test coverage on Core and Data; integration tests green in CI.

### Phase 2: Core framework (Week 9–16)
- Child tables, Link and DynamicLink fields, Select/Check/Date family, validation pipeline
- Lifecycle hooks and controllers (`IDocumentController`), event bus
- Naming series, optimistic concurrency, `Version` tracking, comments, attachments
- Full permission model (permlevel, ifOwner, user permissions, sharing)
- Submittable documents (docstatus, submit/cancel/amend)
- Meta caching + invalidation, error envelope, OpenAPI generation
- SQL Server dialect (proves the dialect abstraction)
- **Dynamic filters and list settings** (5.11): extended filter operators, dynamic value tokens, group-by counts, `ListFilter`, `UserSettings`, and `ListViewSettings`
- **Document collaboration** (5.10): `Comment`, `ToDo` (assign), `DocShare`, `DocumentFollow`, timeline API, mention parsing, and the permission-layer hook for shares
- Desk v1: child-table grid, link autocomplete, filter bar with saved filters, sidebar group-by counts, column picker, bulk actions, form layout (sections/columns/tabs), and a form sidebar with Assign / Share / Follow / Comments timeline

### Phase 3: Platform (Week 17–28)
- App/plugin system with `AssemblyLoadContext`, `new-app`, `install-app`, dependency resolution
- Multi-site/tenancy, site config
- Background jobs (Hangfire) + scheduler + job DocTypes; SignalR realtime
- Workflow engine, notifications, email queue, print formats (PDF)
- Data import/export, global search, custom fields and property setters
- Source generator for typed documents
- Reports (query/report builder), dashboard charts and number cards
- Localization and RTL (Arabic) in Desk

### Phase 4: Ecosystem (Week 29+)
- Sandboxed server and client scripts (Roslyn)
- Visual DocType designer in Desk
- Kanban/calendar/tree views, webhooks, OAuth2/OIDC provider support
- Marketplace-style app distribution (NuGet feed), upgrade tooling
- Docs site, tutorials, templates (`dotnet new qahwa-app`)
- First flagship app (e.g., **Qahwa CRM**, to dogfood the framework)

---

## 8. Testing strategy

| Level | Tooling | Focus |
|---|---|---|
| Unit | xUnit, FluentAssertions | Meta validation, query builder, naming, permission evaluator |
| Integration | Testcontainers (PG + SQL Server) | Schema sync diffs, document lifecycle, transactions, concurrency |
| Contract/API | WebApplicationFactory | Endpoints, error envelopes, authz matrix |
| Security | Custom suites | SQL injection payloads, IDOR, permission bypass attempts |
| E2E | Playwright | Create DocType → use list/form, and key user flows |
| Performance | k6/BenchmarkDotNet | List with 1M rows, meta cache hit rate, bulk insert |
| Migration | Snapshot tests | Schema diff output stays stable |

Rule: every bug in the schema engine or permission system gets a regression test before the fix.

---

## 9. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Schema sync corrupts or loses data | Critical | Additive-by-default, dry-run, backups prompt, explicit rename, transactional DDL, heavy integration tests |
| SQL injection through dynamic queries | Critical | Identifier whitelist from metadata, parameters only, dedicated security tests |
| Permission bypass through the generic API | Critical | Enforce inside Document engine and query layer; authz test matrix |
| UI scope explosion | High | Ship a minimal Desk each phase; reuse libraries; defer the designer |
| Dynamic documents hurt DX | Medium | Source-generated typed layer, good docs and analyzers |
| Performance with dynamic metadata | Medium | Meta cache, compiled accessors, query plan review, indexes from metadata |
| Cross-DB dialect leaks | Medium | Dialect abstraction from day one; run the test suite on both DBs in CI |
| One-person bottleneck | Medium | Strong docs and ADRs, small vertical slices, early open source for contributors |
| Roslyn scripting security | High | Postpone to Phase 4; strict sandbox; or offer a safe expression language first |

---

## 10. Developer experience targets

- `dotnet tool install -g qahwa-cli`
- `qahwa init my-bench` → `qahwa new-site dev.local` → `qahwa new-app my_app` → `qahwa start`
- Hot reload of DocType JSON in development (auto-migrate on file change)
- JSON Schema for DocType files (IDE autocomplete and validation)
- `dotnet new qahwa-app` template; clear error messages that point to the DocType and field
- Documentation: Getting Started (10 min), Concepts, DocType reference, API reference, Cookbook, Architecture

---

## 11. Immediate next steps (first two weeks)

1. Create the GitHub org `qahwa-framework` and repo `qahwa`; reserve NuGet prefix `Qahwa.*`
2. Verify domain availability (`qahwa.dev`, `qahwa.io`) and make a decision on trademark
3. Scaffold the solution (Phase 0) and set up CI
4. Write the **DocType JSON Schema** and 3 example DocTypes (`Customer`, `Territory`, `Customer Contact`)
5. Spike **schema sync** against PostgreSQL with Testcontainers. This is the riskiest component, so do it first
6. Spike the **generic list/form UI** against a hand-written metadata endpoint
7. Write ADR-001 (Dapper vs EF Core), ADR-002 (dynamic vs typed documents), ADR-003 (DB-per-site)

---

## 12. Open decisions (need your input)

| # | Question | Options | Recommendation |
|---|---|---|---|
| 1 | Frontend | React + TS, or Blazor | **React** (ecosystem, hiring); Blazor if you want a C#-only stack |
| 2 | Primary DB | PostgreSQL, SQL Server | **PostgreSQL** first, SQL Server in Phase 2 |
| 3 | License | MIT, Apache-2.0, AGPL/commercial dual | MIT or Apache-2.0 for adoption |
| 4 | Typed documents | Dynamic only, or dynamic + source generator | **Both** (generator in Phase 3) |
| 5 | Scope of first flagship app | CRM, HR, Accounting | CRM-lite (smallest useful surface) |
| 6 | Localization | English only, or Arabic/RTL from day one | **RTL-ready from day one** (cheaper than retrofitting) |

---

## 13. Low-code strategy

### 13.1 Principle
**Build an application with as little code as possible.** Every artifact of an application is a *document* that can be created and changed in the Desk, stored in the database, and applied at runtime without a rebuild or restart:

| Artifact | Low-code form (Desk UI) | Code escape hatch |
|---|---|---|
| Data model | **DocType Designer** (fields, naming, layout, list settings) | JSON files in an app |
| Extending a standard DocType | **Customize Form** / Custom Field / Property Setter | app override |
| Field behavior | `depends_on`, `mandatory_depends_on`, `read_only_depends_on`, `fetch_from`, default expressions, validation rules, all in a safe expression language | server/client script |
| Access control | **Role Permission Manager**, User Permissions, sharing | permission hooks |
| Business process | **Workflow Builder**, Assignment Rules, Auto Repeat | server script / hooks |
| Messaging | **Notification** rules (email, system, Slack, SMS), Email Templates | script |
| Reports and analytics | **Report Builder**, Dashboard Charts, Number Cards, Dashboards | SQL/script report |
| Navigation | **Workspace Builder** (shortcuts, charts, cards, links) | none |
| Documents out | **Print Format Builder**, Letter Heads, PDF | HTML/template |
| Public pages | **Web Forms** and portal pages | web templates |
| Data in/out | **Data Import / Export** tools, bulk edit | API |
| Integration | **Webhooks**, API keys, OAuth apps | script |

Rules that keep it honest:
1. If a feature needs a developer to edit a file, it is not finished. The file format (JSON) stays as the *export* of what the UI produced, so it is Git-friendly and shippable as an app (fixtures).
2. UI-created definitions are data (`__doctype`, `Custom Field`, `Workflow`, ...), versioned and exportable.
3. Logic written in the UI uses one **sandboxed expression language** evaluated identically on server and client. Full scripting comes after it and is never required for common cases.
4. Standard (app-shipped) definitions are read-only in the UI; users extend them through Customize Form instead of editing them, so upgrades never clobber customisations.
5. Schema changes from the UI follow the same safety rules as migrations: additive only, preview before apply, never drop data silently.

### 13.2 Current roadmap (low-code first)

| Step | Scope | Status |
|---|---|---|
| **L1: DocType Designer** | Create, edit and delete custom DocTypes in the Desk: fields, field properties, naming, title field, list columns, permissions, live form preview, SQL change preview, safe schema apply | **done (v1)**. Not yet: child tables (L2), rename, type change of saved fields, depends_on (L4), drag-and-drop ordering (up/down buttons today) |
| L2: Child tables | `Table` fields, child DocTypes, grid editing in forms, designer support | **done (v1)**. Rows save atomically with the parent; the form has an editable grid. Not yet: Table MultiSelect, nested tables, import/export of rows, filters on child fields, row-level drag and drop (up/down buttons today) |
| L3: Customize Form | Custom Fields and Property Setters on any DocType (standard included), reorder fields, hide/rename, per-DocType list settings | **done (v1)**. Custom fields (real columns, never dropped), property changes on standard fields (label, help, hidden, read-only, required, default, list, permission level, Select choices, conditions), order, title field, reset; framework DocTypes Administrator-only. Not yet: child tables, per-DocType list settings (L7), fixtures export (L12) |
| L4: Field logic | `depends_on` family, `fetch_from`, default and validation expressions, expression language shared by server and client | **done (v1)**. A safe expression language (no code, no loops) evaluated identically on server and in the Desk, checked by 220+ shared test vectors. Show/require/read-only conditions, calculated fields (formulas, also in table rows, `parent.x`, `sum(table, 'field')`), expression defaults (`=today()`), fetch from a linked document, and document/row validation rules, all edited in the designer with live checks. Rules across the rows of a table (average, smallest, largest, count and sum of matching rows, distinct values; ADR-121). Formulas that read a field of a linked document, with the person's own permissions (`lookup`, ADR-122). Not yet: formulas over many other documents, per-field validation messages by type, date/time pickers in expressions |
| L5: Permissions UI | Role Permission Manager, permission levels, User Permissions, Role/Module Profiles, sharing | **done (v1)**. Done: permission levels (0–9) on fields and rules, enforced on the server (hidden fields are absent from documents, lists, filters; unwritable fields are refused), the Role Permission Manager (customise, reset, per-level rules, Core DocTypes Administrator-only), designer support. User Permissions (restrict a user to some linked values, enforced in lists, reads, writes and link fields). Sharing a document with one person (read or write, from a Share panel on the form). Submittable documents with submit and cancel rights (draft, submitted, cancelled; locked after submit). Role Profiles and Module Profiles (roles from a profile; blocked modules), and the export (CSV) and report (grouped summary) rights. Not yet: import and share rights, amend |
| L6: Special DocTypes | Single (settings) DocTypes, Tree DocTypes, Submittable, naming series manager, rename/merge | **done (v1)**. Submittable, Single (settings) DocTypes, Tree DocTypes with a tree view, the naming series manager, and rename/merge with every link updated. Not yet: tree drag and drop, descendants queries, renaming text references |
| L7: Collaboration and list power | **Updates log (version history) and comments on every document** (see 5.10.1), assign, follow, timeline, saved filters, user list settings, group-by sidebar, bulk edit. Sharing is already done (L5) | **done (v1)**. Done: the updates log (Version rows, field-level diffs, same transaction, on by default for every DocType, hidden fields filtered, read-only) and comments (@mentions highlighted, own edit/delete, system notes for shares) with an Activity panel on every form (see 5.10.1, ADR-014). Assign (ToDo, with notes, auto read access), follow (automatic on create, comment, assign) and in-app notifications (a bell: mentions, assignments, comments, updates, shares; only for documents the recipient may read; ADR-015). Per-person list settings (columns, page size, sort), saved and shared filters, and bulk update and delete of ticked documents (ADR-016). Group-by sidebar with counts, coloured status labels per DocType and comment counts in lists (ADR-017). Not yet: email notifications, assignment rules, default list views per DocType, bulk assign and export, retention and restore of deleted documents |
| L8: Process automation | Workflow Builder, Assignment Rules, Auto Repeat, Notification rules, Email Templates, scheduler UI | **done (v1)**. Done: the workflow engine and Workflow Builder (states, role- and condition-guarded transitions, self-approval rule, edit-by-state, submit/cancel through the workflow, inbox, ADR-018) and assignment rules (round robin, load balancing, from a field, conditions, close conditions, ADR-020), auto repeat with the first background scheduler (daily to yearly, date fields, catch-up, safe on several servers, ADR-021) and notification rules (created, changed, field change, submit, cancel, days before or after a date; recipients by user, role, field, owner, followers; in-app, ADR-022). and email (a queue sent by the scheduler with retries, templates, emailing from a document, email as a notification channel, SMTP or log-only, ADR-023). Approvals by several people (N of M, ADR-118), escalation of documents that wait too long (ADR-119) and approve/reject by email link (ADR-120). |
| L9: Presentation | Report Builder, Dashboard Charts, Number Cards, Workspace Builder, Print Format Builder, Letter Head | **done (v1)**. Done: dashboards with number, chart (bars, line, pie; per day, month or year) and list tiles, conditions, sharing, always the viewer's permissions (ADR-024) and print formats and letter heads (HTML templates with placeholders, a safe default layout, strict checks and CSP, browser print or Save as PDF, ADR-025). and workspaces (role-based home pages of headings, shortcuts and dashboards, filtered per person, ADR-026). Server-side PDF done (ADR-050). Not yet: multi-column report views |
| L9a: Personal Desk | Per-user sidebar favorites, visibility, ordering, collapsed groups, personal Home shortcuts, default workspace, role defaults and reset | **done (v1)**. Database-backed settings, permission-filtered destinations, preview and keyboard/drag ordering (ADR-089). Shared workspace/dashboard layout uses L9 builders. Not yet: private per-user widget arrangements or workspace cloning |
| L10: Data and outside world | Data Import / Export, Web Forms, Webhooks, API keys, OAuth, file manager | **done (v1)**, with OAuth provider (ADR-094). Done: public web forms (ADR-034), CSV data import with preview, row-by-row results, create and update modes (ADR-027), API keys (Bearer tokens acting as a person within their permissions, hashed, optional read-only and expiry, ADR-028), webhooks (signed JSON posts on new, change, submit, cancel and delete; queued with retries; public https addresses only with SSRF guard at connect time, ADR-029) and attachments (files on documents with per-request permissions, content-based types, only pictures inline, size and count limits, disk storage behind an interface, ADR-030). OpenID Connect sign-in done (ADR-051). Not yet: Qahwa as an OAuth provider |
| L11: Scripting escape hatch | Sandboxed server and client scripts, custom HTML blocks | **started**: no-code Document Actions (set a field, create a document; ADR-035). Server Scripts done: sandboxed JavaScript on Validate, Before/After Submit, Before/After Cancel, Before Delete and After Save with strict limits (ADR-053; Jint instead of Roslyn, which cannot be sandboxed in process). Client Scripts for forms done (ADR-057). Not yet: API, scheduler and permission-query scripts, list-view client scripts |
| L12: Platform | App/plugin packaging and fixtures export, multi-site, SQL Server, translations, more views (Kanban, Calendar, Gantt, Map, Tree) | **mostly done**: multi-site (ADR-055), SQL Server (ADR-059), translations and right-to-left (ADR-033), bundles (ADR-032), Kanban, Calendar, Gantt, Map, Gallery and Tree views (ADR-031, ADR-049), LDAP and SAML sign-in (ADR-054, ADR-058). Not yet: installable apps with hooks, developer mode |

---

## Appendix A: Frappe feature catalog

Status: ✅ done · 🟡 partial · ⬜ planned · 🔽 later/optional. "Step" refers to the roadmap in 13.2.

### A1. Data modeling (DocType)

| Feature | Notes | Status | Step |
|---|---|---|---|
| DocType (metadata-driven document model) | JSON definition, table generated | ✅ | done |
| **DocType Designer UI** | build DocTypes in the Desk: fields, naming, title field, list columns, permissions, live preview, SQL preview | ✅ | L1 (v1) |
| Standard vs custom DocTypes | app files are read-only, UI-created are editable | ✅ | L1 |
| Field types: Data, Text, Long Text, Int, Float, Currency, Percent, Check, Select, Date, Datetime, Time, Link, Email, Phone, URL, Code, Markdown, JSON | | ✅ | done |
| Field types: Small Text, Text Editor (rich text), HTML Editor, HTML, Heading, Button, Read Only, Autocomplete | Small Text, Text Editor (cleaned HTML) and Autocomplete done (ADR-069); Heading and HTML done (ADR-102); Button is made with Document Actions and client scripts (Read Only is a field property) | ✅ | L3 |
| Field types: Attach, Attach Image, Signature, Barcode, Geolocation, Icon, Color, Rating, Duration, Image | done: Signature, Barcode, Geolocation, Color, Rating, Duration, Icon (ADR-047), Attach and Image (ADR-048) | ✅ (v1) | L10 |
| Field types: Table | child rows inside a document | ✅ | L2 |
| Field types: Dynamic Link, Table MultiSelect | Dynamic Link done (ADR-038); Table MultiSelect done as a flag on Table fields (ADR-078) | ✅ | L2 |
| Field types: Password (encrypted), Phone with country | Password done (ADR-066); Phone numbers are validated (ADR-079) with a country picker (ADR-087) | ✅ | L3 |
| Layout fields: Section Break, Column Break, Tab Break | sections, tabs and (ADR-100) columns | ✅ | L1 (section/tab), L3 |
| Field properties: label, required, unique, default, options, in list view, lowercase | | ✅ | done |
| Field properties: description, hidden, read only | UI hints (not enforced by the API) | ✅ | L1 |
| Field properties: `depends_on`, `mandatory_depends_on`, `read_only_depends_on` | conditional logic by expression (read-only is a UI hint) | ✅ | L4 |
| Field properties: `fetch_from`, `fetch_if_empty` | copy a value from a linked document, only what the user may read | ✅ | L4 |
| Calculated fields (formula), expression defaults | recalculated on every save and live in the form | ✅ | L4 |
| Field properties: in standard filter, in preview, in global search, bold, no copy, allow on submit, set only once, non negative, collapsible, translatable, search index, precision, length, ignore user permissions, permission level | permission level, set only once, non-negative and maximum length enforced on the server (ADR-101); no copy, bold, in standard filter and precision also done; in preview (a preview card on links) and translatable (values shown in the person's language) done, ADR-116 | ✅ | L3 |
| Child tables (Table field, parent/child) | created in the designer, saved atomically with the parent | ✅ | L2 |
| Single DocTypes (settings stored as one document) | done (ADR-011) | ✅ (v1) | L6 |
| Tree DocTypes (nested set, parent/child hierarchy) | done: parent/child with tree views (ADR-011) | ✅ (v1) | L6 |
| Virtual DocTypes (data from outside the database) | | 🔽 | L12 |
| Submittable DocTypes (draft, submitted, cancelled, amend) | done: draft, submitted, cancelled (ADR-009) | ✅ (v1) | L6 |
| Naming: hash, field, prompt, series | | ✅ | done |
| Naming: format with date/field parts (`INV-{YYYY}-.####`), expression, naming rules, series manager | done: series, date and field parts, prompt, expression, rule tables, series manager (ADR-011, ADR-063, ADR-073) | ✅ | L6 |
| Rename document (updates links), merge documents | done: rename with link updates and merge (ADR-011) | ✅ (v1) | L6 |
| Duplicate document, track changes (versions), track seen, track views | duplicate (ADR-063), track changes (ADR-014) and track views (ADR-080) done | ✅ | L7 |
| Document options: title field, sort field/order, search fields, image field, quick entry, allow rename, allow import, allow copy, editable grid, max attachments, default view | title field, allow rename, allow import (permission flag), quick entry (ADR-091), default sort, allow copy, max attachments, search fields, default view and image field (ADR-105) done; child tables are edited in an inline grid (TableControl) | ✅ | L1, L3 |
| Customize Form (adjust standard DocTypes) | done (ADR-013) | ✅ (v1) | L3 |
| Custom Field, Property Setter | done: stored as data, survive upgrades (ADR-013) | ✅ (v1) | L3 |
| DocType Layout (alternative form layouts per role) | | 🔽 | L3 |
| Module Definition, Module Profile | group DocTypes on the home page with label, order and hide flag (ADR-099); restrict access to modules with Module Profiles (ADR-081) | ✅ | L5 |

### A2. Document lifecycle and logic

| Feature | Notes | Status | Step |
|---|---|---|---|
| Required, unique, select option, link existence, length, type validation | | ✅ | done |
| Optimistic concurrency ("modified by someone else") | | ✅ | done |
| Lifecycle events: before_insert, validate, before_save, after_insert, on_update, on_trash, after_delete | event bus plus code hooks | 🟡 no-code Document Actions on New, Update, ValueChange, Submit, Cancel; no code hooks | L11 (code), L4 (rules) |
| Submit events: before_submit, on_submit, before_cancel, on_cancel, on_update_after_submit | done as Server Script events (ADR-053) | ✅ | L6 |
| Validation rules without code | expression language, per document and per table row | ✅ | L4 |
| Server Script (DocType event, API, scheduler event, permission query) | sandboxed DocType-event scripts, API scripts and scheduled scripts (ADR-108); permission-query scripts (use permission rules) left out on purpose | ✅ | L11 |
| Client Script (form/list events, field change) | done for forms (ADR-057) and lists: buttons, notes, row marks, filters (ADR-083) | ✅ | L11 |
| Hooks per app (doc events, scheduler events, fixtures, boot) | done (ADR-062, ADR-084) | ✅ | L12 |
| Versions: updates log on every document (field-level history, who/when, restore deleted) | on by default for all DocTypes, same transaction; restore of deleted documents done (ADR-067) | ✅ | L7 |
| Comments on every document with @mentions, merged timeline | mentions highlighted, not yet notified | ✅ | L7 |
| Assign (ToDo), Assignment Rules (round robin, load balancing, by field) | assign done (L7); rules done: round robin, load balancing, by field, conditions, withdraw condition; not yet working hours or leave | ✅ (v1) | L7, L8 |
| Share (DocShare), Follow (Document Follow), Likes, Tags | share (ADR-008), follow (ADR-015), tags and likes (ADR-068) done | ✅ | L7 |
| Attachments, File manager (folders, private/public, thumbnails) | done: attachments on documents (private, permission-checked, picture thumbnails); not yet folders, public files, a File Manager page | ✅ (v1) | L10 |
| Deleted Documents (restore), View Log, Access Log, Activity Log | Deleted Documents (ADR-067), View Log (ADR-080) and Access Log (ADR-081) done | ✅ | L7 |
| Auto Repeat (recurring documents) | daily, weekly, monthly, quarterly, yearly; date fields; pause, run now, end date | ✅ (v1) | L8 |
| Reminders, Events (calendar), Milestones | | 🔽 | L8 |

### A3. Workflow, automation and messaging

| Feature | Notes | Status | Step |
|---|---|---|---|
| Workflow (states, transitions, roles, conditions) | visual **Workflow Builder**; done: states, transitions, roles, conditions, self-approval, edit-by-state, docstatus, approvals by several people (ADR-118), escalation (ADR-119), approve/reject by email link (ADR-120) | ✅ (v1) | L8 |
| Workflow Actions inbox, approve/reject by email link | inbox on the home page done; approve/reject by email link done (ADR-120) | ✅ | L8 |
| Notification rules (on new, save, submit, value change, days before/after, custom condition) | done for the in-app channel; email, Slack and SMS channels not yet | ✅ (v1) | L8 |
| Email Template, Email Account (SMTP out, IMAP/POP in), Email Queue, Email Domain | Email Template, Email Queue and a site Email Account for SMTP out (encrypted password, applies without restart, test email) done (ADR-090) and incoming mail over IMAP (TLS only, read-only, polled, kept as Communication; ADR-095); POP, OAuth SMTP and several accounts not planned | ✅ | L8 |
| Communication (email threads on documents), send email from a document | send from a document done; received mail is kept as Communication documents (ADR-095); threads on documents not yet | 🔽 | L8 |
| Notification Log (bell icon), Email Digest | bell and log done (ADR-015); Email Digest done (ADR-072) | ✅ | L8 |
| Scheduled Job Types (cron, hourly, daily, weekly, monthly), Scheduler UI and logs | background scheduler loop exists (auto repeat); job types, UI and logs not yet | 🔽 | L8 |
| Background jobs and queues (short, default, long), job UI | scheduler steps and app jobs are timed and written to the Scheduled Job Log (ADR-075); persistent background queue with short, default and long queues, retries with backoff, crash recovery and a Background Job list (ADR-117) | ✅ | L8 |
| Webhooks (outgoing, conditions, signed) | done: events, conditions, signing, retries, SSRF-safe; not yet custom headers, test button | ✅ (v1) | L10 |
| Auto Email Report | done: scheduled plain-text emails of a saved report, run as a chosen user (ADR-071) | ✅ | L9 |
| Document Naming Rule | done: rules with priority and conditions, naming by expression (ADR-073) | ✅ | L6 |
| Energy Points and gamification | | 🔽 | later |

### A4. Permissions and security

| Feature | Notes | Status | Step |
|---|---|---|---|
| Roles, role permissions (read, write, create, delete) | | ✅ | done |
| Permissions: if owner | | ✅ | done |
| Permissions: submit, cancel, amend, report, export, import, share, print, email, select | submit, cancel, report, export, import, share, print, email done (ADR-082); amend done (ADR-103); "select" is covered by read access | ✅ | L5 |
| Permission levels (field-level access) | server-enforced, set per field and per rule | ✅ | L5 |
| **Role Permission Manager** UI, Custom DocPerm | customise, reset to default, apply at once | ✅ | L5 |
| User Permissions (restrict by Link value, apply to all or some DocTypes) | `User Permission` DocType, enforced on the server | ✅ | L5 |
| Role Profile, Module Profile, User Types | profiles done; user types (System User, Website User) done, ADR-096 | ✅ | L5 |
| Permission query and `has_permission` hooks | done: app checks and list conditions that can only narrow access, fail closed (ADR-074) | ✅ | L11 |
| Password hashing, lockout, rate limit, change password | | ✅ | done |
| Password policy (strength, expiry), password reset by email, email verification, sign-up | done: strength rules, reset by email and expiry (ADR-041), self sign-up with email verification (ADR-052); System Managers can set a password or send a reset link, audited (ADR-090) | ✅ | L10 |
| Two-factor authentication (TOTP, email, SMS) | done: TOTP with recovery codes and admin reset (ADR-039); not email/SMS/passkeys | ✅ (v1) | L10 |
| API keys / secrets, token auth | done: per-person Bearer keys, hashed, read-only and expiry; not yet scopes, rate limits, OAuth | ✅ (v1) | L10 |
| OAuth 2 provider, social login (Google, GitHub, Microsoft, Keycloak), OpenID Connect | done: OpenID Connect sign-in (code + PKCE, state, nonce, full token checks, identity linking, domain allow-list, auto-create) with any provider; Qahwa is also an OAuth 2.0 provider for other programs (code + PKCE, rotating refresh tokens, revocation, user info; ADR-094); optional group-to-role map in configuration that can never name Administrator or System Manager (ADR-113); without it roles never come from the token | ✅ | L10 |
| LDAP, SAML single sign-on | done: LDAP / Active Directory sign-in (ADR-054) and SAML 2.0 sign-in (ADR-058), both tested against stand-in providers | ✅ (v1) | L12 |
| Session management (list/revoke, expiry), login as user (impersonation) | done: list, revoke, admin sign-out-everywhere, absolute limit (ADR-040); impersonation deliberately not built | ✅ (v1) | L10 |
| IP restrictions, system settings (security) | done: IP allow-list in configuration, System Settings (display) (ADR-042); not per-role IP rules | ✅ (v1) | L10 |
| Encrypted fields (Password type) | done: encrypted at rest, masked on read, never listed (ADR-066) | ✅ | L3 |
| CSRF, security headers | | ✅ | done |

### A5. Desk UI

| Feature | Notes | Status | Step |
|---|---|---|---|
| Login, sidebar, home | | ✅ | done |
| List view: search, filters, sort, paging | | ✅ | done |
| List view: saved filters, user settings (columns, sort), list view settings | done (ADR-016) | ✅ (v1) | L7 |
| List view: sidebar group-by counts, row indicators/status colors, tags, assigned, like, comment count | group-by counts, status indicators and comment counts done (ADR-017); tag and like filters (ADR-068) | ✅ | L7 |
| List view: bulk actions (edit, delete, assign, tag, export, print) | done: edit, delete, rename, assign (ADR-016), tag (ADR-098); export uses the list filters (CSV, Excel) and printing is per document | ✅ | L7 |
| Form view: field controls per type, validation, conflict warning, delete | | ✅ | done |
| Form view: tabs/sections/columns layout, collapsible sections | tabs, sections, up to four columns (ADR-100), collapsible sections | ✅ | L1, L3 |
| Form view: child table grid (editable, reorder, row form) | | ✅ | L2 |
| Form view: child table grid, upload/download rows as CSV | done in the browser: download and upload (adds rows), formula-safe, checked by the server on save (ADR-076) | ✅ | L10 |
| Form view: sidebar (assign, attachments, tags, share, follow, like), timeline, form dashboard with linked documents | done (ADR-019, ADR-068) | ✅ | L7 |
| Form: quick entry dialog, auto-save indicator, keyboard shortcuts, unsaved-changes warning | quick entry: create a linked document in place from any Link field (ADR-091); unsaved changes are kept in the browser and offered back (ADR-109); shortcuts (ADR-110); saving to the server without a click left out on purpose (ADR-110) | ✅ | L7 |
| Report view (editable columns, group by, totals, export) | Report Builder with group by, totals and CSV export (ADR-045); each person chooses and orders the list columns (saved per person); export to CSV and Excel | ✅ | L9 |
| Kanban, Calendar, Gantt, Map, Image, Tree, Dashboard views | done: Tree, Dashboard, Board, Calendar, **Gantt** (timeline), **Map** (plain grid) and **Image** (gallery) (ADR-031, ADR-049) | ✅ (v1) | L12 |
| Workspaces (shortcuts, charts, number cards, onboarding) and **Workspace Builder** | done: headings, shortcut rows, embedded dashboards, role-based; not yet onboarding | ✅ (v1) | L9 |
| Awesomebar (global search, create, commands), global search index | done: Ctrl+K search box with pages, DocTypes, "new …" and permission-checked document search by name, title and each DocType's search fields (ADR-061, ADR-105); no database full-text index, deliberately (ADR-107) | ✅ | L9 |
| Notification bell, toasts, dialogs | done (ADR-015) | ✅ (v1) | L8 |
| Themes (light, dark, automatic), RTL layout | RTL done (logical CSS, dir follows the language); light, dark and automatic themes with organisation brand default done | ✅ | L12 |
| Translations (Translation DocType, language switch) | done: Translation DocType, catalog API, language switch, built-in Arabic chrome; not yet every message, plurals, per-user language | ✅ (v1) | L12 |
| User settings: language, time zone, date/number/currency formats | language and time zone/date formats done (ADR-033, ADR-042); light/dark/auto theme per person and organisation brand done (ADR-056); number and currency format is a site setting (ADR-106) | ✅ | L7 |
| Page Builder / Custom HTML Block | | 🔽 | L11 |

### A6. Reports, charts and printing

| Feature | Notes | Status | Step |
|---|---|---|---|
| **Report Builder** (pick fields, filters, group by, sort, save, share) | done: lists and multi-number summaries, run with the viewer's rights, CSV (ADR-045); not joins, calculated columns, PDF | ✅ (v1) | L9 |
| Query Report (SQL), Script Report | Script Reports written by apps done (ADR-088); a SQL box in the Desk is deliberately not offered (it bypasses permissions) | ✅ (by design) | L11 |
| Dashboard Charts (count, sum, average, group by, time series), Number Cards, Dashboards | done: number, chart and list tiles, per day/month/year, filters, private or shared | ✅ (v1) | L9 |
| Prepared Reports (background) | | 🔽 | L9 |
| Export to CSV / Excel / PDF | done: CSV and Excel (lists, reports, ADR-064), PDF of printouts (ADR-050) | ✅ (v1) | L9 |
| **Print Format Builder** (drag and drop), Print Settings, Letter Head, custom HTML/Jinja-style formats | done: HTML templates with placeholders, if/each, default layout, letter heads, preview; not yet drag and drop, print settings, server PDF | ✅ (v1) | L9 |
| PDF generation | done: headless Chrome, Chromium or Edge, inert page, no network, strict limits (ADR-050) | ✅ (v1) | L9 |

### A7. API and integration

| Feature | Notes | Status | Step |
|---|---|---|---|
| REST resource API (list, get, create, update, delete) with filters, fields, order, paging | | ✅ | done |
| Filter operators: `=`, `!=`, `<`, `>`, `<=`, `>=`, `like`, `in`, `is set` | | ✅ | done |
| Filter operators: `not like`, `not in`, `between`, timespans, dynamic values (`__user`) | done (ADR-060) | ✅ (v1) | L7 |
| `/api/method` (whitelisted server functions), `get_count`, `get_value`, `set_value`, `get_list`, `rename_doc` | done (ADR-062) | ✅ (v1) | L7 |
| API versioning, OpenAPI documentation | done: OpenAPI 3 per caller; URL versioning `/api/v1/` with a version header (ADR-097) | ✅ | L12 |
| Webhooks, incoming API via server script | outgoing webhooks done (L10); incoming API via scripts not yet (L11) | 🔽 | L10, L11 |
| Realtime updates (socket events, document updates), SignalR | done with Server-Sent Events instead of SignalR: live bell and live form updates (ADR-070); database backplane for several servers (ADR-086) | ✅ | L8 |
| Payment gateways (Stripe, PayPal, Razorpay), SMS gateways, Google Calendar/Contacts/Drive, Dropbox, S3 backups, Slack, push notifications | | 🔽 | L12 |
| Event streaming / data sync between sites | | 🔽 | later |

### A8. Data tools and operations

| Feature | Notes | Status | Step |
|---|---|---|---|
| Schema migration (additive), dry run | | ✅ | done |
| Patches / data migrations (patch log) | done (ADR-043); not per-app ordering or a CLI to run one | ✅ (v1) | L12 |
| **Data Import** (CSV/Excel template, validate, preview, background, update existing) | done: CSV, template, preview, row results, update existing; not yet Excel, background, child tables | ✅ (v1) | L10 |
| Data Export, bulk update, bulk rename | export to CSV and Excel (ADR-064), import from CSV and Excel (ADR-027, ADR-064), bulk edit (ADR-016) and bulk rename (ADR-065) done | ✅ | L10 |
| Backups (download, scheduled, restore) | done: download, daily folder, additive restore (ADR-044); not encryption, file contents, cloud storage | ✅ (v1) | L12 |
| Error Log, Scheduled Job Log, Recorder (query profiler), System Console | done: Error Log (requests and scheduler), Scheduled Job Log (ADR-075); slow-request recorder with database statement counts and time (ADR-112); no statement-text profiler or console, by design (ADR-107) | ✅ | L12 |
| System Settings (time zone, formats, session expiry, security) | done: time zone, date format, first weekday, default language; session length, password length and password age can be made stricter (never looser) in System Settings (ADR-114); lockout, hashing and the network allow-list stay in configuration (ADR-107) | ✅ | L10 |
| Fixtures (export UI-made customisations as files) | done: **bundle** export/import of DocTypes, customizations, workflows and rules with preview; not yet print formats/dashboards, patches, CLI | ✅ (v1) | L12 |

### A9. Platform and developer experience

| Feature | Notes | Status | Step |
|---|---|---|---|
| CLI: migrate, create-admin | | ✅ | done |
| CLI: init, new-site, new-app, install-app, backup, restore, console, run-tests, set-config | `migrate`, `create-admin`, `new-site`, `list-sites` done (ADR-055); `list-apps`, `install-app`, `uninstall-app` done (ADR-093); `backup` and `restore` done (ADR-104); init is `new-site`, and set-config, console, run-tests and new-app are left out on purpose (ADR-104) | ✅ | L12 |
| Apps (installable packages with DocTypes, hooks, fixtures, patches) | done: `IQahwaApp`, per-site install record, fixtures, patches (ADR-062), dependencies (ADR-085), uninstall/install per site and `list-apps`/`install-app`/`uninstall-app` on the command line (ADR-093) | ✅ | L12 |
| SQL Server dialect | done: second database product, per site, same tests on both (ADR-059) | ✅ (v1) | L12 |
| Multi-tenancy (sites, per-site config, database per site) | done: Host-header sites, a container and database per site, per-site config files, `qahwa new-site` (ADR-055); not live add/remove, no site manager UI | ✅ (v1) | L12 |
| Developer mode (UI-made DocTypes written to the app's files) | done (ADR-062) | ✅ (v1) | L12 |
| Caching, boot info, performance | boot info in one call (`BootService`); DocType definitions held in memory and refreshed when they change; script and action lists cached for 15 seconds on each server; API responses are never cached (they hold private data, `Cache-Control: no-store`) | ✅ | L12 |
| Test framework (unit, integration, UI) | unit and integration done: xUnit with in-memory stores (Core, Api through a full test server), tests against real PostgreSQL and SQL Server (Data, need a database), vitest for the Desk, shared expression vectors checked by both C# and TypeScript; browser end-to-end tests with Playwright in `e2e/` (ADR-115), run against a throwaway site | ✅ | ongoing |

### A10. Standard documents shipped with the framework

| Document | Status | Step |
|---|---|---|
| User, Role, Has Role | ✅ | done |
| DocType, Custom Field, Property Setter, Module Def | ✅ (DocType designer, Customize Form for custom fields and property changes, Module Definition ADR-099) | L1, L3 |
| Comment, Communication, ToDo, DocShare, Document Follow, Tag, Version, File, Note | ✅ all built, including Note (private to its owner, shareable) | L7, L10 |
| Notification, Email Template, Email Account, Assignment Rule, Auto Repeat, Workflow, Webhook | ✅ (incoming mail added, ADR-095) | L8, L10 |
| Report, Dashboard, Dashboard Chart, Number Card, Workspace, Print Format, Letter Head | Dashboard, Workspace, Print Format and Letter Head done (charts and number cards are tiles of a Dashboard) | ✅ (v1) | L9 |
| Data Import, Data Export, Scheduled Job Type, Error Log, System Settings, Navbar Settings | ✅ import and export (files), app jobs, Error Log, System Settings, Desk Setting for the sidebar | L10, L12 |
| Contact, Address, Country, Currency, Language, Salutation, Gender (master data) | done: Country, Currency, Language, Salutation, Gender (ADR-046), Contact and Address with dynamic links (ADR-077) | ✅ | L10 |
| Web Form, Web Page, Blog, Website Settings (portal and website) | 🔽 | later |
