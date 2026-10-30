# ADR-120: Approve or reject from an email

**Status:** accepted (L8)

## Context
Approvers should not have to hunt for documents. A message saying "this is waiting for you" with the buttons in it is the usual answer, and also the usual security hole: a link that does something when opened gets "clicked" by mail scanners, forwarded, and kept in inboxes for years.

## Decision
- A state has **Email links** (`emailApprovers`). When a document moves into it through a workflow action, the system emails each person who can act on it now (a role of an outgoing step, the step's condition and the self-approval rule applied to that person, at most 50 people) one link per action, through the ordinary email queue.
- A link is a **key to a confirmation page**, not an action. `GET /api/workflow-links/{token}` only describes what the link would do; `POST .../confirm` does it. Both need a signed-in session (an unsigned visitor is sent to sign in and back), and both act only for **the person the email was written for**: anyone else, an expired, used or unknown link, and a document that has moved on all give the same "not valid any more", never saying which.
- Confirming does the action **as that person**, through the same code as the button on the form, so roles, conditions, several-people approvals (ADR-118) and the updates log all apply. The link is spent before the action runs (a second click, or a second server, finds nothing).
- Tokens are 256 random bits, stored only as a SHA-256 hash (`Workflow Link`), valid for 7 days, and removed whenever the document moves by a workflow action. They sit in the path, never in a query string, and the Desk page does not store them.
- The address in the link comes from configuration only (`Qahwa:Auth:PublicUrl`, or the site URL of the Email Account). With neither, no email is sent: there is no request to take a host from when the scheduler or a script moves a document, and a link built from a guessed host could be pointed at an attacker.
- A problem sending the emails never undoes the move.

## Consequences
- Not yet: a one-click (no sign-in) variant, links for people with no account, reminders that resend the links, links in the in-app bell.
- Receiving the mail needs an Email Account (SMTP) as for any other email.
