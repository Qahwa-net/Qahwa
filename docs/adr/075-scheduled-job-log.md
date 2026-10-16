# ADR-075: Scheduled Job Log

Status: accepted

## Context
The scheduler (ADR-029 and later) does many things in the background, but the only trace of a problem was the Error Log, and a successful run left no trace at all. Administrators could not tell whether the nightly backup, the emailed reports or an app's job had run.

## Decision
- **`Scheduled Job Log`** (a Core DocType; System Managers can read and delete rows) gets one row each time a scheduled step did something or failed: the job's name, Success or Failed, what happened (or the error message), and how long it took in milliseconds. A quiet tick that found nothing to do writes nothing, so the log is not filled with noise.
- **Every scheduler step goes through one helper** (`SchedulerService.StepAsync`): it times the step, logs a result if the step reports one, and on an exception writes the Error Log and a Failed row and carries on with the other steps. An app's scheduled job (ADR-062) is logged on every run, with its outcome.
- Message text from jobs is cut to 500 characters and has control characters replaced.
- **Retention:** rows older than `Qahwa:JobLog:RetentionDays` (default 30) are removed by the hourly clean-up. Failing to write the log never stops a job.
- Desk: "Scheduled Job Log", "Error Log" and "Deleted Documents" are linked from the Tools section for people who can design.

## Consequences
- There is no queue of pending or retried jobs to look at; the scheduler is a loop that runs due work (safe on several servers), and the log is the history of what it did.
- Rows are written per site's database by whichever server ran the step.
