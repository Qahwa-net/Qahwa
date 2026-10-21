# ADR-108: API scripts

Status: accepted

## Decision
A **Server Script** with the event **API** (and a *method name*) is a small endpoint written in the sandbox language (ADR-070). A signed-in person calls it with `POST /api/script-method/{method}` and a JSON object; the object arrives in the script as `doc`, and whatever the script assigns to `result` is the answer (`{"data": …}`). The script runs with the same limits as any script (statements, time, memory, depth, reads and writes per run, size of the answer) and may use `qahwa.get/list/insert/update/error/log`.

Safety:
- The caller must be signed in; **everything the script reads or writes is checked against the caller's own permissions**, so a script is never a way past them. An optional role limits who may call it.
- Only System Managers can create or change scripts. A method name is plain lower-case letters, digits and underscores (so it cannot be a path or a query). The request body is limited to 100 KB and must be a JSON object.
- A script cannot call another API script, and the first active script with a method name wins (give each its own name).

## Not built
Permission-query scripts: they would make who-sees-what depend on code, which the declarative permission rules and user permissions (ADR-047) are meant to avoid.

## Scheduled scripts
A Server Script with the event **Scheduled** and a frequency (Hourly or Daily) is run by the scheduler (step "scheduled scripts" in the job log). It runs **as the person who made it, with their permissions**; if that account is missing or disabled the script is not run and the reason is shown on it (Last problem). Each occurrence is claimed with a compare-and-swap on the row, so several servers never run it twice, and runs missed while the system was off happen once. A script is only scheduled the first time the scheduler sees it, not run. The script has no `doc`; it uses `qahwa.get/list/insert/update/log` with the same limits as any script (statements, time, memory, reads and writes per run). Writes it makes do not start other scripts. At most 50 scheduled scripts run.
