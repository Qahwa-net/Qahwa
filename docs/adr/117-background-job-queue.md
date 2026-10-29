# ADR-117: Background job queue

Status: accepted. Replaces the "no job queues" limit of ADR-107.

## Decision
An app registers a handler with `Queue(name, handler)` (it becomes `app.name`) and queues work for it with `BackgroundJobService.EnqueueAsync(handler, data, queue, delay, maxAttempts)`. The work is stored in the database (**Background Job**), so it survives a restart and needs no message broker.

- **Three queues**, each with its own time limit and batch size per scheduler pass: `short` (30 seconds, 20 jobs), `default` (5 minutes, 10) and `long` (30 minutes, 2). Short work is looked at first.
- **Retries:** a failure is tried again after 30 seconds, doubling, at most an hour, up to the job's most attempts (1 to 10, default 3); then it is Failed. A System Manager can retry a failed job (`POST /api/background-jobs/{name}/retry`).
- **Safe on several servers:** a job is claimed with a compare-and-swap on its row, so it runs on one server at a time. A job left "Running" after a crash (past its queue limit plus a minute) is queued again, so a handler **must be safe to run twice**.
- **Limits:** the data is at most 16 KB of JSON, a delay at most 30 days, a handler runs under its queue's time limit and is cancelled when it passes. A handler that is missing or whose app is switched off fails clearly. Error text is cleaned of control characters and cut to 500 characters.
- **Visibility:** each run is written to the Scheduled Job Log; a failure also goes to the Error Log; Background Jobs are listed under Tools (System Managers only, read-only: nobody edits one by hand). Done jobs are removed after a week, failed ones after a month.
- Handlers get the data, the attempt number, the services and a cancellation token, and run as the system, not as a person: they must do their own permission checks if they act for someone.

## Not built
Priorities inside a queue, scheduled (cron) enqueueing (use `Schedule`), and queues between separate programs.
