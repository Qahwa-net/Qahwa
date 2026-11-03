# ADR-043: Patches (one-time data migrations)

**Status:** accepted (L12, part 6)

## Context
Schema changes are additive and automatic, but a new version sometimes needs data changed too: fill a new field from an old one, rename a value, tidy rows. That must happen exactly once per site, in order, even when several servers start together, and a failure must not be silent.

## Decision
- **A patch is code with a name.** `IPatch { Name; RunAsync(services, ct) }`, registered with `services.AddQahwaPatch<T>()`. Names are lowercase `[a-z0-9._-]`, unique and never changed (a date prefix such as `2026_03_01_fill_group` gives the order). A bad or duplicate name stops the server at start-up.
- **The Patch Log is the record**, as a Core DocType (name = patch name; Running, Done or Failed; start, end, error text), readable and deletable by System Managers, not editable by anyone. A patch is **claimed by inserting its row**: the name is unique, so when several servers start together exactly one runs it and the others skip it (tested with five concurrent runners on PostgreSQL).
- **Order and failure.** Pending patches run in name order at start-up, after DocTypes, customizations, rules and workflows are loaded. A failing patch is recorded as Failed with its error, **stops the run** (later patches may rely on it) and stops the server's start-up: carrying on with half-migrated data is worse than not starting. It is tried again on the next start (delete the row to force a re-run of a Done patch).
- **Crashes.** A patch claimed but never finished (the server died) is taken over after 30 minutes; a patch that is still within that time is left alone.
- **Write patches to be safe to repeat**, since an interrupted run is repeated from the start.

## Consequences
- **Looking at and re-running patches** (added later): `PatchRunner.ListAsync` lists every registered patch with its state (Pending, Running, Done, Failed, plus logged ones that no longer exist); `RunOneAsync(name, force)` runs one now. A pending or failed patch just runs; a Done one needs `force` (it runs again, so it must be safe to repeat); one that another server is running right now is refused even then. Over HTTP (`GET /api/patches` for System Managers, `POST /api/patches/{name}/run` for Administrators only, never with an API key) and in the Desk as a "Run this patch again" button on the Patch Log form. On the command line, `qahwa list-patches` and `qahwa run-patch <name> [--force]` work with the patches the command line carries (the framework's own); patches that belong to apps are registered by the server, so run those from the Desk.
- Not yet: ordering of patches across apps (with app dependencies).
