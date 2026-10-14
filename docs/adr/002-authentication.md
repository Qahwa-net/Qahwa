# ADR-002: Cookie sessions with the Identity password hasher (not full ASP.NET Identity)

**Status:** accepted (Phase 1)

## Context
The plan called for ASP.NET Core Identity. Identity's stores are built around EF Core entities and its own user/role tables.
In Qahwa a user is a `User` document like any other, with permissions, API and UI generated from metadata, and roles are
`Has Role` documents. Running Identity's tables next to them would mean two sources of truth for users.

## Decision
- Users, roles and role grants are DocTypes (`User`, `Role`, `Has Role`), shipped embedded in `Qahwa.Core`. The email is the document name and is stored lower case.
- Password hashes are **never** in a DocType. They live in the `__auth` table behind `IAuthStore`, so no generic API can read or write them.
- Hashing uses `PasswordHasher<string>` from `Microsoft.Extensions.Identity.Core` (PBKDF2-HMAC-SHA512, 210,000 iterations by default, automatic rehash when the work factor rises).
- Sessions are ASP.NET Core cookie authentication: `HttpOnly`, `SameSite=Lax`, `Secure` outside Development, session-lifetime cookie, 8 h sliding. The cookie only carries the email and a password version.
- On every request the user is re-checked against the database (cached for `SessionCacheSeconds`, default 15 s). Disabling a user, changing roles, or changing the password therefore affects open sessions within that window; a password change on this node takes effect immediately.
- Failed logins: constant-ish timing for unknown users (dummy hash), one generic `invalid_credentials` response, atomic failure counter in SQL, lockout after 5 failures for 15 minutes, and a per-IP rate limit on login and change-password.
- A credential only counts while it matches the `User` document's creation time, and `Has Role` rows only count if created after the user. Deleting and re-creating a user with the same email never revives the old password or roles.
- The first administrator is created with `qahwa create-admin`. The password is prompted for or read from stdin, never passed as an argument.
- The earlier development-only header login was removed from the product. Tests use a header resolver defined only in the test project.

## Consequences
- Anyone with the `System Manager` role can grant any role, including `Administrator`. Restricting who may grant `Administrator` is a follow-up.
- Behind a reverse proxy, forwarded-header handling must be configured or the rate limiter sees the proxy's address.
- CSRF is mitigated by `SameSite=Lax` plus JSON-only bodies; do not enable CORS for untrusted origins.
- Not done yet: password reset by email, JWT / API keys for machine clients, two-factor authentication, a password breach-list check, and per-user session listing/revocation.
