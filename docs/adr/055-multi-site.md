# ADR-055: Several sites in one server (database per site)

**Status:** accepted (L12)

## Context
One installation often serves several organisations or environments (a company and its subsidiaries, customers of a hosted service). Sharing one database with a "tenant" column in every table would put every query, cache and background job one mistake away from showing one site's data to another. The plan chose **a database per site** (as Frappe does), with site-specific settings and services.

## Decision
- **A site is a folder** `<sites>/<name>/site_config.json` with `hosts` (host names, or one-label wildcards like `*.acme.example.com`) and `connectionString` (or `connectionStringEnv`, the name of an environment variable, so the secret stays out of the file). The rest of the file is layered over the shared configuration for that site (`Qahwa:Auth:PublicUrl`, `Qahwa:Email`, `Qahwa:Auth:SignUp`, …). The server reads the folder named by `Qahwa:Sites:Path`; without it nothing changes from a single-site server.
- **A container per site.** `AddQahwaSite` registers everything one site needs; the same function serves the main site and every extra site, each with its own service provider, so the DocType registry, caches, stores, rate counters, sessions, secrets (Data Protection application name `qahwa:site:<name>`) and schedulers cannot be shared. A middleware picks the container from the request's `Host` header before anything else looks at the request.
- **Host matching is strict.** Only the `Host` header, exactly (case-insensitive, port and trailing dot ignored); an exact name beats a wildcard; a wildcard covers one label; malformed names (spaces, `@`, `/`, control characters, wildcards in a request) match nothing; `X-Forwarded-Host` is never consulted (set up forwarded headers at the proxy if you need that). A host no site claims goes to the main site when it has a database (`ConnectionStrings:Default`), else gets 421 `unknown_site`; `/api/health` always answers.
- **Mistakes stop the server at start-up, together:** invalid names, no hosts, a host on two sites, **two sites on one database**, an unset environment variable, unreadable files. A site whose schema, patches or settings fail to load stops the server with the site's name.
- **Settings that must not be shared get site defaults:** uploaded files go to `<files>/sites/<name>`, scheduled backups to `<backups>/sites/<name>`, and links in emails use `https://<first plain host>` (never the main site's address; none when the site has only wildcards) unless the site's file says otherwise. A site's file can never change its own database or name.
- **Every site runs its own scheduler** (repeating documents, queued email, webhooks, backups).
- **CLI:** `qahwa new-site <name> --sites <dir> --host <h> --admin-connection <cs> [--email …]` checks the new site against the others, creates the database `qahwa_<name>` (name checked and quoted), writes the file, creates the tables and the administrator; `qahwa list-sites`; `migrate` and `create-admin` take `--site <name> --sites <dir>`.

## Consequences
- Not yet: creating or removing sites while the server runs (restart to pick up changes), per-site DocType sets (all sites share the DocType folders; custom DocTypes live in each site's database), a site manager UI, a SQL Server database per site.
- A site can be moved by restoring its backup into another installation; the database is the whole site (files excepted).
- Memory grows with the number of sites (each has its own registry and caches); thousands of tiny sites need another approach.
