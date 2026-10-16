# ADR-034: Web forms (public intake)

**Status:** accepted (L10, part 5)

## Context
Customers and visitors should be able to send an enquiry, a request or an application without an account. A page that creates documents for anonymous people is the most exposed part of any system, so it is designed from what must never happen: reading data, writing fields the form owner did not intend, flooding, and turning a visitor's browser (or session) against the system.

## Decision
- **A form is data.** The Core DocType `Web Form` (address, title, published, DocType, fields to ask, introduction, message, most submissions per hour) is edited by System Managers only. The public address is `/forms/<address>`; addresses are `[a-z0-9-]`, up to 61 characters.
- **Fail closed.** At every use the form is checked again against the DocType: it must be published, target a DocType made in the Desk or an app (never a framework DocType, child table or single), list only plain ordinary fields (text, numbers, dates, choices, yes/no, email, phone, URL; not passwords, links, attachments, tables, hidden, read-only, calculated or higher-permission-level fields), and ask for every field the DocType requires. Anything else makes the form "not available" (404) instead of half working, whoever edited it and however.
- **Only what was listed.** A submission may contain only the listed fields (anything else is a 400), plain values (text up to 5000 characters, numbers, yes/no), in a JSON body of at most 32 KB sent as `application/json` (which another site cannot send without a CORS preflight). Values are validated by the normal document engine, so required fields, choices, types and unique rules apply, and the visitor sees errors for the fields they were asked, nothing else.
- **No session, no ambient authority.** The endpoints never read the cookie or an API key, so a signed-in person visiting a hostile page cannot be used to submit, and a signed-in person gets no extra powers. Documents are created by the system user `web-form@guest` (so its owner shows where the document came from); the DocType's own rules, assignment rules and notifications run as usual.
- **Rate limits.** Per source address: a framework limit of 60 requests a minute across public forms, plus the form's own hourly limit per source (default 10), plus a total cap of 50 times that for the form. The counters are in memory (each server counts for itself) and bounded. Over the limit is a 429.
- **A trap for bots.** A hidden field (`website`) that people never fill in: if it has a value the submission is answered like a success and dropped.
- **Nothing leaks.** The answer is only the success message, never the document's name or content; responses are `no-store`; the form page renders everything as text.

## Consequences
- Visitors can send enquiries with no account; a manager decides exactly which fields.
- Not yet: a CAPTCHA, email confirmation, attachments, editing a submission after sending, multi-step forms, per-field limits, limits shared across servers (needs a shared store).
