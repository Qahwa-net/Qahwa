# ADR-001: Plain ADO.NET behind a dialect abstraction (not Dapper or EF Core)

**Status:** accepted (Phase 1)

## Context
DocType schemas are dynamic: tables and columns come from metadata at runtime, so there are no compile-time entity classes.
The original plan suggested Dapper. While building the repository we found two problems with it:

- Dapper does not map `DateOnly`/`TimeOnly`, and its `DateTime` handling conflicts with Npgsql's `timestamptz` rules
  (a UTC `DateTime` sent as `timestamp without time zone` is rejected).
- Our statements are already fully generated and parameterized by `SqlBuilder`, so Dapper's object mapping adds nothing.

## Decision
`DocumentRepository` uses `DbConnection`/`DbCommand` directly. Parameters are positional (`@p0..@pN`) and typed by the CLR value
(Npgsql infers `date`, `timestamptz`, `numeric`, `boolean` from `DateOnly`, UTC `DateTime`, `decimal`, `bool`).
Everything provider-specific lives behind `ISqlDialect`, `IDbConnectionFactory` and `ISchemaInspector`; `Qahwa.Data` has no package dependencies.

## Consequences
- SQL Server support means writing one dialect, one inspector and one connection factory.
- Values are converted to storage types in `FieldValueConverter` before they reach the repository.
- Identifiers are only ever taken from the DocType column whitelist (`DocTypeMeta.ColumnTypes()`); values are never concatenated into SQL.
