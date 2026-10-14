# Qahwa

Metadata-driven full-stack framework for .NET (inspired by Frappe). Define a DocType in JSON and get the table, the API and (soon) the UI.

See [docs/PLAN.md](docs/PLAN.md) for the roadmap and [docs/adr](docs/adr) for design decisions.

## Status: Phase 1 (MVP backend)

Working today:

- DocType JSON → validated metadata (`Qahwa.Core`)
- Schema sync: creates tables, adds missing columns and indexes, reports type drift without touching data (`Qahwa.Data`)
- Document engine: naming (hash / field / prompt / series), defaults, validation, link checks, optimistic concurrency, role and owner permissions
- Generic REST API with Frappe-style filters: `/api/resource/{doctype}`, `/api/meta`
- Authentication: `User` / `Role` / `Has Role` DocTypes, cookie login, lockout, rate limiting, change password ([ADR-002](docs/adr/002-authentication.md))
- PostgreSQL provider, `qahwa migrate` and `qahwa create-admin` CLI

- Desk (React + TypeScript): login, DocType sidebar, generic list view (search, dynamic filters kept in the URL, sorting, paging) and form view (field controls per type, server-side validation errors inline, concurrent-edit detection, delete), account page with change password. Layout is right-to-left ready and works on phones.

Not yet: child tables, submit/cancel, versions, comments/share/assign/follow, saved filters and per-user list settings, attachments, the Frappe-style form sidebar.

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
