# REST API reference

Base path `/api`. JSON in and out. Authentication is a cookie session from `POST /api/auth/login`. Successful responses wrap the payload: `{ "data": ... }` (lists add `total`). Names in URLs must be URL-encoded.

## Errors

```json
{ "code": "validation_error", "message": "Validation failed.", "details": [ { "field": "customer_name", "message": "This field is required." } ] }
```

| Status | `code` | Meaning |
|---|---|---|
| 400 | `validation_error`, `bad_request` | Invalid input; `details` lists the fields (child rows as `items[0].qty`) |
| 401 | `unauthenticated` | Not signed in |
| 403 | `permission_denied` | Signed in but not allowed (or the module is blocked) |
| 404 | `not_found` | No such DocType or document (or you may not know it exists) |
| 409 | `conflict` | Someone else changed it, or a unique value/name is taken |

## API keys ([ADR-028](../../adr/028-api-keys.md))

Instead of the cookie, a program sends `Authorization: Bearer qk_<id>.<secret>`. The request then acts as the key's owner with their current permissions. A bad, expired or revoked key is simply nobody (401); a read-only key answers 403 to anything but GET, HEAD and OPTIONS.

| Call | Purpose |
|---|---|
| `GET /api-keys?all=true` | The caller's keys (all keys for a System Manager with `all=true`); never includes a secret |
| `POST /api-keys` `{ description?, readOnly?, expiresOn? }` | Make a key for yourself (201): `{ info, token }`, the token only here. Needs a signed-in person, not a key |
| `DELETE /api-keys/{id}` | Revoke (own key, or any for a System Manager). Needs a signed-in person |

## Authentication

| Call | Purpose |
|---|---|
| `POST /auth/login` `{ email, password }` | Sign in (rate limited, lockout after repeated failures) |
| `POST /auth/logout` | Sign out |
| `GET /auth/me` | The signed-in user, name and roles |
| `POST /auth/change-password` `{ oldPassword, newPassword }` | Change password |

## Metadata

| Call | Purpose |
|---|---|
| `GET /meta` | DocTypes the user may read (name, module, titleField), minus blocked modules |
| `GET /meta/{doctype}` | `{ data: meta, permissions, children, writableLevels }`: only the fields the user may read, no permission rules |

## Documents (`/resource/{doctype}`)

| Call | Purpose |
|---|---|
| `GET /resource/{doctype}` | List. Query: `fields` (JSON array), `filters`, `order_by` (`field desc`), `limit_start`, `limit_page_length` (max 1000) |
| `POST /resource/{doctype}` | Create (201). Body: field values; child tables as arrays of row objects |
| `GET /resource/{doctype}/{name}` | One document with its table rows |
| `PUT /resource/{doctype}/{name}` | Update; send changed fields (and `modified` for conflict detection) |
| `DELETE /resource/{doctype}/{name}` | Delete (not submitted documents, not singles) |
| `POST /resource/{doctype}/{name}/submit` / `/cancel` | Submittable DocTypes; optional body `{ "modified": ... }` |
| `POST /resource/{doctype}/{name}/rename` `{ newName, merge }` | Rename or merge; links follow |
| `GET /resource/{doctype}/export` | CSV download (export right); same query parameters as the list |
| `GET /resource/{doctype}/report?group_by=&aggregate=&field=` | Grouped summary (report right): `count`, `sum`, `avg`, `min`, `max` |
| `GET /tree/{doctype}?parent=` | One level of a tree |
| `GET /resource/{doctype}/group-by?field=&filters=&limit=` | Counts per value of a field under the list filters: `[{ value, count }]` (`value` null = empty; Check fields give "1"/"0"). Select, Link, Check, Data, Email, Phone, Int fields you may read, or `owner`, `modified_by`, `docstatus`; max 50 groups |
| `GET /resource/{doctype}/comment-counts?names=["a","b"]` | `{ name: count }` for the documents you may read (max 100 names; system notes not counted) |

**Filters** are `[[field, operator, value], ...]` or an object of equality pairs. Operators: `=` `!=` `>` `<` `>=` `<=` `like` `not like` `in` `not in` `between` (`[from, to]`, ends included) `timespan` (`"today"`, `"this month"`, `"last 7 days"`, … see ADR-060) `is` (`"set"` / `"not set"`). The values `"__user"`, `"__today"` and `"__now"` stand for the caller and the current date or moment. Fields must be columns the user may read. Text matching is case-insensitive.

