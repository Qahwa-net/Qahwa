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
- Not yet: running a single patch from the command line, patches that belong to an installable app (with app dependencies and ordering across apps), a Desk button to retry.
