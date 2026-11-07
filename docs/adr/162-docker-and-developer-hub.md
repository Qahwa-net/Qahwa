# ADR-162: One-command Docker, a bootstrap administrator and developer tools in the Desk

**Status:** accepted

## Context
Trying Qahwa meant installing the .NET SDK and Node, starting PostgreSQL, running two CLI commands and building the Desk. `docker-compose.yml` only held the database. Developers also had to remember where each tool lives (a dozen sidebar entries and routes) and to read the REST reference to call a DocType from a program.

## Decision
- **A `Dockerfile`** builds the Desk (Node), publishes the server with it (.NET SDK) and runs it in the small ASP.NET image as a non-root user on port 8080, with a health check on `/api/health`, files in `/app/data` and the sample DocTypes in `/app/samples/doctypes`.
- **`docker-compose.yml`** adds an `app` service beside `postgres`: `docker compose up --build` is the whole install. The app waits for the database's health check. Volumes keep the database and the files. Port, administrator and database password come from `QAHWA_*` variables (a `.env` file). `docker compose up -d postgres` still starts only the database.
- **A bootstrap administrator:** `Qahwa:Bootstrap:AdminEmail`, `AdminPassword`, `AdminName` make the first administrator at start, **only when that account does not exist**, so a later password change is never put back by a restart. A weak password is refused with the reason in the log.
- **`Qahwa:Auth:AllowHttp`** lets the sign-in cookie travel over plain http, for a trial on one machine; off by default (outside Development the cookie is Secure-only). The compose file sets it; the guide says to remove it behind https.
- **A Developer Hub** page (`/developer`, in *Tools*) lists every developer and administrator tool grouped by the job with a one-line purpose and a search over names, descriptions and keywords.
- **An API tab in the DocType Designer** shows copyable `curl` calls (list, read, create, update, delete, submit) for the DocType, with an example body built from its required fields, and a link to `/api/openapi.json`.
- **Guides:** *Docker and deployment* and *Troubleshooting* in the developer manual, a *Developer Hub* page in the user guide, and Docker first in the README.

## Consequences
- The default administrator password in the compose file is public; the guide says to change it and to remove it from the file after the first sign in. The default database password stays local-only (the port is bound to 127.0.0.1).
- Not done: a published image in a registry, a Helm chart, SQL Server in the compose file.
