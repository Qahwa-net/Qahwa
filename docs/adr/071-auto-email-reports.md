# ADR-071: Reports emailed on a schedule

Status: accepted

## Context
Reports (ADR-045) can be opened and downloaded, but people want the numbers to arrive by email every morning. The email queue (ADR-029) already sends text email.

## Decision
- **Auto Email Report** is a schedule: a saved report, up to 10 recipients, Daily, Weekly or Monthly, an hour (UTC), how many rows (1 to 200, default 50), an optional subject, and **whose permissions it runs with**. The scheduler runs due ones and queues one plain-text email per recipient: the report title, a header line, the first rows as ` | `-separated lines (cells cut at 60 characters, control characters replaced), and a line saying how many rows there are in all. The email queue sends them like any other email.
- **Only a System Manager can set these up** (the Desk page is in Tools; `GET/POST /api/auto-email-reports`, `PUT/DELETE /{id}`, `POST /{id}/send`). A report sent to other people is data leaving the system, so it is not left to every report author.
- **The report runs as a real, enabled user from the user list**, with the roles they hold there: the person chosen when it is set up is checked (the report must exist and be one that user may open) and is used again every time it runs. If that user is later disabled, the send fails with a message on the schedule ("last problem") and nothing is sent; it does not fall back to wider rights.
- **Recipients are checked strictly:** plain addresses only (no display names, spaces, control characters or line breaks, so a recipient or subject cannot add headers), at most 10, no repeats.
- **No double sends and no flood after downtime.** Each due occurrence is claimed with a compare-and-swap on the row before sending (two servers never send the same one), and the next time is moved past now, so a server that was down for ten days sends once, not ten times.

## Consequences
- The email is plain text (no attachment, no HTML) until the email queue supports them; a spreadsheet is still one click away in the Desk.
- Times are UTC.
- Email Digest (a summary of a person's own notifications) is not part of this.
