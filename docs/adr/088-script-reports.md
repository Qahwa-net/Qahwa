# ADR-088: Script Reports (reports written in code by apps)

Status: accepted

## Context
The report builder (ADR-045) covers lists and grouped summaries of one DocType. Real reports sometimes need joins, calculations or data from elsewhere. Frappe offers "Query Reports" (SQL typed into the browser) and "Script Reports".

## Decision
- **Script Reports only, written by apps, not typed in the Desk.** An app registers a report in code (`IAppBuilder.ScriptReport(name, title, handler, options)`); the handler returns columns and rows. **There is no SQL box**: SQL typed by an administrator bypasses document permissions, field levels and User Permissions, and a mistake or a stolen admin session reads the whole database. A report that ships in an app is reviewed and versioned like the rest of the app, and it reads documents through the supplied `DocumentService` as the person running it, so it shows no more than they may see.
- Listed at `GET /api/script-reports` and run with `POST /api/script-reports/{id}/run` (`{ filters: { name: text } }`), id `app.name`. `ScriptReportOptions.Roles` restricts who sees and may run it (Administrators always can); a report someone may not run looks like one that does not exist (404). Only the filters the report declares are passed on (text, at most 200 characters).
- **Limits and safety:** at most 5,000 rows, 50 columns, 1,000 characters per cell (excess is cut; the result says when rows were); column keys must be unique; cells are reduced to plain data (text, numbers, yes/no, ISO dates). If the report throws, the error goes to the Error Log with the app's name and the person sees "The report could not be run" (no internals). A refusal made by the document service (permission) is shown as usual.
- Each run is recorded in the Access Log (ADR-081) as an export with the number of rows.
- Desk: a "Script reports" page (Reports menu): pick a report, fill its filters, run, download CSV.

## Consequences
- Reports cost a deployment (the app) to change. For quick one-off questions use the report builder.
- A report can still be slow; there is no timeout beyond the request's own.
