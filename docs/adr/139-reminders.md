# ADR-139: Reminders

**Status:** accepted (process automation)

## Context
People want to come back to a document later (renew, chase, check). Auto Repeat makes copies and notification rules fire on document dates, but nothing lets a person say "tell me about this one on Friday".

## Decision
- A **Reminder** is a row of the framework DocType `Reminder`: the document (DocType and name), the time (`remind_at`, UTC), an optional note (up to 500 characters), a status (Pending, Sent, Cancelled) and when it was sent. It is made from the form (bell button, quick times or a typed time) through `POST /api/resource/{doctype}/{name}/reminders`; `GET` lists the caller's reminders for the document, `GET /api/reminders` lists everything still waiting, `DELETE /api/reminders/{id}` cancels one.
- **A reminder is about a document the person can read**, checked when it is made (otherwise it answers "not found") and again when it is due, through `NotificationService`, the same gate every notification passes. A document that was deleted or became unreadable means nothing is sent, so a reminder never reveals a document.
- **Only the owner sees or cancels a reminder.** To anyone else it does not exist (404). Limits: the time is in the future and at most 366 days ahead, 100 waiting reminders per person, notes up to 500 characters.
- **Sent once, on any number of servers.** The scheduler step `reminders` takes the due rows (soonest first, 200 at a time) and claims each with a compare-and-swap on the row (Pending to Sent) before writing the notification; a server that loses the swap skips it. The notification (type `Reminder`, added to the Notification Log types) is in the inbox and pushed live like any other.
- A time sent without an offset is read as UTC; the Desk sends the instant the person's own clock means.

## Consequences
- A reminder arrives at the next scheduler run after its time (every five minutes by default), not to the second.
- The notification is a Notification Log row only; there is no email or push to a phone yet (notification email preferences, when they exist, would apply).
- Not yet: reminders on things that are not documents, repeating reminders, snooze, Events (a calendar of dated entries) and Milestones.
