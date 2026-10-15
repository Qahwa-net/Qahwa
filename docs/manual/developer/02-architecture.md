# Architecture

## The idea

A **DocType** is metadata: a name, fields, permission rules, rules and flags. Everything else is generic and driven by it:

```
DocType JSON ──► MetaRegistry ──► DocumentService ──► IDocumentRepository ──► SQL
   (data)        (what exists)    (the rules)          (storage)
                      │                  ▲
                      ▼                  │
              REST API (generic)    Desk (generic list/form)
```

There is no code per DocType. A custom DocType made in the Desk is a row of JSON in `__doctype`; a table is created for it and the API and Desk work at once.

## Layers (dependencies point down)

| Layer | Responsibility |
|---|---|
| `Qahwa.Api` | HTTP only: parse the request, resolve the user, call a service, shape the JSON and the error envelope |
| `Qahwa.Core` | Everything that is a rule: `DocumentService` (insert, get, update, delete, list, submit, cancel, rename, export, report, link search, tree), `PermissionService`, `DocumentLogic` (conditions, formulas, fetch), `NamingService`, managers for permissions, customisation, designer, sharing, series |
| `Qahwa.Data` | Turn engine requests into parameterised SQL (`SqlBuilder`), keep the schema in step (`SchemaSync`), small stores for `__doctype`, `__permissions`, `__customize`, `__auth` |
| `Qahwa.Data.Postgres` | The dialect: quoting, types, series SQL, `ILIKE`, error codes |

Core never touches ADO.NET: it talks to interfaces (`IDocumentRepository`, `IPermissionStore`, ...), which is why the Core and API tests run on in-memory implementations. No ORM and no Dapper: see [ADR-001](../../adr/001-ado-net-instead-of-dapper.md).

## The metadata registry

`MetaRegistry` holds an immutable snapshot of all DocTypes, swapped atomically when one changes, so a request in flight never sees a half-updated set. Each DocType has a **source**:

- **Core**: ships with the framework (User, Role, Has Role, profiles, DocShare, User Permission). Read-only in the designer.
- **App**: loaded from JSON files in `DocTypePaths`. Read-only in the designer; extend with the Customize Form.
- **Custom**: made in the DocType Designer, stored in the database.

What a request sees is the **effective** DocType: the definition with a customisation applied (Customize Form), so the engine itself needs no idea that customisation exists.

## A write, step by step (`DocumentService.InsertAsync` / `UpdateAsync`)

1. Resolve the DocType; check the module is not blocked (Module Profile).
2. Authorise: role rules first, then a share for this document (read/write only), then User Permissions (restrictions on linked values).
3. Work out the field levels the user may write (permission levels).
4. Convert the input to storage types; reject fields the user may not write.
5. Apply defaults, run conditions, formulas and "fetch from" (`DocumentLogic`).
6. Validate: required, types, lengths, link targets, rules (expressions), tree integrity, submitted-document locking.
7. Name the document, stamp `owner`, `creation`, `modified`, and write parent and child rows in **one transaction** (optimistic concurrency on `modified`).
8. The updates-log row (`Version`) is written with the change as a **side write** of the same transaction (also for delete and rename). After-save hooks (Role Profiles) run, and the document is returned with unreadable fields removed.

Reads follow the same authorisation, then list queries add filters (owner-only, shared names, User Permissions) *before* paging, so totals and pages stay correct.

## Cross-cutting rules

- **Errors** are exceptions with a stable code and status (`DocumentValidationException` 400, `UnauthenticatedException` 401, `PermissionDeniedException` 403, `NotFoundException` 404, `ConflictException` 409), turned into `{ code, message, details }` by one handler.
- **Time** comes from `TimeProvider` (tests advance it); timestamps are UTC and truncated to microseconds to round-trip PostgreSQL exactly.
- **Everything configurable is data**: DocTypes, permission overrides, customisations, shares, profiles, user permissions are rows, edited in the Desk, cached as immutable snapshots where hot.

## The Desk

React 19, TypeScript, Vite, TanStack Query, react-router. It is generic: `ListView`, `FormView` and `FieldControl` render any DocType from `/api/meta/{doctype}`; the same expression language runs in the browser (`src/lib/expr`) and on the server so live feedback and the final check agree. Pages for tools (designer, customize, permissions, naming series) sit beside them. The server is always authoritative: the Desk hides what a user cannot do, the API refuses it.

## Decisions

Each significant decision has a short record in [`docs/adr`](../../adr): authentication, designer, child tables, the expression language, permission levels, user permissions, sharing, submittable documents, profiles and export, special DocTypes, link fields, Customize Form.
