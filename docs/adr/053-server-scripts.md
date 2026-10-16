# ADR-053: Server Scripts

**Status:** accepted (L11, part 2)

## Context
Formulas, rules and Document Actions (ADR-035) cover common cases, but some business rules need real logic: "refuse this order if the customer's open balance is over the limit", "grade this crate by weight and create a check". The plan called for a Roslyn C# sandbox. C# scripts cannot be made safe in process (they run on the full runtime and cannot be reliably limited in time, memory or reachable types), whereas an interpreter that has nothing but its own language can be limited completely.

## Decision
- **JavaScript run by Jint**, a managed interpreter with no access to .NET types, files, network, timers, processes or `require`. It is created fresh for each run (no state carries over), in strict mode.
- **Limits per run:** 100,000 statements, 1 second (also for regular expressions, so a catastrophic pattern is stopped), 8 MB of memory, 100 levels of calls, 20 reads and 5 writes, 200 KB of data passed either way, 50 log lines of 500 characters, a script of 20,000 characters, at most 10 scripts per DocType and event. Hitting a limit stops the script with a plain message.
- **What a script sees:** copies of `doc` and `old` (tables and Password fields are left out), `isNew`, `event`, `user` (`name`, `roles`) and a frozen `qahwa` object: `error(message)`, `log(...)`, `today()`, `now()`, `get(doctype, name)`, `list(doctype, {fields, filters, orderBy, descending, limit})`, `insert(doctype, values)`, `update(doctype, name, values)`. Everything goes through `DocumentService` **as the person who made the change**, so permissions, restrictions, validation and naming apply as for them; a script cannot do what they could not. `qahwa` cannot be replaced from inside.
- **Two events.** *Validate* runs before a save (after formulas and "fetch from", before validation), can change fields and refuse with `qahwa.error`; it cannot write other documents. The fields it sets pass through the normal value conversion and validation; system fields, unknown fields, tables and passwords cannot be set. *After Save* runs once the document is stored and can read and write other documents; its changes to this document are not applied.
- **No loops:** a change made by a script does not start other scripts (an async-local guard, like Document Actions).
- **Failure rules.** A *Validate* script that fails or runs out of limits stops the save ("a rule that cannot run cannot be trusted to hold") and the problem is written on the script (`last_error`) so it can be fixed or switched off; saves work again as soon as it is fixed. An *After Save* script that fails never undoes the save; the problem is written on the script.
- **Who:** the Server Script DocType is for System Managers only. The source is parsed when saved, so mistakes are reported at once. `POST /api/server-scripts/test` (System Manager) tries a script on a sample document without saving or writing anything, returning the changes and the log.
- Scripts are cached for 15 seconds, and a save or delete on the same server takes effect immediately.

## Consequences
- Not yet: scripts for API endpoints, scheduled events, permission queries or custom buttons; scripts on submit/cancel/delete; calling other scripts; client-side scripts (the plan's Client Script).
- The interpreter is synchronous, so a script's reads wait on the database on a pool thread; the 20 reads and the 1-second limit bound this.
- Jint is a pure interpreter: scripts are slower than C# but bounded, which is the point.
