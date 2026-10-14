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
| Auth | **ASP.NET Core Identity + JWT / cookie sessions + API key/secret** | Standard |
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
- **Versioning**: `Version` DocType stores field-level diffs when `trackChanges` is true
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

### 5.10 Cross-cutting features (Phase 3+)

Workflow engine, assignment and ToDo, comments and mentions, tags, attachments (local/S3/Azure Blob), email (queue plus templates), notifications (in-app/email/webhook), print formats (Razor/Scriban to PDF), data import/export (CSV/Excel with validation), webhooks, audit log, scheduled jobs, localization (i18n with Arabic/RTL support, which is a first-class goal), and custom reports.

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
7. Auth (Identity, login/logout), `User` and `Role` DocTypes, basic role permissions
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
- Desk v1: child-table grid, link autocomplete, filters, bulk actions, form layout (sections/columns/tabs)

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
