# ADR-148: Site manager: add and remove sites while the server runs

**Status:** accepted

## Context
ADR-055 runs several sites in one server but reads them once at start-up: a new customer meant the CLI plus a restart that interrupted every other site. The plan listed "live add/remove, a site manager UI" as the gap.

## Decision
- **`SiteHost` changes while it runs.** What requests see is one immutable snapshot (the sites and their host resolver) that is swapped whole, so a request never meets a half-changed set. `AddAsync` checks the new site against the running ones (name, host names, database), builds its container, brings it up to date (schema, definitions, patches), starts its scheduler and realtime backplane, and only then publishes it; a failure leaves the running sites exactly as they were. `RemoveAsync` unpublishes first (no new request reaches the site), then stops its schedulers and disposes its container. Changes are one at a time.
- **Remove means "stop serving", never "delete".** The site's database and files are untouched, and its `site_config.json` is renamed to `site_config.json.removed-<time>` so a restart does not bring it back; renaming it back restores it. A removal that also destroyed data would turn a mis-click into an outage with no way back.
- **`SiteManager`** (server) does what `qahwa new-site` does, without the CLI: validates the name and hosts, creates the database (`ISiteDatabases`, a seam: PostgreSQL or SQL Server by `Qahwa:Database:Provider`), writes the site file, adds the site live and creates its first administrator. If anything after the file is written fails, the running site and the folder are undone; a database that was just made is kept and named in the log.
- **API** `GET/POST /api/sites`, `DELETE /api/sites/{name}`, and a **Sites** page in the Desk. The password goes in the body, never in an address.
- **Who may:** only an Administrator of the **main** site, signed in (no API keys), and only when the operator sets `Qahwa:Sites:Manage` and the server has `Qahwa:Sites:Path` and `Qahwa:Sites:AdminConnection` (a login that may create databases). Everywhere else the endpoints answer 404 as if they did not exist: an extra site's own container does not even hold the manager, so a site can never administer its neighbours. The Desk shows the page only when the list call succeeds.
- `Qahwa:Sites:MigrateNew` (default true) controls migrating a new site's database; it exists for tests whose stores have no schema.

## Consequences
- Verified for real: on PostgreSQL a site was added while the server ran, answered at once under its own host, kept its own administrator and data apart from the main site, was refused to a main-site session, and stopped answering when removed.
- Not yet: renaming a site or changing its hosts live, bringing a removed site back from the Desk, deleting a site's database from the Desk (use your database tools after removing it), a SQL Server run of the manager (the code path is the same as `new-site`, which has SQL Server tests).
- A server whose sites are all managed live still has to keep the sites folder on durable storage: the folder is the list of sites.