**Single DocTypes:** `GET /resource/{doctype}/{doctype}` returns the defaults until first saved; `PUT` to the same URL creates then updates it.

**Example**

```bash
curl -b jar.txt "http://localhost:5101/api/resource/Customer?filters=[[\"customer_type\",\"=\",\"Company\"]]&fields=[\"name\",\"customer_name\"]&order_by=modified%20desc"
```

## Updates log and comments

| Call | Purpose |
|---|---|
| `GET /resource/{doctype}/{name}/timeline?limit=&before=` | Versions and comments merged, newest first (`limit` max 100; `before` is the oldest `at` you already have). Items: `{ kind: "version"\|"comment", id, event, user, userName, at, data, content, edited, canEdit }` |
| `GET /resource/{doctype}/{name}/comments` | Only the comments |
| `POST /resource/{doctype}/{name}/comments` `{ content }` | Add a comment (201) |
| `PUT/DELETE /resource/{doctype}/{name}/comments/{id}` | Edit (author) or delete (author, or Administrator) |

For versions `data` is `{ changed: [[field, before, after], ...], tables: { field: { added, removed, changed } } }` limited to fields the caller may read; `event` is Insert, Update, Submit, Cancel, Rename, Merge or Delete. Comments are plain text; `event` is Comment or Info (system note).

## Assignments, following and notifications

| Call | Purpose |
|---|---|
| `GET /resource/{doctype}/{name}/assignments` | Who it is assigned to (open and done) |
| `POST /resource/{doctype}/{name}/assignments` `{ user, description, date, priority }` | Assign (needs the right to share it); `date` is `yyyy-MM-dd`, `priority` Low, Medium or High |
| `GET /assignments/mine?status=Open\|Closed` | My assignments, with the document each refers to |
| `POST /assignments/{id}/complete`, `DELETE /assignments/{id}` | Mark done, or take back (assignee, assigner or Administrator) |
| `GET\|POST\|DELETE /resource/{doctype}/{name}/follow` | State `{ following, followers }`, follow, unfollow (needs read access) |
| `GET /notifications?unread=true&limit=&before=` | My notifications, newest first |
| `GET /notifications/count` | `{ unread }` |
| `POST /notifications/{id}/read`, `POST /notifications/read-all` | Mark mine read |

## List settings, saved filters and bulk actions

| Call | Purpose |
|---|---|
| `GET /list-settings/{doctype}` | `{ settings, filters }`: my columns/page size/sort (null for the usual view) and the saved filters I can pick (mine, then shared) |
| `PUT /list-settings/{doctype}/settings` `{ columns, pageSize, sortField, sortDescending }` | Save my settings (validated against what I may read) |
| `DELETE /list-settings/{doctype}/settings` | Back to the usual view |
| `POST /list-settings/{doctype}/filters` `{ name, filters: [[field, op, value], ...], isPublic }` | Save (or replace by name) a filter; `isPublic` needs write access to the DocType |
| `DELETE /list-settings/{doctype}/filters/{id}` | Delete mine (or a shared one as Administrator) |
| `POST /resource/{doctype}/bulk` `{ action: "update"\|"delete", names: [...], field, value }` | Up to 100 documents; returns `{ data: [{ name, ok, error }], succeeded, failed }`. 401/403 when not signed in / no write or delete right |

## Workflows ([ADR-018](../../adr/018-workflow-engine.md))

| Call | Purpose |
|---|---|
| `GET /resource/{doctype}/{name}/workflow` | Where the document is: `{ stateField, state, color, actions: [{ action, to }], canEdit }`, or `data: null` when the DocType has no active workflow. `actions` only holds what the caller may do now |
| `POST /resource/{doctype}/{name}/workflow` `{ action, modified? }` | Do an action; answers with the new status. 403 for the wrong role or a failed condition, 400 for an unknown action, 409 for a stale `modified` |
| `GET /workflow/pending` | Documents with an action the caller can take now, across workflows |

Building workflows is under *Tools* below. Writing the state field through `PUT /resource` is refused with a validation error on that field.

## Auto Repeat ([ADR-021](../../adr/021-auto-repeat.md))

