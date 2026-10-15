# ADR-031: Board and calendar views

**Status:** accepted (L12, part 1)

## Context
A list is the right view for searching, but people also think of work as columns (what is open, working, done) and as days (what is due when). These views must not need new server code or new permissions: they should be other ways to look at, and change, the same documents.

## Decision
- **Pure Desk views over the existing list API.** `BoardView` (`/app/<DocType>/board`) and `CalendarView` (`/app/<DocType>/calendar`) call `api.list` and `api.update`; the server, its permission checks, validation, workflow and hooks are used unchanged, so a board can never do something the form could not.
- **Board.** One column per choice of a Select field (chosen with `?by=`, default the DocType's status field), then columns for old values still found in the data, then "No value". Moving a card sets the field through a normal update. The card moves at once and is put back, with the server's message shown, if the update fails (permission, validation, a conflict). Without write access the board is read-only. Drag and drop is backed by a "Move to" select on every card so it works with a keyboard and on touch.
- **Calendar.** A month grid (six weeks, weeks start on Monday) of one Date or Datetime field (`?by=`, month in `?month=YYYY-MM`). The query filters `field >= first day` and `field < first day of next month`; days are taken from the stored date in UTC like the server. Three documents show per day with "+N more".
- **Bounded.** At most 500 documents are loaded and a banner says so when there are more.
- **Safe.** All values are rendered as React text (escaped); field names come from the DocType's metadata and the list API whitelists them; links encode document names.

## Consequences
- No migration, no new endpoint and no new permission.
- Not yet: Gantt, map and image views; saving the chosen field as a user setting; dragging inside a column to order cards; a week or day calendar.
