# ADR-081: Access Log for exports and printouts

Status: accepted

## Context
Exporting a list or printing a document moves data out of the system. Administrators need to know who did it, to what and how much.

## Decision
- **`Access Log`** (Core DocType; System Managers read and delete; nobody can create a row by hand) gets one line each time an export or a printout succeeded: the person, **Export** or **Print**, the DocType, the document (for a print), and the number of rows (for an export). Report downloads are exports and are recorded too.
- Lines are written only **after every permission check passed**, so a refused attempt leaves nothing. Writing the line never stops the export or print (a failure is swallowed).
- Lines older than `Qahwa:AccessLog:RetentionDays` (default 365) are removed by the hourly clean-up.
- Desk: "Access Log" is a normal list for System Managers (search or Tools).

## Consequences
- It records that an export happened and its size, not its content or the filters used.
- Print previews in the format editor are not recorded (they are not a document leaving).
