# Qahwa

Metadata-driven full-stack framework for .NET (inspired by Frappe). Define a DocType in JSON and get the table, the API and (soon) the UI.

See [docs/PLAN.md](docs/PLAN.md) for the roadmap and [docs/adr](docs/adr) for design decisions.

## Status: Phase 1 (MVP backend)

Working today:

- DocType JSON → validated metadata (`Qahwa.Core`)
- Schema sync: creates tables, adds missing columns and indexes, reports type drift without touching data (`Qahwa.Data`)
- Document engine: naming (hash / field / prompt / series), defaults, validation, link checks, optimistic concurrency, role and owner permissions
- Generic REST API with Frappe-style filters: `/api/resource/{doctype}`, `/api/meta`
- PostgreSQL provider, `qahwa migrate` CLI

Not yet: real authentication (a development-only header login exists), child tables, submit/cancel, versions, the Desk UI beyond the Vite skeleton.

## Try it

Requires the .NET 10 SDK and Docker.

```bash
docker compose up -d postgres
dotnet run --project src/Qahwa.Cli -- migrate --connection "Host=localhost;Database=qahwa;Username=qahwa;Password=qahwa_dev_only" --doctypes samples/doctypes
dotnet run --project src/Qahwa.Server        # Development: auto-migrates and enables header login
```

```bash
curl -X POST http://localhost:5000/api/resource/Customer \
  -H "X-Qahwa-User: me@example.com" -H "X-Qahwa-Roles: Sales Manager" \
  -H "Content-Type: application/json" -d '{"customer_name":"Acme"}'
```

The `X-Qahwa-User` login only works when `Qahwa:Api:DevAuthEnabled` is true **and** the environment is Development.

## Tests

```bash
dotnet test      # PostgreSQL integration tests use Testcontainers and skip themselves if Docker is not running
```
