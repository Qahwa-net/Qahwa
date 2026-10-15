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

**Filters** are `[[field, operator, value], ...]` or an object of equality pairs. Operators: `=` `!=` `>` `<` `>=` `<=` `like` `in` `is` (`"set"` / `"not set"`). Fields must be columns the user may read. Text matching is case-insensitive.

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
