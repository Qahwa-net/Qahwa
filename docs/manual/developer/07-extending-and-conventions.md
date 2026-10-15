# Extending and conventions

## Adding things

**A DocType for your app.** Put a JSON file in a folder listed in `Qahwa:DocTypePaths` and run `migrate` (or start in Development). Or design it in the Desk.

**A framework DocType.** Add JSON under `src/Qahwa.Core/CoreDocTypes` (embedded resource). Load order does not matter; links are checked after everything is loaded. Give it a System Manager rule.

**A rule that belongs in the engine.** Put it in `DocumentService` (or a partial file beside it, such as `DocumentService.Special.cs`). Core is database-free: new storage needs an interface in Core, an implementation in `Qahwa.Data`, an in-memory one in `tests/Qahwa.Testing`, and DI wiring in `Qahwa.Hosting`.

**An endpoint.** In `Qahwa.Api`, a small static class with `MapQahwa...`; resolve the user with `IUserContextResolver`, call one service method, return `{ data }`. No logic in endpoints. Register it in `QahwaEndpoints.cs`.

**New table or column support.** Extend `ISqlDialect` and `SchemaSync`; system tables are created in `EnsureSystemTablesAsync`.

**Desk pages.** A page in `src/Qahwa.Desk/src/pages`, API calls in `api/client.ts` with types in `api/types.ts`, pure logic in `lib/` with Vitest tests, a route in `App.tsx` and a nav link in `shell/Shell.tsx`.

## Security rules (always)

- **SQL**: only through `SqlBuilder`. Identifiers come from DocType definitions and are checked against the column whitelist (`ColumnTypes()`); every value is a parameter. Never concatenate request text into SQL.
- **Authorisation on the server**, in the service, for every path (get, list, write, delete, export, search). The Desk hiding a button is not security.
- **Do not leak**: absent not null for hidden fields; check access before loading so "missing" and "forbidden" look alike; filters, sort and field lists on unreadable fields are refused.
- **Validate and limit** all input: lengths, counts, ranges, formats; bound loops and recursion.
- **Output**: CSV cells that start with `=`, `+`, `-`, `@` are defused; filenames are sanitised; React escapes text (never `dangerouslySetInnerHTML` on user data).
- **Secrets** (passwords, tokens) are never logged, listed, exported or returned.
- **Privilege escalation**: anything that grants roles or access (profiles, shares, user permissions) needs the same right as granting it directly.
- Write tests with hostile input (quotes, wildcards, huge values, unknown fields) and for who may and may not call each thing.

## Testing

Every feature ships with: Core tests (in-memory), API tests (status codes and shapes), a Postgres test when SQL changes, and Desk tests for pure logic. Verify in a browser against the real database before calling it done. Existing tests are the best examples: `ShareTests`, `SpecialDocTypeTests`, `ExportRenameSeriesTests`, `CustomizationTests`, `SpecialApiTests`, `PostgresPermissionTests`.

## Code style

Match the surrounding code: small files with one purpose, `///` summaries that say why, no clever abstractions, immutable records for data, `TimeProvider` for time, exceptions for expected failures (with stable codes).

## Documentation is part of done

For every new feature, before you call it finished:

1. **Developer manual** (`docs/manual/developer`): the DocType properties (`03`), endpoints (`04`), access rules (`05`), and a section in `06-features-reference.md`.
2. **User guide** (`docs/manual/user`): how someone uses it in the Desk, with the screen names as they appear.
3. **Architecture decision record** (`docs/adr/NNN-title.md`): context, decision, consequences, limits.
4. **README** feature list and the **plan** (`docs/PLAN.md`, kept in sync with `../Qahwa-Framework-Plan.md`).
5. Mention security considerations (what could go wrong and how it is prevented).
