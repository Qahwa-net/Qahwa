# Set up email (outgoing and incoming)

Qahwa sends email for password resets, notification rules, documents you email, emailed reports, workflow links and script reminders. All of it goes through **one site-wide email account** that a System Manager sets up in the Desk. Until it is set up (and enabled) no email leaves the system: messages wait in the **Email Queue** and fail.

## Where to find it

- **Sidebar → Settings → Email Account** (the **Settings** group is under your workspaces; it also holds Email Template, Email Queue, Brand, Naming Series, Workspaces, Role Permissions and Backup & Restore).
- Or press **Ctrl+K** (the search box) and type `email` or `smtp`.
- Or **Developer Hub → Connect other systems → Email Account**.
- Direct address: `/app/Email Account`.

Only a **System Manager** can open it.

## Set up outgoing email (SMTP)

1. Open **Email Account**.
2. Tick **Enable outgoing email**.
3. **Sender email address**: the address messages come from, for example `noreply@yourcompany.com`.
4. **SMTP server** and **SMTP port**: from your mail provider (table below).
5. **Use STARTTLS**: keep it ticked for port 587. (Port 465, which encrypts from the first byte, is **not supported**: use 587.)
6. **SMTP username** (usually the full email address) and **SMTP password or app password**. The password is encrypted on the server and shown masked afterwards.
7. **Public site URL**, for example `https://desk.yourcompany.com`: used in the links of password-reset and workflow emails.
8. **Save**, then click the **envelope icon** in the header to **send a test email** to yourself. If it arrives, you are done.

| Provider | SMTP server | Port | Notes |
|---|---|---|---|
| Gmail / Google Workspace | `smtp.gmail.com` | 587 | Turn on 2-step verification and create an **app password**; use it as the password |
| Microsoft 365 | `smtp.office365.com` | 587 | SMTP authentication must be allowed for the mailbox; use the mailbox address as username |
| Your own server / relay | its host name | 587 (or 25 for an unauthenticated relay inside your network) | Leave username empty for a relay that needs none |
| A local test mailbox such as Mailpit | `127.0.0.1` (from Docker: `host.docker.internal`) | 1025 | Untick STARTTLS; nothing is delivered, you read the mail in the Mailpit page |

## Read incoming mail (IMAP), optional

Tick **Read incoming mail (IMAP)** to turn replies into **Communication** documents on the document they answer (a reply keeps the reference that outgoing messages carry in the subject).

1. **IMAP server** (`imap.gmail.com`, `outlook.office365.com`), **IMAP port** (993; with STARTTLS ticked, 143).
2. **IMAP username** and **IMAP password or app password**.
3. **Folder to read** (default `INBOX`).
4. Save. The mailbox is **only read**: nothing is deleted, moved or marked as read.

## Check that mail is going out

- **Email Queue** (Settings group) lists every message with its status: *Pending*, *Sending*, *Sent* or *Failed*. A failed one shows the reason (wrong password, server unreachable). A message is tried three times.
- Messages go out within a few minutes (the scheduler sends them).
- You can send up to 50 emails a day yourself; scripts follow the same limit.

## If it does not work

| What you see | Cause |
|---|---|
| Test email fails: *authentication failed* | Wrong username or password. Gmail and Microsoft need an **app password** (or allowed SMTP authentication), not the normal password |
| *Could not connect* | Wrong server or port, or the server cannot reach the internet or mail host (in Docker, `localhost` is the container itself: use the host's name or `host.docker.internal`) |
| Port 465 | Not supported: use 587 with STARTTLS |
| Messages stay *Pending* | The account is not enabled, or the scheduler is off (`Qahwa:Scheduler:Enabled`) |
| Everything is *Failed* after restoring a backup | Backups do not include the Email Account: set it up again |
| Password-reset links point to the wrong address | Set **Public site URL** |

## For administrators and developers

- The same account can come from the server's settings instead (`Qahwa__Email__Host`, `Port`, `UseSsl`, `User`, `Password`, `From`; in Docker add them to the `app` service's `environment`). **A saved Email Account in the Desk takes over** from those settings, and a saved but *disabled* account turns delivery off.
- Passwords are encrypted with the server's data-protection keys: keep the keys persistent across restarts and deployments.
- API: `POST /api/email/test` with `{ "to": "person@example.com" }`; the account is the single document `/api/resource/Email%20Account/Email%20Account`. More in [User passwords and outgoing email](69-user-passwords-and-email-account.md) and [Email](13-email.md).
