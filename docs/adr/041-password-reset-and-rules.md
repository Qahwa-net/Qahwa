# ADR-041: Password reset by email and password rules

**Status:** accepted (L10, security part 3)

## Context
People forget passwords, and a reset flow is a favourite way into an account (guessable links, links that reveal whether an address is registered, links built from a forged Host header, reset links that leak to administrators). Separately, "at least 10 characters" lets through "1234567890" and "password1234".

## Decision
- **Stateless signed link.** The link carries an authenticated-encrypted token (the same `ISecretProtector` as two-factor) naming the person, an expiry (**1 hour**) and the time their password last changed. Using the link, or changing the password any other way, changes that time, so every earlier link is dead and a link works **once**. Nothing is stored, so there is nothing for an administrator to read: the token is deliberately **not** put in the Email Queue (which System Managers can read) but sent directly, never written to a document.
- **No account enumeration.** `POST /api/auth/forgot` answers the same message for any input (known, unknown, malformed, empty). The mail is sent in the background so the time taken does not give the answer away. Three links an hour per address (no mail-bombing a victim) plus the login rate limit per connection.
- **The site address is configured, never guessed.** The link's base is `Qahwa:Auth:PublicUrl`. Only in Development is the request's own address used (and the link is written to the log so it can be tried without a mail server). In production without `PublicUrl` nothing is sent and a warning is logged: a forged Host header can never point a link at another site.
- **A reset ends every session** of the person and **does not switch off two-factor** (the password alone is still not enough).
- **Password rules** (also for creating a user and for changing the password): length 10 to 128; not the email; at least 4 different characters; not a sequence (`123456789`, `abcdefghij`, `9876543210`); not a very common password (with or without trailing digits); not mostly the person's own name or email. The check is on the letters and digits only, so `Password1!` is caught but a long phrase containing a word is fine.
- Desk: "Forgot your password?" on the sign-in page, a request page and a "choose a new password" page.

## Consequences
- Not yet: a breached-password list (only a short built-in list), email verification and self sign-up, a notice mail "your password was changed".

## Addendum: password expiry

`Qahwa:Auth:PasswordMaxAgeDays` (0 by default: passwords never expire). When a password is right but older than that, **sign-in gives no session**: the answer is `{ password_expired: true, token }`, a ten-minute token (encrypted, tied to the password it was made for, so changing the password ends it). `POST /api/auth/expired-password` takes the token, the **old password again** (counted like a sign-in, so guessing locks the account) and a new password that must pass the password rules and **differ from the old one**; it ends every session of the person. The person then signs in again with the new password, and **two-factor still applies** as usual. There is deliberately no half-privileged "must change" session, so nothing can be done with an expired password except choosing a new one. The sign-in page shows the form (ADR-039's code step and this step are both answers to a correct password).
