# ADR-124: Webhook headers and the test button

**Status:** accepted (L10)

## Context
Receivers often want a token in an `Authorization` header, and nobody trusts a webhook until they have seen one arrive. Both were missing: the only header was Qahwa's own signature, and the only way to try a webhook was to make a document and wait for the scheduler.

## Decision
- A webhook can carry up to 10 **extra headers** (name and value). They are sent with every delivery, before Qahwa's own headers.
- **Values are write-only**, exactly like the secret: the API and the Desk get the name and "a value is set", never the value; leaving a value blank on save keeps the stored one, typing a new one replaces it. (Like the secret, values are kept in the webhook's stored definition, readable by whoever can read the database; they are for receiver tokens, not for anything stronger than that.)
- **Names Qahwa or the connection needs are refused**: `Host`, `Content-Type`, `Content-Length`, `Transfer-Encoding`, `Connection`, `Cookie`, `User-Agent`, `Date`, ..., `Proxy-*`, `Sec-*`, `X-Forwarded-*` and everything starting `X-Qahwa-`, so a header can never forge the signature, redirect the request or smuggle another one. Names are letters, digits and dashes; values are plain printable characters (no line breaks), up to 500. Duplicates (in any case) are refused. On delivery the same check runs again and Qahwa's headers are written last.
- **Send a test**: `POST /api/webhooks/doctypes/{doctype}/test` sends one signed sample message (`event: ping`, `test: true`, no document data) to a **saved** webhook, now, through the same sender as real deliveries (public https addresses only, no redirects, time limit), with the same headers and signature. It works even when the webhook is switched off, is not stored as a delivery, needs a System Manager, is limited to one try every five seconds per webhook, and answers only with the status code or the connection error (never the receiver's reply text).
- The Desk's Webhooks page gets an "Extra headers" editor and a "Send a test" button next to each webhook (enabled once the webhook is saved and has no unsaved changes), with the result shown beside it.

## Consequences
- Not yet: per-webhook retry settings, a body template, resending a failed delivery from the delivery list.
