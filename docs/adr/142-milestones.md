# ADR-142: Milestones read from the history

**Status:** accepted (process automation)

## Context
People want to see how a status moved: when a ticket was opened, who started it, how long it waited. Frappe keeps a separate Milestone table written by a hook. Qahwa already keeps the field-level history of every document (Version, ADR-067), in the same transaction as each change.

## Decision
- A **Milestone Tracker** (a framework DocType, System Managers only) names one field of one DocType (one tracker per DocType). On save it is checked: the DocType exists, has documents of its own (not a child table or settings page) and keeps its updates log, and the field exists and holds a plain value (not Password, JSON, a signature or a file).
- **Nothing is stored for milestones.** `MilestoneService` reads the document's Version rows (Updates only), takes the entries for the tracked field, and builds the steps: the value the document started with (the earliest change's "before", or the current value when it never changed) at its creation time and by its creator, then each change with who made it and the seconds since the step before. A change to the same value is not in the log, so it is not a step.
- **It cannot show more than the reader may read.** The document is read through the normal engine first (so an unreadable document is an error, an anonymous caller is refused) and the tracked field must be readable under the reader's field levels; otherwise the answer is null and the Desk shows nothing.
- `GET /api/resource/{doctype}/{name}/milestones` answers `{ field, label, items: [{ value, at, by, afterSeconds }] }` or null; the Desk form shows it as a strip above the activity ("Status so far") for documents that are not framework documents.

## Consequences
- The milestones are exactly as complete as the log: nothing before the DocType kept a log, and at most the last 1000 updates of a document are read (and the last 100 steps shown).
- Because the log is the source, there is no way to backfill, edit or lose a milestone separately from the history, and no second place to keep correct.
- Not yet: milestones for more than one field of a DocType, a report of how long documents spend in each step across all documents, and notifications on reaching a value (use a notification rule or a workflow for that).
