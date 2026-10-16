# ADR-040: Server-side sessions (list, revoke, absolute limit)

**Status:** accepted (L10, security part 2)

## Context
A signed cookie alone cannot be taken back: a stolen laptop or an old phone stays signed in until the cookie expires, and there is no way to see where an account is signed in.

## Decision
- **A record for every sign-in** in the `__sessions` table: a random 128-bit id, the person, when it started, last used, its absolute end, the connection's address and the browser's own description (shortened, control characters removed). Forwarded headers are never trusted for the address.
- **The cookie carries only the id** (as a claim), together with the password version it already had. On every request the id must name a live session of that person: not revoked, not past its end, and the person must match. The check is cached for the same few seconds as the user check (`SessionCacheSeconds`), so ending a session takes effect at once on the server where it was ended and within seconds on others. A cookie without a session id (made before this change) is simply nobody: sign in again.
- **Absolute limit.** A session ends `AbsoluteSessionDays` (default 14) after sign-in however active it is; the cookie still slides within it.
- **People manage their own:** `GET /api/auth/sessions` lists live ones (marking this device), `DELETE /api/auth/sessions/{id}` ends one, `POST /api/auth/sessions/revoke-others` ends all but this one. Nobody can see or end anyone else's (another person's id is "not found"). A System Manager can sign someone out everywhere (`revoke-user`). Changing the password already ended other cookies (password version); it now also revokes the other session records. API keys cannot use these endpoints.
- **Housekeeping** is done on the way: now and then a sign-in deletes sessions that ended more than a day ago, so there is no extra job.
- The Account page shows "Where you are signed in" with a sign out button for each place and one for all others.

## Consequences
- A lost device can be cut off without changing the password.
- Not yet: impersonation ("login as user"; deliberately left out, it is a powerful tool that needs its own audit trail), mapping addresses to places, alerts for new sign-ins, limiting the number of concurrent sessions.
