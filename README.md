# Qahwa

Metadata-driven full-stack framework for .NET (inspired by Frappe). Define a DocType in JSON (or in the Desk) and get the table, the API, permissions and the UI.

Documentation: [developer manual and user guide](docs/README.md) · [roadmap and plan](docs/PLAN.md) · [design decisions](docs/adr).

## What works today

- DocType JSON → validated metadata (`Qahwa.Core`)
- Schema sync: creates tables, adds missing columns and indexes, reports type drift without touching data (`Qahwa.Data`)
- Document engine: naming (hash / field / prompt / series), defaults, validation, link checks, optimistic concurrency, role and owner permissions
- Generic REST API with Frappe-style filters: `/api/resource/{doctype}`, `/api/meta`
- Authentication: `User` / `Role` / `Has Role` DocTypes, cookie login, lockout, rate limiting, change password ([ADR-002](docs/adr/002-authentication.md))
- PostgreSQL provider, `qahwa migrate` and `qahwa create-admin` CLI

- **DocType Designer** (Tools → DocType Designer, for System Managers): create and edit your own DocTypes in the Desk with a live form preview, a preview of the database changes, field properties, naming rules and a permissions table. New DocTypes work immediately, no restart ([ADR-003](docs/adr/003-doctype-designer.md))
- **Child tables**: mark a DocType as a child table and add it to another DocType as a Table field (for example the items of an order). The form shows an editable grid; rows save atomically with the document ([ADR-004](docs/adr/004-child-tables.md))
- **Field logic without code**: show/require/read-only conditions, calculated fields, `=today()`-style defaults, fetch-from-a-link, and document and row validation rules, all written in a small safe [expression language](docs/expressions.md) that runs identically on the server and in the form ([ADR-005](docs/adr/005-expression-language.md))
- **Role Permission Manager** (Tools → Role Permissions) and **permission levels**: change who can read, write, create and delete any DocType without editing it, and hide fields from roles with no rule at the field's level; enforced on the server and reset-able ([ADR-006](docs/adr/006-permission-levels.md))
- **User Permissions**: restrict a person to some documents (for example only the North territory) with a `User Permission` document; lists, reads, writes and links all respect it ([ADR-007](docs/adr/007-user-permissions.md))
- **Sharing**: share one document with one person (read or edit) from the form, without changing anyone's role ([ADR-008](docs/adr/008-sharing.md))
- **Submittable documents**: draft, submitted and cancelled states with submit and cancel rights; submitted documents are locked except fields marked "allow on submit" ([ADR-009](docs/adr/009-submittable-documents.md))
- **Role and Module Profiles, export and report rights**: hand out roles or hide modules by profile; export CSV and grouped reports as separate rights ([ADR-010](docs/adr/010-profiles-export-report.md))
- **Single and tree DocTypes, naming series manager, rename and merge**: settings pages, hierarchies with a tree view, series counters you can set, and renaming that updates every link ([ADR-011](docs/adr/011-special-doctypes.md))
- **Searchable Link fields**: every Link field (and the Share panel) is a type-ahead select that shows friendly names, not IDs ([ADR-012](docs/adr/012-link-fields.md))
- **Customize Form** (Tools → Customize Form): add fields to framework and app DocTypes, change labels, required, hidden, defaults and conditions of existing fields, and reorder them, with a reset that restores the original ([ADR-013](docs/adr/013-customize-form.md))
- **Updates log and comments on every document**: who changed what and when (field-level, in the same transaction as the change), and a comment thread with @mentions, in an Activity panel under every form ([ADR-014](docs/adr/014-updates-log-and-comments.md))
- **Assign, follow and notifications**: assign a document to a person, follow what you care about, and get in-app notifications (a bell) for mentions, assignments, comments, changes and shares, only for documents you may open ([ADR-015](docs/adr/015-assign-follow-notifications.md))
- **Your own lists**: choose columns, rows per page and sort per person, save and share filters, and tick rows to set a field or delete many documents at once (each checked on its own) ([ADR-016](docs/adr/016-list-settings-saved-filters-bulk.md))
- **API keys**: Bearer tokens that act as a person within their permissions, shown once and stored hashed, with read-only and expiry options ([ADR-028](docs/adr/028-api-keys.md))
- **Data import**: CSV files become documents through the normal insert or update rules, with a preview, row-by-row results and an update mode ([ADR-027](docs/adr/027-data-import.md))
- **Workspaces**: role-based home pages of headings, shortcuts and dashboards, filtered for each person ([ADR-026](docs/adr/026-workspaces.md))
- **Printing**: print formats (HTML with placeholders, a safe default layout) and letter heads, printed or saved as PDF from the browser, with strict template checks and a locked-down page policy ([ADR-025](docs/adr/025-print-formats-and-letter-heads.md))
- **Dashboards**: number, chart (bars, line, pie, per day, month or year) and list tiles with conditions, private or shared, always shown with the viewer's own permissions ([ADR-024](docs/adr/024-dashboards.md))
- **Email**: a queue sent by the scheduler (retries, no double sends across servers), templates, emailing about a document, and an email option on notification rules; plain text, SMTP or log-only ([ADR-023](docs/adr/023-email-queue-and-templates.md))
- **Notification rules**: tell people when a document is created, changed, a field changes, it is submitted or cancelled, or a date is near, with conditions and message placeholders, only to people who may read it ([ADR-022](docs/adr/022-notification-rules.md))
- **Auto Repeat**: documents that copy themselves daily to yearly as the person who set them up, run by a background scheduler that is safe on several servers ([ADR-021](docs/adr/021-auto-repeat.md))
- **Assignment rules**: give new documents to people automatically (round robin, load balancing or from a field, with conditions and a withdraw condition) ([ADR-020](docs/adr/020-assignment-rules.md))
- **Document form**: sticky action bar with icon buttons and tooltips, Assign/Share/Rename and confirmations in accessible dialogs, a side rail (assignees, shared with, details), Ctrl+S ([ADR-019](docs/adr/019-document-form-ux.md))
- **Workflows**: a Workflow Builder (states, transitions guarded by roles and conditions, self-approval rule, edit-by-state), action buttons on the form, submit/cancel through the workflow and a "Waiting for my action" inbox ([ADR-018](docs/adr/018-workflow-engine.md))
- **List sidebar, status colours and comment counts**: a Group by panel with counts per value (click to filter), coloured status labels per DocType, and a comment count on each row ([ADR-017](docs/adr/017-list-sidebar-status-comment-counts.md))
- Desk (React + TypeScript): login, DocType sidebar, generic list view (search, dynamic filters kept in the URL, sorting, paging) and form view (field controls per type, server-side validation errors inline, concurrent-edit detection, delete), account page with change password. Layout is right-to-left ready and works on phones.

