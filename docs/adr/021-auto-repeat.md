# ADR-021: Auto Repeat and the scheduler

**Status:** accepted (L8, part 3)

## Context
Recurring work (a monthly invoice, a weekly checklist, a yearly renewal) should not depend on someone remembering to copy last time's document. The framework had no background work at all, so this also introduces the scheduler.

## Decision
- **A repeat belongs to one document.** It is a row of the Core DocType `Auto Repeat` (reference DocType and name, `frequency` Daily/Weekly/Monthly/Quarterly/Yearly, `start_date`, optional `end_date`, `next_date`, `occurrence`, `status` Active/Paused/Finished, `date_fields`, and what happened last). Rows are written only by `AutoRepeatService`, never through the generic API. A document may repeat in at most 5 ways, a person may have at most 100 repeats; deleting or renaming the document takes its repeats along (the same hooks as assignments and follows).
- **Dates are counted from the start date** (`AutoRepeatSchedule.On(start, frequency, n)`), so a monthly repeat starting on the 31st gives 28 February, then 31 March again, never drifting. All dates are UTC dates. The start date cannot be in the past.
- **Copies are made as the person who set the repeat up.** The copy is an ordinary insert by that person (`DocumentService.InsertAsync`): their create permission, User Permissions, validation, naming series, workflow (a copy starts in the first state) and assignment rules all apply, and hidden fields they cannot read are not copied. It is a draft with its table rows copied; unique fields and formulas are left for the new document to fill; the chosen **date fields** are set to the date of the copy. Setting a repeat up needs read access to the document and create access to its DocType. If the person has been disabled or lost access the repeat is **paused** with the reason on the row; other failures (validation) are recorded and the schedule carries on.
- **The scheduler** (`SchedulerService`, a hosted background loop in the server, `Qahwa:Scheduler:Enabled`, `IntervalSeconds` 10 to 3600, default 300) calls `AutoRepeatService.RunDueAsync`. Each due occurrence is **claimed first** with a compare-and-swap on the row (`modified`), so several servers (or overlapping runs) never make the same copy twice. A scheduler that was down makes at most 5 late copies per repeat per run and catches up on the next ticks. After the last date before `end_date` the repeat becomes Finished.
- **Create now** makes the next copy immediately (dated today) and uses up that occurrence; the schedule carries on after it.
- **Desk.** A Repeat icon on the form opens a dialog (existing repeats with make-now, pause/resume, stop; a form for a new one with frequency, first date, last date and which date fields to move), and a "My work, Auto Repeat" page lists everyone's repeats (a System Manager sees all).

## Security
Only the owner of a repeat, or a System Manager, can see, change, run or delete it (others get "not found"). Setting one up is checked against the caller's read and create rights, and every copy is created with the owner's rights, never the system's, so a repeat cannot be used to create what the person could not create by hand. Dates and frequencies are parsed strictly and checked against fixed lists; date field names must be Date or Datetime fields of the DocType; all values are parameters. Failure text is trimmed to 500 characters and shown as text.

## Consequences
- Not yet: repeating with an email to a customer, a "weekdays only" or "every N days" frequency, per-repeat time of day, copying linked documents, and other scheduled job types (the scheduler is ready for them).
- Notification rules and email templates are the next L8 parts.
