# ADR-022: Notification rules

**Status:** accepted (L8, part 4)

## Context
People need to hear about the things that matter to them without watching every document: a large claim was submitted, a contract ends in a week, an invoice is overdue. That is configuration an administrator should own, as data, with no code.

## Decision
- **Rules belong to a DocType**, stored as one JSON row per DocType in `__notification_rule` (same store, manager, catalog and `modified` conflict check as workflows and assignment rules). Up to 20 rules per DocType, for ordinary DocTypes (not child tables, settings or the framework's own).
- **Events.** `New`; `Update` (only when a saved change really altered a column: saving the same values is not an update); `ValueChange` (one chosen field changed); `Submit` (docstatus 0 to 1) and `Cancel` (1 to 2), including when a workflow does it; `DaysBefore` and `DaysAfter` a Date or Datetime field (0 to 365 days). An optional `condition` (the expression language used everywhere, over the saved document) narrows any rule.
- **Recipients.** Named users, everyone holding named roles, the users named in chosen fields of the document (Link to User, Data or Email fields), the document's owner, and its followers; at most 200 people per firing, each told once. The person who caused the change is never told about their own action.
- **Message.** One line of text with `{{field}}` placeholders (plus `{{name}}`, `{{doctype}}` and `{{user}}`, the person who did it). Only plain names are replaced: there is no code, no filters, and replaced text is never expanded again. Values are formatted fixedly (yes/no, ISO dates), control characters become spaces and each value is cut at 200 characters. The subject is stored on the notification and shown as text. Placeholders are checked against the DocType when the rules are saved.
- **Delivery** is the in-app Notification Log (type `Alert`), through the same `NotificationService.NotifyAsync` as every other notification: a person is only told if they are an enabled user **and may read the document**, so a rule can never leak a document to someone who could not open it.
- **Time-based rules** run from the scheduler (at most once an hour). A notification written by a rule carries the rule name and a key (the day), and a person is not told twice for the same rule, document and key, so repeated and concurrent runs, and several servers, are safe.
- **A rule never breaks a save.** The document is saved first; if telling people fails, the error is swallowed and the next change tries again.
- **Desk.** A Notification Rules page (`/notification-rules`, System Managers): per rule an event with its settings, condition, people and roles (search boxes), user fields, owner and followers, and a message with an "Insert a field" list and a warning for unknown names; errors appear under the rule they belong to.

## Security
Only System Managers manage rules. Everything stored is validated when saved: events and fields come from fixed lists or the DocType, conditions parse with the safe expression language and may only name the DocType's fields, users and roles must exist, counts and lengths are limited, and malformed placeholders are refused. Read access of every recipient is checked at send time. Template filling is text substitution only and the result is shown as text. Tests cover forged-looking field values (`{{name}}`, markup, newlines, very long text), recipients without access, anonymous and non-manager callers, stale saves and a restart.

## Consequences
- Not yet: other channels (email, Slack, SMS, push) and email templates (there is no email infrastructure yet), a per-person "mute", digests, attachments, and a test button that previews a rule on a real document.
- Email accounts, templates and the queue are the next L8 parts.
