# ADR-140: Events

**Status:** accepted (process automation)

## Context
The Desk already has a calendar view for any DocType with Date or Datetime fields, and reminders (ADR-139) can be set on any document. What was missing was a DocType for a person's own dated entries.

## Decision
- `Event` is a framework DocType in the module **Calendar**: subject, starts on, ends on, all day, status (Open, Completed, Cancelled, coloured blue, green, grey), location and a formatted description. It names itself `EVENT-.#####`, sorts by start (earliest first) and opens in the calendar view.
- **Private by default, shareable.** The permission rules are the same as for Note: every signed-in person (the `All` role) may create events and read, change and delete their own (`ifOwner`); System Managers may do everything; sharing a document gives others access.
- A rule refuses an event that ends before it starts (`!ends_on || ends_on >= starts_on`, on the Ends on field).
- Nothing new is needed for the calendar, the reminder bell, Share, Repeat or the notification bell: Event is an ordinary DocType (not in the Core module, so the document toolbar offers Remind me and Repeat).
- **A fix found on the way:** `NotificationService.ContextOfAsync` built a person's roles from their Has Role rows only, without the implicit `All` role a real session has. A notification or reminder about a document readable only through `All` (like an Event or a Note) was therefore silently dropped. It now adds `All`, like `AuthService`.

## Consequences
- No attendees, invitations, recurrence or time zones of their own: times are the Datetime values the engine keeps (UTC).
- Not yet: Milestones (tracking when a field of a document reaches a value), invitations and free/busy.
