# ADR-029: Webhooks

**Status:** accepted (L10, part 3)

## Context
Other systems (an accounting package, a chat channel, a warehouse) should be told when documents change. A server that posts to addresses chosen by users is a classic way to attack internal networks (server-side request forgery), so safety matters as much as the feature.

## Decision
- **Hooks are data per DocType**, stored as one JSON row per DocType in `__webhook_set` (same store, manager, catalog and `modified` conflict check as workflows and rules), at most 10 per DocType: a name, active, an **event** (New, Update, Submit, Cancel, Delete), an optional **condition** (the expression language), an **address**, a **secret**, and optionally the **fields** to send. Managing them needs a System Manager.
- **Events become queued deliveries.** After a save or delete, each matching hook gets a row in the Core DocType `Webhook Delivery` (the JSON payload `{ event, doctype, name, timestamp, data }`). "Update" means a column really changed. The save never waits for, or fails because of, a webhook.
- **What is sent.** By default every real column that is not a password **and is at permission level 0** (so fields reserved for higher levels are never posted), plus the standard columns; or exactly the listed fields (passwords can never be listed). A payload over 256 KB is replaced by the names and `"truncated": true`.
- **Signed.** Each post carries `X-Qahwa-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body" with the secret>`, `X-Qahwa-Event` and `X-Qahwa-Delivery` (the delivery's id, for de-duplication); receivers verify the signature and reject old timestamps. The **secret is write-only**: at least 16 characters, never returned by the API (only `secretSet`), a blank value on save keeps the stored one, and the Desk can generate one.
- **Delivered by the scheduler**, in batches, each delivery **claimed** with a compare-and-swap (as for email), so several servers never send one twice. A non-2xx answer or a failure is retried after 1 minute, 5 minutes, 30 minutes and 2 hours, then marked **Failed** after the fifth attempt, with the reason. A delivery whose hook was removed or switched off is marked failed without sending. Everything is visible in the ordinary Webhook Delivery list.
- **Only public https addresses (SSRF).** The address must be `https` (plain http and local names only if `Qahwa:Webhooks:AllowInsecure` is on, for development), may not contain credentials, and may not be `localhost`, `.local`, `.internal`, a single-label name, or a private, loopback, link-local (cloud metadata `169.254.169.254`), carrier-grade NAT, multicast, reserved or documentation IP address, IPv4 or IPv6 (including IPv4-mapped forms). That is checked when saving, **and again when connecting**: the sender resolves the name itself and connects only to addresses that pass `NetworkGuard.IsPublic` (so a name that later points inside, or changes between check and connect, is refused). Redirects are never followed, connections time out after 5 seconds and the whole call after 10, cookies are not used, and the response body is not read.
- **Desk.** A Webhooks page (Tools): per hook the event, condition, address (with a quick hint), secret (with "Make one"), and the fields; errors appear under the hook.

## Security
Only System Managers manage hooks. The secret is stored in the hooks row (it must be, to sign), is never returned by the API or written to a delivery row, and is not logged. The guard is tested for dozens of address forms, and the real sender is tested to refuse loopback, private and metadata addresses and not to follow redirects. Payloads respect permission levels and never contain passwords.

## Consequences
- Not yet: custom headers and request templates, a "send a test" button, manual retry of a failed delivery, per-hook rate limits, mutual TLS, and receiver-side examples (the developer manual gives the verification recipe).
- Webhooks send the document as the system saw it, not as a particular person would; keep the field list tight for sensitive DocTypes.
