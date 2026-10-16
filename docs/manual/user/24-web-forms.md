# Web forms

A **web form** lets anyone, without an account, send answers that become a document, for example an enquiry or a request. You decide exactly which fields it asks.

## Making a form (System Manager)
1. Open **Web Form** in the sidebar and choose **New**.
2. **Address**: letters, digits and dashes, for example `contact`. The form will be at `/forms/contact`.
3. **Creates documents of this DocType**: a DocType you made in the Desk (not a framework DocType).
4. **Fields to ask**: one field name per line.
5. Tick **Published** when ready, and share the link.

A form is **not available** (visitors see "This form is not available") when it is not published, when the DocType is not allowed, when it asks for a field that cannot be asked (passwords, links, attachments, tables, hidden, read-only, calculated or higher-permission fields) or when it leaves out a field the DocType requires. Fix the list and it works again.

## What happens when someone sends it
- The answers are checked like any document (required fields, choices, numbers).
- A document is created and shows as created by `web-form@guest`. Your assignment and notification rules run as for any new document.
- The visitor only sees your message, never the document.

## Protection against misuse
- Each person can send **10 per hour** by default (change **Most submissions per person per hour**); the form as a whole is capped too.
- Only the listed fields are accepted and values are limited in size.
- A hidden field catches simple bots.
