# ADR-062: Apps, hooks, server functions and developer mode

**Status:** accepted (L12)

## Context
Server Scripts (ADR-053) let an administrator add small rules in the browser, but real products need code that is written, reviewed, tested and shipped like any other: new DocTypes, logic on documents, functions other systems can call, scheduled work and starting data. The plan called for installable apps with hooks, whitelisted functions (`/api/method`) and a developer mode that writes Designer work into files.

## Decision
- **An app is a class implementing `IQahwaApp`** (name, version, optional DocType folder, patches, and `Configure(IAppBuilder)`), registered in the host with `AddQahwaApp<T>()`. Names are checked (lower case, `qahwa` reserved, unique); everything an app registers is collected into an `AppCatalog` at start-up, and a mistake (bad or repeated method name, a job more often than once a minute, a hook or fixture for a DocType that does not exist) stops the server with the app's name. Each site records its apps in **Installed App** (read-only for System Managers) and keeps the version current; fixtures are made once, the first time.
- **Document hooks** (`DocEvent`: before and after save, submit, cancel and delete) are C# delegates. Before hooks work on a copy of the document: changes go through the same conversion and validation as client input, system fields, tables and passwords cannot be set, a refusal stops the change, and any other failure stops it as well and is recorded with the app's name. After hooks cannot undo anything; failures go to the Error Log. Hooks act as the person who made the change, and a change made inside a hook does not start other hooks.
- **Server functions** (`POST /api/method/<app>.<name>`, `GET` only when the function allows it): need a signed-in person, optional roles (Administrators pass), a per-person per-function rate limit and a 1 MB body limit; names must match a strict pattern, unknown ones are 404. The built-in `qahwa.get_count`, `get_value`, `get_list`, `set_value` and `rename_doc` are wrappers over `DocumentService`, so permissions, validation, hooks and scripts apply as for the REST API.
- **Scheduled jobs** run from the existing scheduler tick when due (in memory per server; they must be safe to run twice). A failure is recorded and never stops other jobs.
- **Developer mode** (`Qahwa:Developer:Enabled`, `AppPath`): Designer creates, changes and deletes also write or remove `<AppPath>/<name>.json`, atomically, with a file name reduced to letters, digits and underscores and a path that cannot leave the folder; a file problem never undoes the Designer change; `POST /api/developer/export-all` writes everything made so far. A DocType that exists identically as a file and in the database is accepted quietly at start-up. Developer mode is off by default and a misconfiguration (on without a folder) stops the server.
- **Isolation across sites** follows ADR-055: each site has its own catalog instance and installed-app records; an app is code and is shared by all sites of the server.

## Consequences
- Apps are compiled code running in the server process with full trust: install only apps you would run anyway. The sandbox of ADR-053 is for scripts, not apps.
- Not yet: boot hooks, app dependency and version constraints, removing an app (uninstall), hooks for permission checks and queries, a CLI `install-app`, and per-site enabling of an app.
- Job timing is per server and in memory, so a restarted server runs its jobs at the first tick.
