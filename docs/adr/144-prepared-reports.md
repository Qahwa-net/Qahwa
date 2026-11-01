# ADR-144: Prepared reports

**Status:** accepted (reports)

## Context
A live report run is capped (1,000 rows) and holds a request open. A report over many documents, or with a calculation, is better built in the background and read later. The scheduler already runs imports this way (ADR-130 and neighbours).

## Decision
- A **Prepared Report** is a row (framework DocType, read-only to System Managers): the report, its title, a status (Queued, Running, Done, Failed), timings, the row count, whether it was cut short, a message, and the result (a JSON table of header texts and rows, permission level 9 so no one reads it through the generic API). A person asks for one with `POST /api/reports/{id}/prepare` (202); `GET /api/reports/{id}/prepared` lists theirs; `GET /api/prepared-reports/{id}` returns one with its rows; `DELETE` removes a finished one.
- **It is built as the person who asked**, resolved again when it runs (`ContextOfAsync`), by the same code as the report's download (`ReportService.ExportAsync`). So every permission, field level and the export right apply exactly as for a download, and a prepared report is not cut at the live run's 1,000 rows. A person who is no longer an enabled user, or has lost access, gets a Failed run with the reason.
- **Safe on several servers, and again after a crash.** The scheduler step takes the oldest Queued row and claims it with a compare-and-swap (Queued to Running). A run that stays Running for 30 minutes is marked Failed ("prepare it again"); a report is built from scratch each time, so nothing is half-done.
- **Limits:** 3 waiting or running per person (asking again for the same report returns the waiting one), a result of at most 8 MB, and finished results are deleted after 7 days by the same scheduler step.
- The Desk report page has a **Prepared versions** panel: ask for one, see it move from Waiting to Building to a row count (it looks again every 3 seconds while any is pending), open it (first 500 rows) or download all rows as CSV (formula-safe cells).

## Consequences
- A list report needs the export right to be prepared, like its download; a summary report only needs the report right.
- The result is kept in the database as text: 8 MB per prepared report and 7 days bounds the space.
- Not yet: notifying the person when it is ready (the page refreshes itself, and a notification needs a readable document to be about), scheduled preparation (use Auto Email Report for a regular report by email), and Excel from a prepared report.
