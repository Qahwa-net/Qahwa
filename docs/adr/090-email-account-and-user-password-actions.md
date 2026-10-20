# ADR-090: Email Account (SMTP) and password actions for System Managers

Status: accepted

## Context
Outgoing email was configured only in host settings, and without a host the system "sent" mail by writing it to the log, which looked like success. Managers had no way to set a person's password or send them a reset link (a new user had no way to choose a first password), and the sign-up and reset links needed a public address that could only come from host settings.

## Decision
- **Email Account** is a Core single DocType (System Manager only): sender address, SMTP server and port, STARTTLS switch, user name, password and the **public site URL** used in email links. The SMTP password is a Password field (ADR-066): encrypted at rest, masked in the API. Port 465 (implicit TLS) is not supported; a rule asks for a password whenever a user name is given.
- **One transport, read on every send.** `SmtpEmailSender` asks `EmailAccountService` each time, so a saved change applies without a restart. A saved account that is not enabled turns outgoing email off; with no saved account the host's `Qahwa:Email` settings are the fallback. The log-only stand-in is gone: with no usable SMTP account sending **fails with a clear message** (queued mail keeps its normal retries) instead of reporting a log line as delivery.
- **Links in email** use `Qahwa:Auth:PublicUrl` if the host sets it, otherwise the Email Account's site URL (an absolute http(s) address with no credentials, query or fragment), and in Development the request's own address. In production with none of them, the action refuses.
- **System Manager password actions** (Administrators only for users who hold the Administrator role): `POST /api/users/{email}/password` sets a password under the normal password policy, ends all of that person's sessions and clears their cached sessions; `POST /api/users/{email}/reset-password` emails a reset link (needs an enabled Email Account); `POST /api/email/test` sends a test message. The Desk offers a lock, a mail and a test-mail icon on the User and Email Account forms. Both password actions are written to the Access Log (new actions *Set Password* and *Send Password Reset*), never with the password or token.
- **Reset links** work for enabled users who have no password yet, expire after one hour, can be used once (they are bound to the password's version and to the user record's creation, so a deleted and re-made user cannot use an old link), allow three links per user per hour, and are refused for disabled users. Redeeming a link also ends the person's cached sessions.
- **Backups leave Email Account out** (it holds credentials); configure it again after a restore. Keep the ASP.NET Data Protection keys persistent, or the saved password cannot be read and the form asks for it again.

## Consequences
- Incoming mail (IMAP/POP) and OAuth SMTP sign-in are not implemented; one site-wide account only.
- A site that relied on log-only delivery in development must now configure an SMTP server (or a local catcher such as MailHog) to see email work.
