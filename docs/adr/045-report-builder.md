# ADR-045: Report Builder

**Status:** accepted (L9, part 4)

## Context
The list view can filter and sort, and a single summary can be made on the fly, but people need to *save* a report (columns, filters, order, or a summary with several numbers), share it, run it again later and download it.

## Decision
- **A report is data.** The Core DocType `Report` holds its title, whether it is shared, and its definition as JSON: the DocType and either a **list** (up to 20 columns, up to 10 filters, an order, up to 1000 rows) or a **summary** (a field to group by and up to five numbers: count, total, average, smallest, largest). Reports are private to their owner or shared with everyone signed in; System Managers see all.
- **Run for the person looking.** Running a report never uses the author's rights. A list goes through the normal list rules (read right, User Permissions, field permission levels: asking for a field the viewer may not read is refused with a reason, never quietly shown); a summary needs the **report right** and is worked out from the rows the viewer may read; a download of a list needs the **export right**. Sharing a report therefore cannot widen anyone's access. The definition itself is visible to everyone it is shared with (it names fields and filters, nothing more).
- **Checked every time.** Saving and running both validate the definition against the DocType as it is now: DocType and fields exist, no password fields, operators from a fixed set (`= != > < >= <= like in is`), values are plain or a list, limits are enforced. Values reach the query only as parameters. A saved report whose DocType later lost a field fails with the field named, instead of showing something wrong.
- **Summary with several numbers** is `DocumentService.GroupedAsync`, one pass over the readable rows (same cap as exports) with columns keyed `count`, `sum_amount`, …
- **Download** as CSV with the column labels as headers (spreadsheet formula injection is neutralised by the shared CSV writer); a "truncated" flag is returned when the cap was hit.
- **Builder** in the Desk: pick the DocType (a searchable list), the kind, columns or group and numbers, filters, order; **Preview** runs the unsaved definition with your own rights; **Save**. Saving uses the `modified` value, so two people editing the same report get a conflict instead of overwriting each other.

## Consequences
- Not yet: reports across several DocTypes (joins), calculated columns, grouping by two fields, charts from a report (dashboards have charts), scheduled email of a report, printing a report as PDF.
