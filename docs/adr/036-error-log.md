# ADR-036: Error log

**Status:** accepted (L12, part 4)

## Context
When something unexpected breaks, people see "an unexpected error occurred" (by design: details can help an attacker) and the details go only to the process log, which a business user or System Manager often cannot read. Problems in the background scheduler are invisible.

## Decision
- **A Core DocType `Error Log`**, readable and deletable by System Managers only, never writable by people (rows are written by the service).
- **What is recorded:** where (Request or Scheduler), HTTP method, the address **without query string or fragment**, the signed-in user, the kind of error, its message (1000 characters) and stack trace (8000). **Never** the request body, headers, cookies or query string, which can hold passwords, tokens or card numbers.
- **Only unexpected errors** (the ones that become a 500). Expected ones (validation, permissions, not found, conflicts) are normal answers, not log entries.
- **Cannot hurt:** at most 30 entries a minute and 500 an hour (the process log still has everything); recording is wrapped so it can never throw or turn one error into another.
- **Scheduler failures** (auto repeat, email, webhooks, notification rules) are recorded as source "Scheduler".
- The visitor still gets the same generic message. The log is for the people who fix things; the message and stack can contain internal details (for example a database message with a value in it), which is why only System Managers can read it.

## Consequences
- A manager can see what failed and where without server access.
- Not yet: grouping repeated errors, a retention setting (delete old rows by hand or with bulk delete), a scheduled-job history page, alerts.
