# ADR-104: Backup and restore from the command line

Status: accepted

## Decision
`qahwa backup --out <file.zip>` and `qahwa restore <file.zip> [--dry-run]` (with `--connection` or `--site`, and the same `--doctypes` the server uses) run the existing `BackupService` (ADR-052) as the Administrator, so the file has the same content and the same exclusions (no passwords, sessions, two-factor secrets or uploaded file contents) and restore still only **adds**. `backup` creates the file with `CreateNew`, so it never overwrites an existing file, and deletes an empty leftover if it fails. The CLI is for people who already control the server and the database, which is why it needs no sign-in.

## Not built, on purpose
- `init`, `new-site`: `new-site` already makes a site.
- `set-config`: configuration is a JSON file (`site_config.json`, ADR-055) that an operator edits; a command that writes secrets into files would only add a place for them to leak.
- `console` and `run-tests`: a console would bypass every permission; tests run with `dotnet test`.
- `new-app`: apps are ordinary .NET projects (see the app guide).
