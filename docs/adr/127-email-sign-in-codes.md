# ADR-127: Sign-in codes by email

**Status:** accepted (L10, follows ADR-039)

## Context
An authenticator app is the better second factor, but not everyone has one. A code sent to the person's mailbox is a weaker but much easier second step: it stops a stolen or guessed password on its own, which is most of the risk.

## Decision
- A person can turn on **email codes** on their Account page. Turning it on (and off) is confirmed with a code mailed to their address; turning it off also needs the password.
- With it on, `POST /api/auth/login` checks the password and then **emails a six-digit code** and returns a challenge (`method: "email"`); `POST /api/auth/login/2fa` with the challenge and the code creates the session. A person who has an authenticator app is asked for the app code instead (the app takes precedence).
- **Nothing readable is stored.** The challenge is an encrypted, signed token that carries a keyed hash of the code (code, a random nonce, the person and the purpose), so a leaked database holds no codes. A code works **once** (the nonce is remembered until it expires; across several servers a code could in theory be tried once on each, within its ten minutes), lasts **ten minutes**, and only works for what it was asked for (a sign-in code cannot turn the option on or off, and the other way round).
- **Guessing is limited** exactly as for the app: a wrong code counts as a failed sign-in on the same counter as wrong passwords (five lock the account for fifteen minutes, and a locked account is refused without looking at the code). Asking for codes is limited to five per person per fifteen minutes (429).
- The flag is a field on the User (`email_two_factor`, permission level 9, so it is not visible or editable through the document API). API keys can neither read nor change it. A System Manager's "reset two-factor" clears it as well as the app secret.
- The code is sent straight away (not through the email queue, which the scheduler empties every few minutes), through the site's outgoing Email Account.

## Consequences
- **It needs working outgoing email.** If the mail server is down, a person with email codes cannot sign in until it is back; a System Manager can reset their two-factor from the Users page to let the password alone work.
- It is only as strong as the mailbox: anyone who can read the person's email can both reset the password and read the code. Prefer the authenticator app where it is possible.
- Not yet: SMS codes, passkeys, making two-factor mandatory for a role, remembering a trusted device.