Not yet (see the [plan](docs/PLAN.md)): email notifications, saved filters and per-user list settings, attachments, workflow, reports and dashboards, import, scripting.

## Try it

Requires the .NET 10 SDK and Docker.

```bash
docker compose up -d postgres
dotnet run --project src/Qahwa.Cli -- migrate --connection "Host=localhost;Database=qahwa;Username=qahwa;Password=qahwa_dev_only" --doctypes samples/doctypes
dotnet run --project src/Qahwa.Cli -- create-admin --connection "Host=localhost;Database=qahwa;Username=qahwa;Password=qahwa_dev_only" --email you@example.com
dotnet run --project src/Qahwa.Server        # Development: auto-migrates on start
```

`create-admin` prompts for the password (10+ characters). The server listens on http://localhost:5101.

### The Desk

Build it once and the server serves it at http://localhost:5101 (sign in with the admin you just created):

```bash
cd src/Qahwa.Desk && npm install && npm run build
```

For Desk development run `npm run dev` instead (http://localhost:5173, proxies `/api` to the server) and keep the server running.

### The API

```bash
curl -c jar.txt -X POST http://localhost:5101/api/auth/login \
  -H "Content-Type: application/json" -d '{"email":"you@example.com","password":"<your password>"}'

curl -b jar.txt -X POST http://localhost:5101/api/resource/Customer \
  -H "Content-Type: application/json" -d '{"customer_name":"Acme"}'
```

### Deployment notes

- Session cookies are encrypted with ASP.NET Data Protection keys. In containers or multiple instances, persist and share the key ring (`AddDataProtection().PersistKeysTo...`), or every restart signs everyone out.
- Behind a reverse proxy configure forwarded headers, otherwise login rate limiting sees only the proxy's address.
- Serve over HTTPS: outside Development the session cookie is marked `Secure`.

Auth endpoints: `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`, `POST /api/auth/change-password`.

## Tests

```bash
dotnet test      # PostgreSQL integration tests use Testcontainers and skip themselves if Docker is not running
```
