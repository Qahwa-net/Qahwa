# Email: send mail about a document

## Emailing from a document

1. Open the document and click the **Email** icon (an envelope) at the top.
2. Type one or more addresses, separated by commas or spaces (up to 10).
3. Optionally choose a **Template**: the subject and message are filled in with this document's details, and you can still change them.
4. Check the **Subject** and **Message** and click **Send**.

The message is queued and goes out within a few minutes; a note "Emailed ..." appears in the document's Activity. You need permission to change the document, and you can send up to 50 emails a day. Messages are plain text.

## Templates (System Managers)

Open **Email Template** in the sidebar (Core) and add one: a name, a subject and a message. Put `{{customer}}` (any field of the document) where its value should appear; `{{name}}` is the document's ID, `{{doctype}}` its kind and `{{user}}` the person sending. Optionally say which DocType the template is for; it is then only offered for those documents.

## Emails from notification rules

In **Notification Rules**, tick **Also send an email** on a rule. People it tells in the bell also get the same text by email. They must be allowed to open the document.

## For administrators

- **Email Queue** (sidebar, Core) lists every email with its status. A failed one shows the reason; a message is tried three times.
- Outgoing mail needs the server to be configured with an SMTP server (`Qahwa:Email:Host`, `Port`, `UseSsl`, `User`, `Password`, `From`; see the developer manual). Until then, emails are only written to the server log.
