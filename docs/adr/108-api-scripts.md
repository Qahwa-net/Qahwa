# ADR-108: API scripts

Status: accepted

## Decision
A **Server Script** with the event **API** (and a *method name*) is a small endpoint written in the sandbox language (ADR-070). A signed-in person calls it with `POST /api/script-method/{method}` and a JSON object; the object arrives in the script as `doc`, and whatever the script assigns to `result` is the answer (`{"data": …}`). The script runs with the same limits as any script (statements, time, memory, depth, reads and writes per run, size of the answer) and may use `qahwa.get/list/insert/update/error/log`.

Safety:
- The caller must be signed in; **everything the script reads or writes is checked against the caller's own permissions**, so a script is never a way past them. An optional role limits who may call it.
- Only System Managers can create or change scripts. A method name is plain lower-case letters, digits and underscores (so it cannot be a path or a query). The request body is limited to 100 KB and must be a JSON object.
- A script cannot call another API script, and the first active script with a method name wins (give each its own name).

## Not built
Scheduler-event scripts and permission-query scripts. A scheduled task can be written as an app job (ADR-075), and permission queries would make who-sees-what depend on code, which the declarative permission rules and user permissions (ADR-047) are meant to avoid.
