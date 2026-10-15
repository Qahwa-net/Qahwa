# ADR-028: API keys

**Status:** accepted (L10, part 2)

## Context
Scripts, integrations and other systems need to call the API without a person's password and without a browser cookie. The cookie login is right for the Desk; programs need something they can hold, rotate and revoke.

## Decision
- **A key stands for one person.** `Authorization: Bearer qk_<id>.<secret>` makes the request act as the key's owner, with their *current* roles and permissions (re-read on every request, so taking a role away or disabling the person takes effect at once). A key can never do more than its owner; there are no scopes beyond an optional **read-only** flag (only GET, HEAD and OPTIONS requests; anything else is refused with 403 "This API key is read-only").
- **Making keys.** A signed-in person makes keys for themselves on their Account page: a description, an optional expiry date (in the future) and read-only. At most 10 per person. The full token is returned **once**, in the response of the creation (`Cache-Control: no-store`), and never again.
- **Only a hash is stored.** The secret is 256 random bits (base64url); the database holds its SHA-256 (a slow hash is unnecessary for a value that cannot be guessed), in a field at permission level 9 that even System Managers cannot read through the API. Comparison is constant-time. The `key_id` (12 hex digits) finds the row and is not secret.
- **A key cannot make or delete keys.** Making and revoking require a real session, so a stolen key cannot mint more of itself or hide its tracks by deleting others.
- **Revoking and expiry.** A person deletes their own keys (a System Manager can list and delete anyone's). A deleted, expired or disabled-owner key authenticates as nobody (401). `last_used` is recorded at most every five minutes, as bookkeeping that can never refuse a good key.
- **Where.** `CookieUserContextResolver` checks for the Bearer key first, then the cookie; every endpoint therefore accepts keys with the same permission checks as for a person. Desk: an "API keys" section on the Account page with a create dialog, a one-time token dialog with a copy button, and delete.

## Security
Keys are high-entropy random values, never logged or listed, shown once, stored only as a hash in a hidden field, compared in constant time, and limited by the owner's permissions; malformed tokens (wrong prefix, lengths, characters, SQL-shaped text) are refused before any lookup that could be abused. CSRF does not apply because a key is a header, not a cookie. Tests cover wrong, truncated, swapped and malformed tokens, expiry, revocation, disabling the owner, role changes, read-only keys, keys making keys, and the real HTTP pipeline.

## Consequences
- Not yet: scopes per DocType, IP allow-lists, rate limits per key, key rotation reminders, an admin page of all keys, secrets shown as a pair (key and secret) for OAuth-style clients, and OAuth itself.
- Webhooks (outgoing) and attachments are the next L10 parts.
