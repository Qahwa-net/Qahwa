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
- **Your own lists**: choose columns, rows per page and sort per person, save and share filters, and tick rows to set a field, rename or delete many documents at once (each checked on its own) ([ADR-016](docs/adr/016-list-settings-saved-filters-bulk.md))
- **Attachments**: files on documents, checked against the document on every request, typed by content (only pictures inline), limited in size and number ([ADR-030](docs/adr/030-attachments.md))
- **Board and calendar views**: any DocType with a choice or date field can be shown as a kanban board (drag cards to change the value, keyboard-friendly) or a month calendar ([ADR-031](docs/adr/031-board-and-calendar-views.md))
- **Export & import bundles**: what was built in the Desk (DocTypes, customizations, workflows, rules) as one JSON file, previewed before it is applied and validated item by item like hand-typed input ([ADR-032](docs/adr/032-app-bundles.md))
- **Translations and right-to-left**: translations are ordinary documents; pick a language in the top bar and the Desk mirrors for Arabic, Hebrew and the like ([ADR-033](docs/adr/033-translations-and-rtl.md))
- **Web forms**: public intake pages that create documents without an account, with a field whitelist, no session, size limits, rate limits and a bot trap ([ADR-034](docs/adr/034-web-forms.md))
- **Document actions**: no-code automation (set a field, create a document) when documents are created, changed, submitted or cancelled; runs with the rights of the person who made the change and cannot loop ([ADR-035](docs/adr/035-document-actions.md))
- **Error log**: unexpected errors (requests and the scheduler) recorded for System Managers, without bodies or query strings and with a rate limit ([ADR-036](docs/adr/036-error-log.md))
- **OpenAPI**: `/api/openapi.json` describes the REST API for exactly what the caller may use, generated from the live DocTypes ([ADR-037](docs/adr/037-openapi.md))
- **Two-factor sign-in**: authenticator-app codes with recovery codes, replay protection, shared lockout, encrypted secrets ([ADR-039](docs/adr/039-two-factor-sign-in.md))
- **Sessions**: every sign-in is recorded; see where you are signed in, end any of them, absolute 14-day limit ([ADR-040](docs/adr/040-sessions.md))
- **Password reset and rules**: emailed one-hour single-use link (nothing stored, no account enumeration, configured site address), common-password and pattern checks ([ADR-041](docs/adr/041-password-reset-and-rules.md))
- **System Settings and network restrictions**: time zone, date format, first weekday and default language for the site; an IP allow-list in configuration that fails closed and loud ([ADR-042](docs/adr/042-system-settings-and-network-restrictions.md))
- **Patches**: one-time versioned data migrations, recorded in a Patch Log, claimed atomically across servers, a failure stops start-up ([ADR-043](docs/adr/043-patches.md))
- **Backups**: a zip of documents and definitions (no passwords or secrets) made from the Desk or daily to a folder; restoring only adds, with a preview and zip-bomb limits ([ADR-044](docs/adr/044-backups.md))
- **Report Builder**: saved, shareable list and summary reports (several numbers per group) that always run with the viewer's own permissions, with CSV download ([ADR-045](docs/adr/045-report-builder.md))
- **Master data**: countries, currencies, languages, salutations and genders seeded by a patch; the automatic `All` role for what every signed-in person may read ([ADR-046](docs/adr/046-master-data.md))
- **More field types**: rating, duration, colour, barcode, place and signature, validated on the server and usable in the Designer ([ADR-047](docs/adr/047-more-field-types.md))
- **Attachment and image fields**: a form field that names a file attached to the same document, validated on save, with picture preview ([ADR-048](docs/adr/048-attach-and-image-fields.md))
- **Gallery, map and timeline views**: pictures as tiles, places on a plain grid (no outside map service), documents as bars between two dates ([ADR-049](docs/adr/049-gallery-map-and-timeline-views.md))
- **Light, dark and automatic themes and a brand**: per-person theme, organisation name, logo, colours and default theme, all checked as plain data, readable colours guaranteed ([ADR-056](docs/adr/056-brand-and-theme.md))
- **App permission hooks**: apps can add per-document checks and list conditions that only ever take access away ([ADR-074](docs/adr/074-app-permission-hooks.md))
- **Naming rules and naming by expression**: names built from fields, and rule tables that choose the series by condition ([ADR-073](docs/adr/073-naming-rules-and-expressions.md))
- **Email digest**: an opt-in daily or weekly email to yourself with your unread notifications and open assignments ([ADR-072](docs/adr/072-email-digest.md))
- **Emailed reports**: a saved report is emailed on a schedule, run with the permissions of a chosen user, set up by System Managers only ([ADR-071](docs/adr/071-auto-email-reports.md))
- **Live updates**: the bell and open forms update by themselves through a permission-checked event stream (Server-Sent Events) that carries only where something changed ([ADR-070](docs/adr/070-live-updates-server-sent-events.md))
- **Text Editor, Small Text and Autocomplete fields**: formatted text stored as HTML rebuilt from an allow-list (no script, style or event attribute can survive) ([ADR-069](docs/adr/069-text-editor-small-text-autocomplete.md))
- **Tags and likes**: label documents, like them, and filter lists by tag or "liked by me" ([ADR-068](docs/adr/068-tags-and-likes.md))
- **Restore deleted documents**: a copy of every deleted document is kept for 30 days and a System Manager can put it back, with its table rows ([ADR-067](docs/adr/067-deleted-documents.md))
- **Password fields**: secrets encrypted at rest, shown only as a mask, never listed or exported, readable only by server code ([ADR-066](docs/adr/066-password-fields.md))
- **Excel import and export**: a small, hostile-file-safe .xlsx writer and reader (size limits that count real bytes, no DTDs, no formulas run), the same import path as CSV ([ADR-064](docs/adr/064-excel-import-and-export.md))
- **Naming formats with dates and fields, and Duplicate**: `INV-{YYYY}-.####`, `{branch}-.###`, field values cleaned to safe characters; a Duplicate button that starts a new form from a document ([ADR-063](docs/adr/063-naming-formats-and-duplicate.md))
- **Apps, hooks, server functions and developer mode**: installable apps with document hooks, `/api/method` functions, scheduled jobs, fixtures and patches; Designer work written to files ([ADR-062](docs/adr/062-apps-hooks-methods-developer-mode.md), [writing an app](docs/manual/developer/08-writing-an-app.md))
- **The search box (Ctrl+K)**: pages, DocTypes, "new …" commands and documents, found through the same permission-checked reads as a list, text searched literally, limited per person ([ADR-061](docs/adr/061-global-search.md))
- **More filter operators**: not like, not in, between, named timespans and `__user`/`__today`, expanded before the query is built and bound as parameters ([ADR-060](docs/adr/060-more-filter-operators.md))
- **Several sites in one server**: a database and a service container per site, chosen by the Host header, strict matching, shared databases refused, own files, backups and links, `qahwa new-site` ([ADR-055](docs/adr/055-multi-site.md))
- **Company directory sign-in (LDAP / Active Directory)**: search with a read-only account, bind as the found entry, escaped filters, never an empty password, TLS required, directory is the authority for linked people ([ADR-054](docs/adr/054-ldap-sign-in.md))
- **SQL Server** as a second database (per site): one dialect, the same tests on both products, unique indexes that ignore empty values, locked gapless series, exact microsecond moments, a clear limit on long filter lists ([ADR-059](docs/adr/059-sql-server.md))
- **SAML 2.0 sign-in**: SP-initiated, signature checked over exactly the element that is read (no wrapping, unique ids, strong algorithms, configured certificates only), audience, recipient, request binding and replay checks, XML read without DTDs ([ADR-058](docs/adr/058-saml-sign-in.md))
- **Client Scripts**: JavaScript on forms run in a sandboxed worker with no network, no storage and no page, its own content-security policy, operations re-checked by the page, loops stopped after a second ([ADR-057](docs/adr/057-client-scripts.md))
- **Server Scripts**: sandboxed JavaScript (Jint) on Validate and After Save, running as the person who saved, with limits on time, work, memory and calls, and no way out to the machine ([ADR-053](docs/adr/053-server-scripts.md))
- **Self sign-up with email verification**: nothing stored until the link is used, one-time links, same answer for new and existing addresses, limited mailing, allowed domains ([ADR-052](docs/adr/052-sign-up-with-email-verification.md))
- **Single sign-on with OpenID Connect**: authorization code with PKCE, state and nonce, fully checked ID tokens, accounts linked by the provider's id and verified email, allowed domains, no open redirects ([ADR-051](docs/adr/051-openid-connect-sign-in.md))
- **PDF files of printouts**: made by a headless Chrome, Chromium or Edge on the server, with the page made inert, no network and strict limits ([ADR-050](docs/adr/050-server-side-pdf.md))
- **Webhooks**: signed JSON posts to other systems when documents are created, changed, submitted, cancelled or deleted, queued with retries and guarded against requests to internal networks ([ADR-029](docs/adr/029-webhooks.md))
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
