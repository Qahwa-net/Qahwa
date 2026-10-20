# ADR-094: Qahwa as an OAuth 2.0 provider

Status: accepted

## Context
Other programs (a reporting tool, a mobile app, another website) need to act for a Qahwa user without ever seeing the password. API keys (ADR-040) are made by the person for their own scripts; there was no way for a program to ask a person for permission. OpenID Connect sign-in (ADR-048) is the other direction (Qahwa signs people in with an outside provider).

## Decision
- **Authorization-code flow with PKCE, and nothing else.** There is no implicit flow and no password grant. PKCE with `S256` is **required for every client**, public or confidential. Endpoints: the consent page `GET /oauth/authorize` (a Desk route), `GET/POST /api/oauth/authorize` (the calls the Desk makes), `POST /api/oauth/token`, `POST /api/oauth/revoke` (RFC 7009), `GET /api/oauth/userinfo`, `GET /api/oauth/metadata` (RFC 8414). Token requests are form-encoded, answered with `Cache-Control: no-store`, rate limited per client, and errors use RFC 6749 names (`invalid_grant`, `invalid_client`, …).
- **Clients** are registered by System Managers (Desk: Tools → OAuth Applications; `POST /api/oauth/clients`): a name, redirect addresses, the permissions it may ask for, public or confidential. A confidential client's secret is shown once and only its hash is stored. Redirect addresses must match **exactly**; they may be `https`, `http` only for localhost, or an app's own reverse-domain scheme; never a fragment, credentials, `javascript:`, `data:` or similar. A bad client or redirect is shown to the person and **nothing is ever sent to that address**.
- **Scopes:** `profile` (name and email through user-info), `api` (read through the API as the person) and `api:write` (change data as the person). A token carries the person's own permissions, never more, and reads their roles fresh on every request; it never stands for a signed-in session, so it cannot make or delete API keys, applications, backups or other tokens (the same rule as API keys). Without `api:write` it is read-only (a write answers 403).
- **Tokens:** random 256-bit values with a prefix (`qc_` code, `qo_` access, `qr_` refresh); only the SHA-256 hash is stored. Codes live 10 minutes and work once; **using a code a second time withdraws every token issued from it**. Access tokens live one hour; refresh tokens 30 days and **rotate**: a used refresh token presented again withdraws the whole approval. Everything stops when the person is disabled, the client is switched off or deleted, or the person withdraws access (Account → Applications you allowed; `DELETE /api/oauth/connected/{clientId}`). Expired rows are removed by the scheduler.
- The consent page shows who is asking, the permissions in plain words and the host the person will be sent to; the Desk only follows the address the server returns.

## Consequences
- OAuth scopes are coarse (read or write everything the person may). Per-DocType scopes are future work.
- Tokens are looked up in the database on each request (like API keys), which is fine at normal volumes.
- Dynamic client registration, the implicit and client-credentials grants and ID tokens are not provided; use `userinfo`.
