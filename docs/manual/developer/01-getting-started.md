# Getting started

## Requirements

- .NET 10 SDK
- Docker (for PostgreSQL, and for the database tests, which skip themselves without it)
- Node.js 20+ for the Desk (React, TypeScript, Vite)

## Run the app

```bash
docker compose up -d postgres            # PostgreSQL 17, user qahwa, database qahwa
cd src/Qahwa.Desk && npm install && npm run build && cd ../..   # the server serves the built Desk
dotnet run --project src/Qahwa.Cli -- migrate --connection "Host=localhost;Database=qahwa;Username=qahwa;Password=qahwa_dev_only" --doctypes samples/doctypes
dotnet run --project src/Qahwa.Cli -- create-admin --connection "..." --email you@example.com
dotnet run --project src/Qahwa.Server    # http://localhost:5101
```

Environments:

- **Development** reads `appsettings.Development.json` (connection string, `AutoMigrate: true`), so the schema is brought up to date on every start.
- **Production**: set `ConnectionStrings:Default`, keep `AutoMigrate` false and run `migrate` as a deployment step, serve over HTTPS, share the Data Protection key ring between instances and configure forwarded headers behind a proxy (see the README deployment notes).

Settings (`appsettings.json`, section `Qahwa`):

| Key | Meaning |
|---|---|
| `DocTypePaths` | Folders of DocType JSON files (an app's definitions) loaded at start |
| `AutoMigrate` | Run the schema sync at start |
| `Qahwa:Email:Host`, `Port`, `UseSsl`, `User`, `Password`, `From` | Outgoing mail. Without a `Host` messages are only logged. Keep the password in user secrets or environment variables |
| `Qahwa:Webhooks:AllowInsecure` | Development only: allow http and local addresses for webhooks. Never in production |
| `Qahwa:Files:Path`, `MaxMegabytes` | Where attachments are stored (default `data/files` next to the server; back it up) and the largest file (1 to 100, default 25) |
| `Qahwa:Scheduler:Enabled`, `IntervalSeconds` | The background scheduler (auto repeat): on by default, every 300 seconds (10 to 3600) |
| `Auth:MinPasswordLength`, `MaxFailedAttempts`, `LockoutMinutes` | Password and lockout policy |
| `Auth:LoginPermitsPerMinute` | Login rate limit |
| `Auth:SessionHours`, `SessionCacheSeconds` | Cookie lifetime; how long a session's roles are cached |

## Desk development

`npm run dev` in `src/Qahwa.Desk` serves http://localhost:5173 and proxies `/api` to the server. `npm run build` writes to `src/Qahwa.Server/wwwroot`. Type check with `npx tsc -b`, test with `npx vitest run`.

## Tests

```bash
dotnet test                         # Core, API and Postgres (Testcontainers) suites
cd src/Qahwa.Desk && npx vitest run # Desk logic tests
```

- `tests/Qahwa.Core.Tests` use an in-memory repository (`tests/Qahwa.Testing`), so they are fast and need nothing.
- `tests/Qahwa.Api.Tests` start the real server in-process (`QahwaFactory`) with in-memory stores. Requests are authenticated with `X-Qahwa-User` and `X-Qahwa-Roles` headers (test-only), or pass `realAuth: true` for the cookie login.
- `tests/Qahwa.Data.Tests` run against a real PostgreSQL container, one schema per test.
- The expression language has shared test vectors for C# and TypeScript (`tests/expression-vectors/vectors.json`).

## Repository layout

```
src/Qahwa.Abstractions   field types and shared contracts
src/Qahwa.Core           DocType metadata, document engine, permissions, features (no database code)
src/Qahwa.Data           ADO.NET repository, SQL builder, schema sync, stores (database neutral)
src/Qahwa.Data.Postgres  PostgreSQL dialect and connection setup
src/Qahwa.Hosting        dependency injection wiring, migrator
src/Qahwa.Api            minimal API endpoints
src/Qahwa.Server         the web host (serves the API and the built Desk)
src/Qahwa.Cli            migrate, create-admin
src/Qahwa.Desk           React + TypeScript UI
docs/                    this manual, ADRs, the plan
```
