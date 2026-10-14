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

Not yet: child tables, submit/cancel, versions, comments/share/assign/follow, the Desk UI beyond the Vite skeleton.

## Try it

Requires the .NET 10 SDK and Docker.

```bash
docker compose up -d postgres
dotnet run --project src/Qahwa.Cli -- migrate --connection "Host=localhost;Database=qahwa;Username=qahwa;Password=qahwa_dev_only" --doctypes samples/doctypes
dotnet run --project src/Qahwa.Cli -- create-admin --connection "Host=localhost;Database=qahwa;Username=qahwa;Password=qahwa_dev_only" --email you@example.com
dotnet run --project src/Qahwa.Server        # Development: auto-migrates on start
```

`create-admin` prompts for the password (10+ characters). Then log in and use the API with the session cookie:

```bash
curl -c jar.txt -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" -d '{"email":"you@example.com","password":"<your password>"}'

curl -b jar.txt -X POST http://localhost:5000/api/resource/Customer \
  -H "Content-Type: application/json" -d '{"customer_name":"Acme"}'
```

Auth endpoints: `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`, `POST /api/auth/change-password`.

## Tests

```bash
dotnet test      # PostgreSQL integration tests use Testcontainers and skip themselves if Docker is not running
```
