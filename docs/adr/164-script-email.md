# ADR-164: Email from server scripts, and the script examples for Project Management

**Status:** accepted

## Context
Server scripts (ADR-053, ADR-108) could read documents and write documents, but could not tell anyone: the Email Queue is not writable by scripts' users, and a "reminder when a task is due" needed either a notification rule or an app. Developers asked for worked examples of server and client scripts, and whether scripts can read the database.

## Decision
- **`qahwa.email(to, subject, message, doctype?, name?)`** in the script sandbox queues one plain-text email through the existing queue and sender. Limits and safety:
  - the recipient must be an **enabled User** of the site (the address is matched to the account; strangers, disabled accounts and non-addresses are refused), so a script cannot be used to mail outsiders;
  - when a document is named the acting person must be able to read it, and the message is linked to it (`reference_doctype`, `reference_name`);
  - it counts in the acting person's **daily limit of 50 emails** and, per run, **at most 20** (its own counter, separate from the 5 writes);
  - it is only allowed where writes are (After events, API, Scheduled); Validate and Before events refuse it;
  - the subject and message limits of the queue apply.
- **Example scripts** ship with the Project Management app in `apps/Qahwa.ProjectManagement/Scripts/` (four server scripts, two client scripts) and are run by tests: the tasks box on the project, the daily due-date email, an API of overdue tasks, and the two forms' helpers. The `Pm Project` DocType gains a read-only **Tasks in this project** field (`task_overview`) for the first.
- **Docs:** a developer chapter (*Server scripts and client scripts*) with the question "can scripts read the database?" answered (documents, as the acting person, no SQL; client scripts cannot read anything), the create steps, the full sandbox reference, limits, examples and pitfalls, and a user guide page to install the example set.

## Consequences
- Tested: `ScriptEmailTests` (7), `ProjectManagementScriptTests` (5) and the example client scripts run through the real sandbox worker (8). Seen in the running app on PostgreSQL: a saved task refreshed its project's box; the reminder code queued an email linked to the task; the API script answered; the form banner showed.
- A no-code route stays the first choice for a plain reminder: a Notification Rule (*days before a date*, email) has no per-run cap.
- Not done: emailing addresses that are not users, HTML mail, attachments from scripts, After Delete events (the Tasks box is not refreshed on a delete), a script version history.
