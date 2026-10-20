# ADR-095: Incoming mail (IMAP)

Status: accepted

## Context
The site could send email (ADR-090) but not receive it. People want mail sent to a support or sales address to appear in Qahwa. Reading a mailbox means connecting to an outside server with a stored password and then handling text written by strangers.

## Decision
- **IMAP only, and only over an encrypted connection**: TLS from the start (port 993) or STARTTLS (port 143). Certificates are checked. POP and unencrypted IMAP are not offered.
- **Settings live in Email Account** (`imap_*` fields). The password is a Password field, so it is encrypted on the server and shown masked. Incoming mail is off until a System Manager turns it on.
- **Read-only.** The folder is opened read-only: nothing is deleted, moved or marked as read, so the mailbox stays the owner's copy.
- **New mail only.** The first poll only notes where the folder ends; mail already there is not imported. A cursor (last message number and the folder's UIDVALIDITY) is saved without changing the account's modified time. If the server renumbers the folder, the system notes where it ends now and carries on.
- **Polled by the scheduler** (step "incoming mail", logged in the job log), at most 50 messages per poll (the rest follow on the next poll). A failing server is an error in the job log and loses nothing.
- **Each message becomes a `Communication` document** (type Received): sender, sender name, subject, date, plain text, Message-ID and In-Reply-To. Only System Managers can read it.
- **Hostile mail is expected.** Only text is kept: HTML is reduced to its text, attachments are ignored, nothing is opened or followed. Messages over 512 KB are not downloaded. Control characters are removed; the subject is cut to 300 characters and the text to 100,000. A message without a plain `name@host` sender is dropped, and the same Message-ID is kept once. The mailbox is never trusted as a source of instructions.
- The Desk shows the text as plain text. The connection test (`POST /api/email/test-incoming`, System Manager) reports the folder size and hides server error details.

## Consequences
- Threads, replying from Qahwa, attachments, POP, OAuth sign-in to the mailbox and several accounts are not part of this step.
- One mailbox per site. The mailbox library is MailKit (`Qahwa.Hosting`); tests use a stand-in.