| Call | Purpose |
|---|---|
| `GET /resource/{doctype}/{name}/auto-repeat` | The caller's repeats of that document (all of them for a System Manager); needs read access to the document |
| `POST /resource/{doctype}/{name}/auto-repeat` `{ frequency, startDate, endDate?, dateFields? }` | Set a repeat up (201). 403 without create rights on the DocType, 400 for bad dates, frequency or fields |
| `GET /auto-repeat` | The caller's repeats, soonest first |
| `PUT /auto-repeat/{id}` `{ status?, endDate?, dateFields? }` | Pause or resume, change the end date or date fields (frequency and start date are fixed) |
| `POST /auto-repeat/{id}/run` | Make the next copy now |
| `DELETE /auto-repeat/{id}` | Stop repeating |

Someone else's repeat answers 404.

## Email ([ADR-023](../../adr/023-email-queue-and-templates.md))

| Call | Purpose |
|---|---|
| `GET /email/templates?doctype=` | Email templates offered for a DocType (name, DocType) |
| `POST /resource/{doctype}/{name}/email/preview` `{ template }` | The subject and message a template gives for the document |
| `POST /resource/{doctype}/{name}/email` `{ to[], subject?, message?, template? }` | Queue emails (202 `{ queued }`). Needs write access; 10 addresses, 50 a day per person; 400 for bad addresses or text |

## Dashboards ([ADR-024](../../adr/024-dashboards.md))

| Call | Purpose |
|---|---|
| `GET /dashboards` | The dashboards the caller may open (own, shared, and for a System Manager all) |
| `POST /dashboards` `{ title, isPublic, items[] }` | Create (201). Problems are keyed `items[2].groupBy` |
| `GET /dashboards/{id}` | The dashboard and its tiles, with `canEdit` |
| `GET /dashboards/{id}/data` | Every tile worked out for the caller: `{ id, type, number?, points?, columns?, rows?, truncated, error? }` |
| `PUT /dashboards/{id}` `{ title, isPublic, items[], modified? }` / `DELETE` | Owner or System Manager; 409 for a stale `modified` |
| `POST /dashboards/preview` `{ tile }` | One unsaved tile, for the builder |

A tile is `{ id, type: Number|Chart|List, title, docType, function, valueField, groupBy, period, chartType, columns, orderBy, descending, limit, filters: [{ field, op, value }], color }`.

## Printing ([ADR-025](../../adr/025-print-formats-and-letter-heads.md))

| Call | Purpose |
|---|---|
| `GET /print/choices?doctype=` | The formats made for a DocType and the letter head names (needs read access to the DocType) |
| `GET /print/{doctype}/{name}?format=&letterhead=` | The printable HTML page, with a strict Content-Security-Policy. `letterhead=none` removes the letter head |
| `GET/PUT/DELETE /print-formats[/{name}]`, `POST /print-formats/preview` | Manage formats and preview one on a document (System Manager). `PUT` takes `{ name, docType, html, isDefault, letterHead }` |
| `GET/PUT/DELETE /letter-heads[/{name}]` | Manage letter heads (`{ name, header, footer, isDefault }`) |

## Workspaces ([ADR-026](../../adr/026-workspaces.md))

| Call | Purpose |
|---|---|
| `GET /workspaces/mine` | The workspaces for the caller's roles, in order, with shortcuts and dashboards filtered to what the caller may use: `{ id, title, blocks: [{ type, text?, shortcuts?: [{ label, kind, path }], dashboardId?, dashboardTitle? }] }` |
| `GET/POST /workspaces`, `GET/PUT/DELETE /workspaces/{id}` | Manage (System Manager): `{ title, sequence, roles[], blocks[] }` where a block is `{ type: Heading|Shortcuts|Dashboard, text?, items?: [{ label, kind: DocType|Dashboard|Page, target }], dashboardId? }` |

## Data import ([ADR-027](../../adr/027-data-import.md))

| Call | Purpose |
|---|---|
| `GET /import/{doctype}/template?mode=Insert|Update` | A CSV with the columns a file may fill in |
| `POST /import/{doctype}/preview` `{ csv, mode }` | Check a file without importing: `{ columns: [{ header, field, label }], rows, sample, errors: [{ row, field, message }], moreErrors }` |
| `POST /import/{doctype}` `{ csv, mode, stopOnError }` | Import: `{ created, updated, failed, errors, stopped, moreErrors }`. 403 when the first row is refused for permission, 400 for a bad file (empty, unclosed quote, too big, no matching column) or mode |

