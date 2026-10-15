# ADR-023: Email queue, templates and sending

**Status:** accepted (L8, part 5)

## Context
Notifications should be able to reach people outside the Desk, and people should be able to email about a document without leaving it. A mail server is slow and sometimes down, and a framework that sends mail is a target for spam and header injection.

## Decision
- **A queue.** Every email is first a row of the Core DocType `Email Queue` (recipient, subject, body, status Pending/Sending/Sent/Failed, attempts, last error, the document it is about). Nothing is sent while a person waits. System Managers can see and delete rows in the ordinary Desk list.
- **The scheduler sends.** `EmailService.SendPendingAsync` runs on every scheduler tick (default 5 minutes). Each message is **claimed** first with a compare-and-swap on the row's `modified` (always changed by the claim, even on a stalled clock), so several servers never send it twice. A failure puts it back for another try, three attempts in all, then `Failed` with the reason. A message left in `Sending` for 10 minutes belongs to a server that died and is taken over.
- **Sending.** `IEmailSender` is the only thing that touches a mail server. `SmtpEmailSender` uses `Qahwa:Email` (`Host`, `Port`, `UseSsl`, `User`, `Password`, `From`) from configuration; without a `Host`, `LogEmailSender` only logs what it would have sent, so development never emails anyone. Credentials stay in configuration or secrets, never in the database.
- **Plain text only.** Messages are `text/plain`, so there is no HTML to inject or sanitise.
- **Templates.** The Core DocType `Email Template` (System Managers edit it in the ordinary Desk): a subject and a message with `{{field}}` placeholders, optionally for one DocType. Filling uses the same `MessageTemplate` as notification rules: plain names only, values formatted fixedly, never expanded twice, nothing beyond the document's own fields.
- **From a document.** `POST /api/resource/{doctype}/{name}/email` (and `/email/preview` for a template): the person needs read access, and write access to email about it. Up to 10 addresses, a single-line subject of up to 200 characters, a message of up to 20 000, and **50 emails per person per day**. The activity timeline records "Emailed x@y".
- **By notification rule.** A rule can tick "Also send an email": the people it notified (only those, so only people who may read the document and who were not told already today) also get the message by email. Their user name is their address.
- **Desk.** An Email icon on the form opens a dialog (addresses with live validation, template picker that fills subject and message, subject, message).

## Security
Addresses must be plain `name@host.tld`: no display names, spaces, quotes, commas or control characters (so one field can never become several recipients or extra headers), and the subject is forced onto one line. The per-person daily limit and recipient cap stop the server being used as a relay. Sending needs write access to the document, and rules email only people who were allowed to be notified. The queue is only readable by System Managers; templates cannot read anything but the document they are filled from. Tests cover header-injection addresses and subjects, template escapes, permissions, limits, retries and overlapping schedulers (also on Postgres).

## Consequences
- Not yet: incoming mail (IMAP/POP), HTML emails and attachments, bounce handling, several outgoing accounts per user or DocType, unsubscribe links, and a queue page with a "retry now" button (the ordinary list works for now).
- Print formats (PDF) and a report builder are in L9; emailing a PDF of a document will build on both.
