# ADR-039: Two-factor sign-in (authenticator app)

**Status:** accepted (L10, security part 1)

## Context
A password alone can be guessed, phished or reused from another breach. Two-factor sign-in asks for something the person has (their phone) as well.

## Decision
- **TOTP (RFC 6238)**: 6 digits, 30 seconds, HMAC-SHA1, which every authenticator app supports. Tested against the RFC's published vectors.
- **Two steps.** `POST /api/auth/login` checks the password. For an account with two-factor on it returns only a **challenge** (an authenticated-encrypted token valid for 5 minutes, naming the person) and sets **no cookie**; `POST /api/auth/login/2fa` with the challenge and a code (or a recovery code) creates the session. A forged, altered or expired challenge is refused.
- **Guessing is limited.** A wrong code counts as a failed sign-in on the same counter as wrong passwords (5 wrong in a row lock the account for 15 minutes, and a locked account is refused without looking at the code). The password step no longer clears that counter by itself (it is cleared when the whole sign-in succeeds), so guessing codes in rounds, with a password sign-in between rounds, does not work. Both endpoints are also rate limited per address.
- **A code works once.** The last used time step is stored and advanced atomically (one SQL statement), so replaying a code, or two requests at once with the same code, fails. One step either side of now is accepted for clock drift.
- **Recovery codes**: 10 one-time codes (`XXXXX-XXXXX`), shown once when two-factor is turned on, stored only as SHA-256 hashes, accepted in any case with or without the dash, removed when used (compare-and-swap).
- **Secrets at rest.** The authenticator secret is encrypted with ASP.NET Core Data Protection (`ISecretProtector`) in the `__totp` table, outside every DocType, so no API can read it. With several servers, share the Data Protection key ring or a secret made on one cannot be read on another.
- **Setup and removal.** Setup creates a pending secret that does nothing until a right code is entered; turning it off needs the password **and** a code (or recovery code); an administrator (System Manager) can reset someone who lost their phone and recovery codes, after which the password alone works again (and they can set it up afresh). API keys can neither read nor change two-factor settings. The QR code is drawn in the browser and shown as an image, so the secret never goes to a third-party service.
- A re-created account (same email, new User document) does not inherit the old secret.

## Consequences
- Not yet: making two-factor mandatory for a role, SMS or email codes, WebAuthn/passkeys, remembering a trusted device, logging two-factor events.
