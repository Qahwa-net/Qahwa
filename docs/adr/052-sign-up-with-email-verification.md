# ADR-052: Self sign-up with email verification

**Status:** accepted (L10)

## Context
Some systems (a customer portal, a community, a small business) let people make their own account. Done carelessly this lets anyone register someone else's address, squat on it, flood an inbox with mail, learn which addresses already have accounts, or smuggle text into a message sent from the system's own address.

## Decision
- **Off unless `Qahwa:Auth:SignUp:Enabled` is true.** When off, the sign-up endpoint answers 404 and old links do nothing.
- **Nothing is stored until the address is proven.** The email carries a link with an encrypted token (`signup|address|name|expiry`, 24 hours, `LinkHours` up to 7 days). The account is made only when the link is used, with the password chosen on that page. So an unconfirmed address never becomes an account, nobody can occupy someone else's address, nothing needs cleaning up, and no password (or password hash) ever travels by email.
- **The link works once**: it is refused when the address already has an account (an administrator may have made one in the meantime), and two clicks at once make one account (the second finds it there).
- **No account enumeration.** The answer to "sign up" is the same for a new and an existing address. An address that already has an account (enabled or not) is sent a note saying so with sign-in and reset links, never a link that could take it over; a disabled account is never revived. Format problems (bad address, bad name, domain not allowed) are reported, as they say nothing about accounts.
- **Mail is limited**: three per address per hour and ten per source address per hour, then nothing is sent (same answer). The endpoints also use the sign-in rate limit.
- **The name cannot carry links or markup**: letters of any script, digits, spaces and `. - ' ,` only, up to 140 characters, because it appears in a message sent to an address that may belong to someone else.
- **`AllowedDomains`** restricts addresses to exact domains (sub-domains and look-alikes do not match). **`DefaultRoles`** are the only roles a new account gets (plus the automatic `All`); the Administrator role is never given this way. The link URL comes from `Qahwa:Auth:PublicUrl`, never the Host header.
- `AuthService.CreateUserAsync` makes a strictly new account (it fails with a conflict if there is one, unlike `EnsureUserAsync`, which resets passwords and re-enables).
- **Endpoints:** `GET /api/auth/signup` (is it on), `POST /api/auth/signup` `{email, full_name}`, `POST /api/auth/signup/confirm` `{token, password}`. Desk: `/sign-up`, `/sign-up/confirm`, and a "Create an account" link on the sign-in page when it is on.

## Consequences
- No CAPTCHA or other bot check beyond the limits; a bot can cause up to ten mails an hour from one address.
- No approval step by an administrator (the roles are fixed by configuration); it can be added with a workflow on a request DocType later.
- A person who is signed up by an attacker's request and clicks the link anyway chooses their own password on that page, so the attacker gains nothing.
