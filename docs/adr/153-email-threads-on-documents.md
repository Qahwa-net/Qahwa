# ADR-153: Email threads on documents

**Status:** accepted

## Context
Sending email from a document (ADR-090) and keeping received mail as Communications (ADR-095) were separate: nothing connected a customer's reply to the quote it was about. "Threads on documents" was listed as not yet done.

## Decision
- **A reference in the subject.** An email sent *from a document* gets ` [QW-<10 hex>]` appended to its subject (random, one per send; the Email Queue rows of one send share it in the new `thread_ref` field). Other system emails (notifications, codes) are unchanged.
- **Matching replies.** When the mailbox is read, a subject containing `QW-<10 hex>` that matches a queued email's reference sets the Communication's *About* fields to that email's document. Matching is by the exact random reference only: nothing else about a message (sender, headers, text) can attach it to a document.
- **The conversation** (`GET /api/resource/{doctype}/{name}/conversation`) is the emails sent about the document (one entry per send, with its recipients and whether it was delivered, is waiting or failed) and the replies matched to it, oldest first, at most 200. The reference is removed from the subjects shown.
- **Who sees it:** anyone who can read the document (the same check notifications use, including shares and User Permissions); anyone else gets "not found". The mail is part of the document's record, so people need no access to Communication itself. The Desk shows an **Emails** section on the form of non-Core documents when there is something to show; text is shown as plain text.

## Consequences
- Tested: a reply that keeps the reference lands on the document and follows what was sent, a reply with an unknown reference is not attached, one send to several people is one entry, and only readers see the conversation. Existing email tests now expect the reference in document emails.
- Not exercised against a real mailbox or in a browser in this change.
- Not yet: replying from the panel (use the Email dialog), matching on `In-Reply-To` for clients that drop the subject reference, attachments, HTML.
- Anyone holding a reference can attach a plain-text reply to that document; it is random (40 bits) and given only to the recipients, which is the same trust as a reply-to address.