## Webhooks ([ADR-029](../../adr/029-webhooks.md))

| Call | Purpose |
|---|---|
| `GET /webhooks/doctypes[/{name}]` | List DocTypes with their hook counts, or one DocType's hooks (secrets are never returned: `secretSet` says whether one is stored) |
| `PUT /webhooks/doctypes/{name}` `{ hooks: { docType, hooks: [{ name, active, event, condition?, url, secret?, fields[] }] }, modified? }` | Replace the hooks. A blank `secret` keeps the stored one; a new hook needs one (16 to 200 characters). 400 for non-https or non-public addresses, 409 for a stale `modified` |
| `DELETE /webhooks/doctypes/{name}` | Remove them |

Receivers verify a delivery like this (pseudo-code): `expected = hex(HMAC_SHA256(secret, t + "." + rawBody))` where `t` comes from `X-Qahwa-Signature: t=...,v1=...`; compare with `v1` in constant time and reject if `|now - t|` is large. `X-Qahwa-Delivery` is unique per delivery attempt chain: use it to ignore repeats.

## Attachments ([ADR-030](../../adr/030-attachments.md))

| Call | Purpose |
|---|---|
| `GET /files/for/{doctype}/{name}` | The files attached to a document (needs read access): `{ id, fileName, contentType, size, image, uploadedBy, created }` |
| `POST /files/for/{doctype}/{name}` | Upload: the **raw bytes** as the body and the name in the `X-File-Name` header (percent-encoded UTF-8). Needs write access. 201; 400 for empty, too big (default 25 MB) or a missing name; 403 without write access |
| `GET /files/{id}` | Download. Pictures (by content) are inline; everything else is `application/octet-stream` with `Content-Disposition: attachment`; always `nosniff`, a sandboxing CSP and `no-store`. 404 when the caller cannot read the document |
| `DELETE /files/{id}` | Remove (the uploader, whoever can change the document, or a System Manager) |

Example: `curl -X POST -H "Authorization: Bearer <key>" -H "X-File-Name: contract.pdf" --data-binary @contract.pdf https://server/api/files/for/Customer/CUST-0001`

## Link fields

| Call | Purpose |
|---|---|
| `GET /link/{doctype}/search?q=&limit=` | Choices `[{ value, label, description }]` matching a name or title (max 20), only documents the user may read |
| `GET /link/{doctype}/titles?names=["a","b"]` | Friendly names of chosen values, keyed by value (max 50) |

## Sharing

| Call | Purpose |
|---|---|
| `GET /share/{doctype}/{name}` | Who it is shared with (`user`, `fullName`, `read`, `write`) |
| `GET /share/{doctype}/{name}/users?q=` | People it could be shared with (by name or email) |
| `PUT /share/{doctype}/{name}` `{ user, read, write }` | Share, change, or remove (`read` and `write` both false) |

## Tools (System Manager or Administrator)

| Call | Purpose |
|---|---|
| `GET/POST/PUT/DELETE /designer/doctypes[/{name}]`, `GET /designer/field-types`, `POST /designer/preview` | The DocType Designer |
| `GET/PUT/DELETE /permissions/doctypes[/{name}]` | Role Permission Manager (customise, reset) |
| `GET/PUT/DELETE /customize/doctypes[/{name}]` | Customize Form |
| `GET/PUT/DELETE /workflows/doctypes[/{name}]` | Workflow Builder: `PUT` takes `{ workflow, modified }`; `GET` returns `{ docType, source, exists, modified, workflow, stateFields, canEdit }` |
| `GET/PUT/DELETE /assignment-rules/doctypes[/{name}]` | Assignment Rules: `PUT` takes `{ rules: { docType, rules: [...] }, modified }`; `GET` returns `{ docType, source, exists, modified, set, userFields, canEdit }` |
| `GET/PUT/DELETE /notification-rules/doctypes[/{name}]` | Notification Rules: `PUT` takes `{ rules: { docType, rules: [...] }, modified }`; `GET` returns `{ docType, source, exists, modified, set, userFields, fields, dateFields, canEdit }` |
| `GET/PUT /naming/series` | Naming series counters |

PUT bodies carry the version you loaded (`modified`) and fail with 409 if someone else changed it.
